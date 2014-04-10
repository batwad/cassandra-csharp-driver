//
//      Copyright (C) 2012 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Diagnostics;
﻿using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Cassandra
{

    public class Session : ISession
    {
        internal Guid Guid;

        private readonly Logger _logger = new Logger(typeof(Session));
        
        private readonly Cluster _cluster;

        internal readonly Policies Policies;
        private readonly ProtocolOptions _protocolOptions;
        private readonly PoolingOptions _poolingOptions;
        private readonly SocketOptions _socketOptions;
        private readonly ClientOptions _clientOptions;
        private readonly IAuthProvider _authProvider;
        private readonly IAuthInfoProvider _authInfoProvider;
        
        public string Keyspace { get { return _keyspace; } }
        private string _keyspace;
        private int _binaryProtocolVersion;

        public Cluster Cluster { get { return _cluster; } }

        readonly ConcurrentDictionary<IPAddress, ConcurrentDictionary<Guid, CassandraConnection>> _connectionPool = new ConcurrentDictionary<IPAddress, ConcurrentDictionary<Guid, CassandraConnection>>();
        readonly ConcurrentDictionary<IPAddress, AtomicValue<int>> _allocatedConnections = new ConcurrentDictionary<IPAddress, AtomicValue<int>>();

        internal Session(Cluster cluster,
                         Policies policies,
                         ProtocolOptions protocolOptions,
                         PoolingOptions poolingOptions,
                         SocketOptions socketOptions,
                         ClientOptions clientOptions,
                         IAuthProvider authProvider,
                         IAuthInfoProvider authInfoProvider,
                         string keyspace,
                         int binaryProtocolVersion)
        {
            _binaryProtocolVersion = binaryProtocolVersion;
            _cluster = cluster;

            _protocolOptions = protocolOptions;
            _poolingOptions = poolingOptions;
            _socketOptions = socketOptions;
            _clientOptions = clientOptions;
            _authProvider = authProvider;
            _authInfoProvider = authInfoProvider;

            Policies = policies ?? Policies.DefaultPolicies;

            Policies.LoadBalancingPolicy.Initialize(_cluster);

            _keyspace = keyspace ?? clientOptions.DefaultKeyspace;

            Guid = Guid.NewGuid();
        }

        public int BinaryProtocolVersion { get { return _binaryProtocolVersion; } }

        Timer _trashcanCleaner;

        internal void Init(bool allock=true)
        {
            if (allock)
            {
                var ci = Policies.LoadBalancingPolicy.NewQueryPlan(null).GetEnumerator();
                if (!ci.MoveNext())
                {
                    var ex = new NoHostAvailableException(new Dictionary<IPAddress, List<Exception>>());
                    _logger.Error(ex.Message);
                    throw ex;
                }

                var triedHosts = new List<IPAddress>();
                var innerExceptions = new Dictionary<IPAddress, List<Exception>>();
                int streamId;
                var con = Connect( ci, triedHosts, innerExceptions, out streamId);
                con.FreeStreamId(streamId);
            }

            _trashcanCleaner = new Timer(TranscanCleanup, null, Timeout.Infinite, Timeout.Infinite);
        }

        readonly ConcurrentDictionary<IPAddress, ConcurrentDictionary<Guid, CassandraConnection>> _trashcan = new ConcurrentDictionary<IPAddress, ConcurrentDictionary<Guid, CassandraConnection>>();

        void TranscanCleanup(object state)
        {
            _trashcanCleaner.Change(Timeout.Infinite, Timeout.Infinite);

            foreach (var kv in _trashcan)
            {
                foreach(var ckv in kv.Value)
                {
                    CassandraConnection conn;
                    if(kv.Value.TryRemove(ckv.Key,out conn))
                    {
                        if (conn.IsEmpty())
                        {
                            _logger.Info("Connection trashed");
                            FreeConnection(conn);
                        }
                        else
                        {
                            kv.Value.TryAdd(conn.Guid,conn);
                        }
                    }
                }
            }
        }

        void TrashcanPut(CassandraConnection conn)
        {
            RETRY:
            if (!_trashcan.ContainsKey(conn.GetHostAdress()))
                _trashcan.TryAdd(conn.GetHostAdress(), new ConcurrentDictionary<Guid, CassandraConnection>());

            ConcurrentDictionary<Guid, CassandraConnection> trashes;
            if (_trashcan.TryGetValue(conn.GetHostAdress(), out trashes))
                trashes.TryAdd(conn.Guid,conn);
            else
                goto RETRY;

            _trashcanCleaner.Change(10000, Timeout.Infinite);
        }

        CassandraConnection TrashcanRecycle(IPAddress addr)
        {
            if (!_trashcan.ContainsKey(addr))
                return null;

            ConcurrentDictionary<Guid, CassandraConnection> trashes;
            if (_trashcan.TryGetValue(addr, out trashes))
            {
                foreach(var ckv in trashes)
                {
                    CassandraConnection conn;
                    if(trashes.TryRemove(ckv.Key,out conn))
                        return conn;
                }
            }

            return null;
        }

        internal CassandraConnection Connect(IEnumerator<Host> hostsIter, List<IPAddress> triedHosts, Dictionary<IPAddress, List<Exception>> innerExceptions, out int streamId)
        {
            CheckDisposed();

            while (true)
            {
                var currentHost = hostsIter.Current;
                if (currentHost == null)
                {
                    var ex = new NoHostAvailableException(innerExceptions);
                    _logger.Error("All hosts are not responding.", ex);
                    throw ex;
                }
                if (currentHost.IsConsiderablyUp)
                {
                    triedHosts.Add(currentHost.Address);
                    var hostDistance = Policies.LoadBalancingPolicy.Distance(currentHost);
                RETRY_GET_POOL:
                    if (!_connectionPool.ContainsKey(currentHost.Address))
                        _connectionPool.TryAdd(currentHost.Address, new ConcurrentDictionary<Guid, CassandraConnection>());

                    ConcurrentDictionary<Guid, CassandraConnection> pool;

                    if (!_connectionPool.TryGetValue(currentHost.Address, out pool))
                        goto RETRY_GET_POOL;

//                    CassandraCounters.SetConnectionsCount(currentHost.Address, pool.Count);
                    foreach (var kv in pool)
                    {
                        CassandraConnection conn = kv.Value;
                        if (!conn.IsHealthy)
                        {
                            CassandraConnection cc;
                            if(pool.TryRemove(conn.Guid, out cc))
                                FreeConnection(cc);
                        }
                        else
                        {
                            if (!conn.IsBusy(_poolingOptions.GetMaxSimultaneousRequestsPerConnectionTreshold(hostDistance)))
                            {
                                streamId = conn.AllocateStreamId();
                                return conn;
                            }
                            else
                            {
                                if (pool.Count > _poolingOptions.GetCoreConnectionsPerHost(hostDistance))
                                {
                                    if (conn.IsFree(_poolingOptions.GetMinSimultaneousRequestsPerConnectionTreshold(hostDistance)))
                                    {
                                        CassandraConnection cc;
                                        if (pool.TryRemove(conn.Guid, out cc))
                                            TrashcanPut(cc);
                                    }
                                }
                            }
                        }
                    }
                    {

                        var conn = TrashcanRecycle(currentHost.Address);
                        if (conn != null)
                        {
                            if (!conn.IsHealthy)
                                FreeConnection(conn);
                            else
                            {
                                pool.TryAdd(conn.Guid, conn);
                                streamId = conn.AllocateStreamId();
                                return conn;
                            }
                        }
                        // if not recycled
                        {
                            Exception outExc;
                            conn = AllocateConnection(currentHost.Address, hostDistance, out outExc);
                            if (conn != null)
                            {
                                if (_cluster.Metadata != null)
                                    _cluster.Metadata.BringUpHost(currentHost.Address, this);
                                pool.TryAdd(conn.Guid, conn);
                                streamId = conn.AllocateStreamId();
                                return conn;
                            }
                            else
                            {
                                if (!innerExceptions.ContainsKey(currentHost.Address))
                                    innerExceptions.Add(currentHost.Address, new List<Exception>());
                                innerExceptions[currentHost.Address].Add(outExc);
                                _logger.Info("New connection attempt failed - goto another host.");
                            }
                        }
                    }
                }

                _logger.Verbose(string.Format("Currently tried host: {0} have all of connections busy. Switching to the next host.", currentHost.Address));

                if (!hostsIter.MoveNext())
                {
                    var ex = new NoHostAvailableException(innerExceptions);
                    _logger.Error("Cannot connect to any host from pool.", ex);
                    throw ex;
                }
            }
        }


        internal void HostIsDown(IPAddress endpoint)
        {
			var metadata = _cluster.Metadata;
            if(metadata!=null)
                metadata.SetDownHost(endpoint, this);
        }

        void FreeConnection(CassandraConnection connection)
        {
            connection.Dispose();
            AtomicValue<int> val;
            _allocatedConnections.TryGetValue(connection.GetHostAdress(), out val);
            var no = Interlocked.Decrement(ref val.RawValue);
        }

        CassandraConnection AllocateConnection(IPAddress endPoint, HostDistance hostDistance, out Exception outExc)
        {
            CassandraConnection nconn = null;
            outExc = null;

            try
            {
                int no = 1;
                if (!_allocatedConnections.TryAdd(endPoint, new AtomicValue<int>(1)))
                {
                    AtomicValue<int> val;
                    _allocatedConnections.TryGetValue(endPoint, out val);
                    no = Interlocked.Increment(ref val.RawValue);
                    if (no > _poolingOptions.GetMaxConnectionPerHost(hostDistance))
                    {
                        Interlocked.Decrement(ref val.RawValue);
                        outExc = new ToManyConnectionsPerHost();
                        return null;
                    }
                }

            RETRY:
                nconn = new CassandraConnection(this, endPoint, _protocolOptions, _socketOptions, _clientOptions, _authProvider, _authInfoProvider,_binaryProtocolVersion);

                var streamId = nconn.AllocateStreamId();

                try
                {
                    var options = ProcessExecuteOptions(nconn.ExecuteOptions(streamId));
                }
                catch (CassandraConnectionBadProtocolVersionException)
                {
                    if (_binaryProtocolVersion == 1)
                        throw;
                    else
                    {
                        _binaryProtocolVersion = 1;
                        goto RETRY;
                    }
                }

                if (!string.IsNullOrEmpty(_keyspace))
                    nconn.SetKeyspace(_keyspace);
            }
            catch (Exception ex)
            {
                if (nconn != null)
                {
                    nconn.Dispose();
                    nconn = null;
                }

                AtomicValue<int> val;
                _allocatedConnections.TryGetValue(endPoint, out val);
                Interlocked.Decrement(ref val.RawValue); 

                if (CassandraConnection.IsStreamRelatedException(ex))
                {
                    HostIsDown(endPoint);
                    outExc = ex;
                    return null;
                }
                else
                    throw ex;
            }

            _logger.Info("Allocated new connection");            
            
            return nconn;
        }
        
        public void CreateKeyspace(string keyspace_name, Dictionary<string, string> replication = null, bool durable_writes = true)
        {
            WaitForSchemaAgreement(
                Query(CqlQueryTools.GetCreateKeyspaceCql(keyspace_name, replication, durable_writes, false), QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()));
            _logger.Info("Keyspace [" + keyspace_name + "] has been successfully CREATED.");
        }

        public void CreateKeyspaceIfNotExists(string keyspace_name, Dictionary<string, string> replication = null, bool durable_writes = true)
        {
            if (_binaryProtocolVersion > 1)
            {
                WaitForSchemaAgreement(
                    Query(CqlQueryTools.GetCreateKeyspaceCql(keyspace_name, replication, durable_writes, true), QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()));
                _logger.Info("Keyspace [" + keyspace_name + "] has been successfully CREATED.");
            }
            else
            {
                try
                {
                    CreateKeyspace(keyspace_name, replication, durable_writes);
                }
                catch (AlreadyExistsException)
                {
                    _logger.Info(string.Format("Cannot CREATE keyspace:  {0}  because it already exists.", keyspace_name));
                }
            }
        }

        public void DeleteKeyspace(string keyspace_name)
        {
            WaitForSchemaAgreement(
                Query(CqlQueryTools.GetDropKeyspaceCql(keyspace_name, false), QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()));
            _logger.Info("Keyspace [" + keyspace_name + "] has been successfully DELETED");
        }

        public void DeleteKeyspaceIfExists(string keyspace_name)
        {
            if (_binaryProtocolVersion > 1)
            {
                WaitForSchemaAgreement(
                    Query(CqlQueryTools.GetDropKeyspaceCql(keyspace_name, true), QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()));
                _logger.Info("Keyspace [" + keyspace_name + "] has been successfully DELETED");
            }
            else
            {
                try
                {
                    DeleteKeyspace(keyspace_name);
                }
                catch (InvalidQueryException)
                {
                    _logger.Info(string.Format("Cannot DELETE keyspace:  {0}  because it not exists.", keyspace_name));
                }
            }
        }

        public void ChangeKeyspace(string keyspace_name)
        {
            Execute(CqlQueryTools.GetUseKeyspaceCql(keyspace_name));
        }

        private void SetKeyspace(string keyspace_name)
        {
            foreach (var kv in _connectionPool)
            {
                foreach (var kvv in kv.Value)
                {
                    var conn = kvv.Value;
                    if (conn.IsHealthy)
                        conn.SetKeyspace(keyspace_name);
                }
            }
            foreach (var kv in _trashcan)
            {
                foreach (var ckv in kv.Value)
                {
                    if (ckv.Value.IsHealthy)
                        ckv.Value.SetKeyspace(keyspace_name);
                }
            }
            _keyspace = keyspace_name;
            _logger.Info("Changed keyspace to [" + _keyspace + "]");
        }

        BoolSwitch _alreadyDisposed = new BoolSwitch();

        void CheckDisposed()
        {
            if (_alreadyDisposed.IsTaken())
                throw new ObjectDisposedException("CassandraSession");
        }

        internal void InternalDispose()
        {
            if (!_alreadyDisposed.TryTake())
                return;

            _trashcanCleaner.Change(Timeout.Infinite, Timeout.Infinite);

            foreach (var kv in _connectionPool)
                foreach (var kvv in kv.Value)
                {
                    var conn = kvv.Value;
                    FreeConnection(conn);
                }
            foreach (var kv in _trashcan)
                foreach (var ckv in kv.Value)
                    FreeConnection(ckv.Value);
        }

        public void Dispose()
        {
            InternalDispose();
            Cluster.SessionDisposed(this);
        }

        ~Session()
        {
            Dispose();
        }

        #region Execute

        private ConcurrentDictionary<long, IAsyncResult> _startedActons = new ConcurrentDictionary<long, IAsyncResult>();

        public IAsyncResult BeginExecute(IStatement statement, object tag, AsyncCallback callback, object state)
        {
            if (statement == null)
            {
                throw new ArgumentNullException("statement");
            }
            var options = Cluster.Configuration.QueryOptions;
            var consistency = statement.ConsistencyLevel ?? options.GetConsistencyLevel();
            if (statement is RegularStatement)
            {
                var s = ((RegularStatement)statement);
                return BeginQuery(s.QueryString, callback, state,
                                      QueryProtocolOptions.CreateFromQuery(s, options.GetConsistencyLevel()),
                                      s.ConsistencyLevel, s.IsTracing, s, s, tag);
            }
            if (statement is BoundStatement)
            {
                var s = ((BoundStatement)statement);
                return BeginExecuteQuery(s.PreparedStatement.Id, s.PreparedStatement.Metadata,
                                                 QueryProtocolOptions.CreateFromQuery(s, Cluster.Configuration.QueryOptions.GetConsistencyLevel()),
                                                 callback, state, s.ConsistencyLevel, s, s, tag, s.IsTracing);
            }
            if (statement is BatchStatement)
            {
                var s = ((BatchStatement)statement);
                return BeginBatch(s.BatchType, s.Queries, callback, state, s.ConsistencyLevel, s.IsTracing, s, s, tag);
            }
            throw new NotSupportedException("Statement of type " + statement.GetType().FullName + " not supported");
        }

        public IAsyncResult BeginExecute(IStatement statement, AsyncCallback callback, object state)
        {
            return BeginExecute(statement, null, callback, state);
        }

        public IAsyncResult BeginExecute(string cqlQuery, ConsistencyLevel consistency, object tag, AsyncCallback callback, object state)
        {
            return BeginExecute(new SimpleStatement(cqlQuery).SetConsistencyLevel(consistency), tag, callback, state);
        }

        public IAsyncResult BeginExecute(string cqlQuery, ConsistencyLevel consistency, AsyncCallback callback, object state)
        {
            return BeginExecute(cqlQuery, consistency, null, callback, state);
        }

        public static object GetTag(IAsyncResult ar)
        {
            var longActionAc = ar as AsyncResult<RowSet>;
            return longActionAc.Tag;
        }

        public RowSet EndExecute(IAsyncResult ar)
        {
            var longActionAc = ar as AsyncResult<RowSet>;
            IAsyncResult oar;
            _startedActons.TryRemove(longActionAc.Id, out oar);
            if (longActionAc.AsyncSender is RegularStatement)
            {
                return EndQuery(ar);
            }
            if (longActionAc.AsyncSender is BoundStatement)
            {
                return EndExecuteQuery(ar);
            }
            if (longActionAc.AsyncSender is BatchStatement)
            {
                return EndBatch(ar);
            }
            throw new NotSupportedException("Async result sender not supported " + longActionAc.AsyncSender);
        }

        internal void WaitForAllPendingActions(int timeoutMs)
        {
            while (_startedActons.Count > 0)
            {
                var it = _startedActons.GetEnumerator();
                it.MoveNext();
                var ar = it.Current.Value as AsyncResultNoResult;
                ar.AsyncWaitHandle.WaitOne(timeoutMs);
                IAsyncResult oar;
                _startedActons.TryRemove(ar.Id, out oar);
            }
        }

        public RowSet Execute(IStatement query)
        {
            return EndExecute(BeginExecute(query, null, null));
        }

        public RowSet Execute(string cqlQuery, ConsistencyLevel consistency)
        {
            return Execute(new SimpleStatement(cqlQuery).SetConsistencyLevel(consistency).SetPageSize(_cluster.Configuration.QueryOptions.GetPageSize()));
        }

        public RowSet Execute(string cqlQuery, int pageSize)
        {
            return Execute(new SimpleStatement(cqlQuery).SetConsistencyLevel(_cluster.Configuration.QueryOptions.GetConsistencyLevel()).SetPageSize(pageSize));
        }

        public RowSet Execute(string cqlQuery)
        {
            return Execute(new SimpleStatement(cqlQuery).SetConsistencyLevel(_cluster.Configuration.QueryOptions.GetConsistencyLevel()).SetPageSize(_cluster.Configuration.QueryOptions.GetPageSize()));
        }

        public Task<RowSet> ExecuteAsync(IStatement query)
        {
            return Task.Factory.FromAsync<IStatement, RowSet>(BeginExecute, EndExecute, query, null);
        }
        #endregion

        #region Prepare

        public IAsyncResult BeginPrepare(string cqlQuery, AsyncCallback callback, object state)
        {
            var ar = BeginPrepareQuery(cqlQuery, callback, state) as AsyncResultNoResult;
            _startedActons.TryAdd(ar.Id, ar);
            return ar;
        }

        public PreparedStatement EndPrepare(IAsyncResult ar)
        {
            IAsyncResult oar;
            _startedActons.TryRemove((ar as AsyncResultNoResult).Id, out oar);
            RowSetMetadata metadata;
            var id = EndPrepareQuery(ar, out metadata);
            return new PreparedStatement(metadata, id.Item1, id.Item2, id.Item3);
        }

        public PreparedStatement Prepare(string cqlQuery)
        {
            return EndPrepare(BeginPrepare(cqlQuery, null, null));
        }
        
        #endregion

        static RetryDecision GetRetryDecision(Statement query, QueryValidationException exc, IRetryPolicy policy, int queryRetries)
        {
            if (exc is OverloadedException) return RetryDecision.Retry(null);
            else if (exc is IsBootstrappingException) return RetryDecision.Retry(null);
            else if (exc is TruncateException) return RetryDecision.Retry(null);

            else if (exc is ReadTimeoutException)
            {
                var e = exc as ReadTimeoutException;
                return policy.OnReadTimeout(query, e.ConsistencyLevel, e.RequiredAcknowledgements, e.ReceivedAcknowledgements, e.WasDataRetrieved, queryRetries);
            }
            else if (exc is WriteTimeoutException)
            {
                var e = exc as WriteTimeoutException;
                return policy.OnWriteTimeout(query, e.ConsistencyLevel, e.WriteType, e.RequiredAcknowledgements, e.ReceivedAcknowledgements, queryRetries);
            }
            else if (exc is UnavailableException)
            {
                var e = exc as UnavailableException;
                return policy.OnUnavailable(query, e.Consistency, e.RequiredReplicas, e.AliveReplicas, queryRetries);
            }

            else if (exc is AlreadyExistsException) return RetryDecision.Rethrow();
            else if (exc is InvalidConfigurationInQueryException) return RetryDecision.Rethrow();
            else if (exc is PreparedQueryNotFoundException) return RetryDecision.Rethrow();
            else if (exc is ProtocolErrorException) return RetryDecision.Rethrow();
            else if (exc is InvalidQueryException) return RetryDecision.Rethrow();
            else if (exc is UnauthorizedException) return RetryDecision.Rethrow();
            else if (exc is SyntaxError) return RetryDecision.Rethrow();

            else if (exc is ServerErrorException) return null;
            else return null;
        }

        private IDictionary<string, string[]> ProcessExecuteOptions(IOutput outp)
        {
            using (outp)
            {
                if (outp is OutputError)
                {
                    var ex = (outp as OutputError).CreateException();
                    _logger.Error(ex);
                    throw ex;
                }
                else if (outp is OutputOptions)
                {
                    return (outp as OutputOptions).Options;
                }
                else
                {
                    var ex = new DriverInternalError("Unexpected output kind");
                    _logger.Error("Prepared Query has returned an unexpected output kind.", ex);
                    throw ex;
                }
            }
        }

        private void ProcessPrepareQuery(IOutput outp, out RowSetMetadata metadata, out byte[] queryId, out RowSetMetadata resultMetadata)
        {
            using (outp)
            {
                if (outp is OutputError)
                {
                    var ex = (outp as OutputError).CreateException();
                    _logger.Error(ex);
                    throw ex; 
                }
                else if (outp is OutputPrepared)
                {
                    queryId = (outp as OutputPrepared).QueryId;
                    metadata = (outp as OutputPrepared).Metadata;
                    resultMetadata = (outp as OutputPrepared).ResultMetadata;
                    _logger.Info("Prepared Query has been successfully processed.");
                    return; //ok
                }
                else
                {
                    var ex = new DriverInternalError("Unexpected output kind");
                    _logger.Error("Prepared Query has returned an unexpected output kind.", ex);
                    throw ex; 
                }
            }
        }

        private RowSet ProcessRowset(IOutput outp, RowSetMetadata resultMetadata = null)
        {
            bool ok = false;
            try
            {
                if (outp is OutputError)
                {
                    var ex = (outp as OutputError).CreateException();
                    _logger.Error(ex);
                    throw ex;
                }
                else if (outp is OutputVoid)
                    return new RowSet(outp as OutputVoid, this);
                else if (outp is OutputSchemaChange)
                    return new RowSet(outp as OutputSchemaChange, this);
                else if (outp is OutputSetKeyspace)
                {
                    SetKeyspace((outp as OutputSetKeyspace).Value);
                    return new RowSet(outp as OutputSetKeyspace, this);
                }
                else if (outp is OutputRows)
                {
                    ok = true;
                    return new RowSet(outp as OutputRows, this, true, resultMetadata);
                }
                else
                {
                    var ex = new DriverInternalError("Unexpected output kind");
                    _logger.Error(ex);
                    throw ex; 
                }
            }
            finally
            {
                if (!ok)
                    outp.Dispose();
            }
        }

        abstract class LongToken
        {
            private readonly Logger _logger = new Logger(typeof(LongToken));
            public CassandraConnection Connection;
            public ConsistencyLevel? Consistency=null;
            public Statement Query;
            private IEnumerator<Host> _hostsIter = null;
            public IAsyncResult LongActionAc;
            public readonly Dictionary<IPAddress, List<Exception>> InnerExceptions = new Dictionary<IPAddress, List<Exception>>();
            public readonly List<IPAddress> TriedHosts = new List<IPAddress>();
            public int QueryRetries = 0;
            virtual public void Connect(Session owner, bool moveNext, out int streamId)
            {
                if (_hostsIter == null)
                {
                    _hostsIter = owner.Policies.LoadBalancingPolicy.NewQueryPlan(Query).GetEnumerator();
                    if (!_hostsIter.MoveNext())
                    {
                        var ex = new NoHostAvailableException(new Dictionary<IPAddress, List<Exception>>());
                        _logger.Error(ex);
                        throw ex;
                    }
                }
                else
                {
                    if (moveNext)
                        if (!_hostsIter.MoveNext())
                        {
                            var ex = new NoHostAvailableException(InnerExceptions);
                            _logger.Error(ex);
                            throw ex;
                        }
                }

                Connection = owner.Connect(_hostsIter, TriedHosts, InnerExceptions, out streamId);
            }
            abstract public void Begin(Session owner, int steamId);
            abstract public void Process(Session owner, IAsyncResult ar, out object value);
            abstract public void Complete(Session owner, object value, Exception exc = null);
        }

        void ExecConn(LongToken token, bool moveNext)
        {
            while (true)
            {
                try
                {
                    int streamId;
                    token.Connect(this, moveNext, out streamId);
                    token.Begin(this,streamId);
                    return;
                }
                catch (Exception ex)
                {
                    if (!CassandraConnection.IsStreamRelatedException(ex))
                    {
                        token.Complete(this, null, ex);
                        return;
                    }
                    else if (_alreadyDisposed.IsTaken())
                        return;
                    //else
                        //retry
                }
            }
        }

        void ClbNoQuery(IAsyncResult ar)
        {
            var token = ar.AsyncState as LongToken;
            try
            {
                try
                {
                    object value;
                    token.Process(this, ar, out value);
                    token.Complete(this, value);
                }
                catch (QueryValidationException exc)
                {
                    var decision = GetRetryDecision(token.Query, exc, token.Query != null ? (token.Query.RetryPolicy ?? Policies.RetryPolicy) : Policies.RetryPolicy, token.QueryRetries);
                    if (decision == null)
                    {
                        if (!token.InnerExceptions.ContainsKey(token.Connection.GetHostAdress()))
                            token.InnerExceptions.Add(token.Connection.GetHostAdress(), new List<Exception>());

                        token.InnerExceptions[token.Connection.GetHostAdress()].Add(exc);
                        ExecConn(token, true);
                    }
                    else
                    {
                        switch (decision.DecisionType)
                        {
                            case RetryDecision.RetryDecisionType.Rethrow:
                                token.Complete(this, null, exc);
                                return;
                            case RetryDecision.RetryDecisionType.Retry:
                                if (token.LongActionAc.IsCompleted)
                                    return;
                                token.Consistency = (decision.RetryConsistencyLevel.HasValue && (decision.RetryConsistencyLevel.Value<ConsistencyLevel.Serial)) ? decision.RetryConsistencyLevel.Value : token.Consistency;
                                token.QueryRetries++;

                                if (!token.InnerExceptions.ContainsKey(token.Connection.GetHostAdress()))
                                    token.InnerExceptions.Add(token.Connection.GetHostAdress(), new List<Exception>());

                                token.InnerExceptions[token.Connection.GetHostAdress()].Add(exc);
                                ExecConn(token, exc is UnavailableException);
                                return;
                            default:
                                token.Complete(this, null);
                                return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (CassandraConnection.IsStreamRelatedException(ex))
                {
                    if (!token.InnerExceptions.ContainsKey(token.Connection.GetHostAdress()))
                        token.InnerExceptions.Add(token.Connection.GetHostAdress(), new List<Exception>());
                    token.InnerExceptions[token.Connection.GetHostAdress()].Add(ex);
                    ExecConn(token, true);
                }
                else
                    token.Complete(this, null, ex);
            }
        }

        #region Query

        class LongQueryToken : LongToken
        {
            public string CqlQuery;
            public bool IsTracing;
            public Stopwatch StartedAt;
            public QueryProtocolOptions QueryPrtclOptions;
            override public void Connect(Session owner, bool moveNext, out int streamId)
            {
                StartedAt = Stopwatch.StartNew();
                base.Connect(owner, moveNext, out streamId);
            }

            override public void Begin(Session owner, int streamId)
            {
                Connection.BeginQuery(streamId, CqlQuery, owner.ClbNoQuery, this, owner,  IsTracing, QueryProtocolOptions.CreateFromQuery(Query, owner.Cluster.Configuration.QueryOptions.GetConsistencyLevel()),Consistency);
            }
            override public void Process(Session owner, IAsyncResult ar, out object value)
            {
                value = owner.ProcessRowset(Connection.EndQuery(ar, owner));
            }
            override public void Complete(Session owner, object value, Exception exc = null)
            {
                try
                {
                    var ar = LongActionAc as AsyncResult<RowSet>;
                    if (exc != null)
                        ar.Complete(exc);
                    else
                    {
                        RowSet rowset = value as RowSet;
                        if (rowset == null)
                            rowset = new RowSet(null, owner, false);
                        rowset.Info.SetTriedHosts(TriedHosts);
                        rowset.Info.SetAchievedConsistency(Consistency ?? QueryPrtclOptions.Consistency);
                        ar.SetResult(rowset);
                        ar.Complete();
                    }
                }
                finally
                {
                    var ts = StartedAt.ElapsedTicks;
                    CassandraCounters.IncrementCqlQueryCount();
                    CassandraCounters.IncrementCqlQueryBeats((ts * 1000000000));
                    CassandraCounters.UpdateQueryTimeRollingAvrg((ts * 1000000000) / Stopwatch.Frequency);
                    CassandraCounters.IncrementCqlQueryBeatsBase();
                }
            }
        }
        
        internal IAsyncResult BeginQuery(string cqlQuery, AsyncCallback callback, object state, QueryProtocolOptions queryProtocolOptions, ConsistencyLevel? consistency, bool isTracing = false, Statement query = null, object sender = null, object tag = null)
        {
            var longActionAc = new AsyncResult<RowSet>(-1, callback, state, this, "SessionQuery", sender, tag);
            var token = new LongQueryToken() { Consistency = consistency ?? queryProtocolOptions.Consistency, CqlQuery = cqlQuery, Query = query, QueryPrtclOptions = queryProtocolOptions, LongActionAc = longActionAc, IsTracing = isTracing };

            ExecConn(token, false);

            return longActionAc;
        }

        internal RowSet EndQuery(IAsyncResult ar)
        {
            return AsyncResult<RowSet>.End(ar, this, "SessionQuery");
        }

        internal RowSet Query(string cqlQuery, QueryProtocolOptions queryProtocolOptions, ConsistencyLevel consistency, bool isTracing = false, Statement query = null)
        {
            return EndQuery(BeginQuery(cqlQuery, null, null, queryProtocolOptions, consistency,isTracing, query));
        }

        #endregion

        #region Prepare

        readonly ConcurrentDictionary<byte[], string> _preparedQueries = new ConcurrentDictionary<byte[], string>();


        class LongPrepareQueryToken : LongToken
        {
            public string CqlQuery;            
            override public void Begin(Session owner, int streamId)
            {
                Connection.BeginPrepareQuery(streamId, CqlQuery, owner.ClbNoQuery, this, owner);
            }
            override public void Process(Session owner, IAsyncResult ar, out object value)
            {
                byte[] id;
                RowSetMetadata metadata;
                RowSetMetadata resultMetadata;
                owner.ProcessPrepareQuery(Connection.EndPrepareQuery(ar, owner), out metadata, out id, out resultMetadata);
                value = new KeyValuePair<RowSetMetadata, Tuple<byte[], string, RowSetMetadata>>(metadata, Tuple.Create(id, CqlQuery, resultMetadata ));
            }
            override public void Complete(Session owner, object value, Exception exc = null)
            {
                var ar = LongActionAc as AsyncResult<KeyValuePair<RowSetMetadata, Tuple<byte[], string, RowSetMetadata>>>;
                if (exc != null)
                    ar.Complete(exc);
                else
                {
                    var kv = (KeyValuePair<RowSetMetadata, Tuple<byte[], string, RowSetMetadata>>)value;
                    ar.SetResult(kv);
                    owner._preparedQueries.AddOrUpdate(kv.Value.Item1, kv.Value.Item2, (k, o) => o);
                    ar.Complete();
                }
            }
        }

        internal IAsyncResult BeginPrepareQuery(string cqlQuery, AsyncCallback callback, object state, object sender = null, object tag = null)
        {
            var longActionAc = new AsyncResult<KeyValuePair<RowSetMetadata, Tuple<byte[],string, RowSetMetadata>>>(-1, callback, state, this, "SessionPrepareQuery", sender, tag);
            var token = new LongPrepareQueryToken() { Consistency = _cluster.Configuration.QueryOptions.GetConsistencyLevel(), CqlQuery = cqlQuery, LongActionAc = longActionAc };

            ExecConn(token, false);

            return longActionAc;
        }

        internal Tuple<byte[], string, RowSetMetadata> EndPrepareQuery(IAsyncResult ar, out RowSetMetadata metadata)
        {
            var longActionAc = ar as AsyncResult<KeyValuePair<RowSetMetadata, byte[]>>;
            var ret = AsyncResult<KeyValuePair<RowSetMetadata, Tuple<byte[], string, RowSetMetadata>>>.End(ar, this, "SessionPrepareQuery");
            metadata = ret.Key;
            return ret.Value;
        }

        internal Tuple<byte[], string, RowSetMetadata> PrepareQuery(string cqlQuery, out RowSetMetadata metadata)
        {
            var ar = BeginPrepareQuery(cqlQuery, null, null, null);
            return EndPrepareQuery(ar, out metadata);
        }


        #endregion

        #region ExecuteQuery

        class LongExecuteQueryToken : LongToken
        {
            public byte[] Id;
            public string Cql;
            public RowSetMetadata Metadata;
            public RowSetMetadata ResultMetadata;
            public QueryProtocolOptions QueryProtocolOptions;
            public bool IsTracinig;
            public Stopwatch StartedAt;

            override public void Connect(Session owner, bool moveNext, out int streamId)
            {
                StartedAt = Stopwatch.StartNew();
                base.Connect(owner, moveNext, out streamId);
            }
            
            override public void Begin(Session owner,int streamId)
            {
                Connection.BeginExecuteQuery(streamId, Id, Cql, Metadata, owner.ClbNoQuery, this, owner, IsTracinig, QueryProtocolOptions.CreateFromQuery(Query, owner.Cluster.Configuration.QueryOptions.GetConsistencyLevel()), Consistency);
            }
            override public void Process(Session owner, IAsyncResult ar, out object value)
            {
                value = owner.ProcessRowset(Connection.EndExecuteQuery(ar, owner), ResultMetadata);
            }
            override public void Complete(Session owner, object value, Exception exc = null)
            {
                try
                {
                    var ar = LongActionAc as AsyncResult<RowSet>;
                    if (exc != null)
                        ar.Complete(exc);
                    else
                    {
                        RowSet rowset = value as RowSet;
                        if (rowset == null)
                            rowset = new RowSet(null, owner, false);
                        rowset.Info.SetTriedHosts(TriedHosts);
                        rowset.Info.SetAchievedConsistency(Consistency ?? QueryProtocolOptions.Consistency);
                        ar.SetResult(rowset);
                        ar.Complete();
                    }
                }
                finally
                {
                    var ts = StartedAt.ElapsedTicks;
                    CassandraCounters.IncrementCqlQueryCount();
                    CassandraCounters.IncrementCqlQueryBeats((ts * 1000000000));
                    CassandraCounters.UpdateQueryTimeRollingAvrg((ts * 1000000000) / Stopwatch.Frequency);
                    CassandraCounters.IncrementCqlQueryBeatsBase();
                }
            }
        }

        internal IAsyncResult BeginExecuteQuery(byte[] id, RowSetMetadata metadata, QueryProtocolOptions queryProtocolOptions, AsyncCallback callback, object state, ConsistencyLevel? consistency, Statement query = null, object sender = null, object tag = null, bool isTracing = false)
        {
            var longActionAc = new AsyncResult<RowSet>(-1, callback, state, this, "SessionExecuteQuery", sender, tag);
            var token = new LongExecuteQueryToken() { Consistency = consistency ?? queryProtocolOptions.Consistency, Id = id, Cql = _preparedQueries[id], Metadata = metadata, QueryProtocolOptions = queryProtocolOptions, Query = query, LongActionAc = longActionAc, IsTracinig = isTracing, ResultMetadata = (query as BoundStatement).PreparedStatement.ResultMetadata };

            ExecConn(token, false);

            return longActionAc;
        }

        internal RowSet EndExecuteQuery(IAsyncResult ar)
        {
            var longActionAc = ar as AsyncResult<RowSet>;
            return AsyncResult<RowSet>.End(ar, this, "SessionExecuteQuery");
        }

        internal RowSet ExecuteQuery(byte[] id, RowSetMetadata metadata, QueryProtocolOptions queryProtocolOptions, ConsistencyLevel consistency, Statement query = null, bool isTracing=false)
        {
            var ar = BeginExecuteQuery(id,metadata, queryProtocolOptions, null, null, consistency, query, isTracing);
            return EndExecuteQuery(ar);
        }

        #endregion

        #region Batch

        class LongBatchToken : LongToken
        {
            public BatchType BatchType;
            public List<Statement> Queries;
            public bool IsTracing;
            public Stopwatch StartedAt;

            override public void Connect(Session owner, bool moveNext, out int streamId)
            {
                StartedAt = Stopwatch.StartNew();
                base.Connect(owner, moveNext, out streamId);
            }

            override public void Begin(Session owner, int streamId)
            {
                Connection.BeginBatch(streamId, BatchType, Queries, owner.ClbNoQuery, this, owner, Consistency??owner._cluster.Configuration.QueryOptions.GetConsistencyLevel(), IsTracing);
            }
            override public void Process(Session owner, IAsyncResult ar, out object value)
            {
                value = owner.ProcessRowset(Connection.EndBatch(ar, owner));
            }
            override public void Complete(Session owner, object value, Exception exc = null)
            {
                try
                {
                    var ar = LongActionAc as AsyncResult<RowSet>;
                    if (exc != null)
                        ar.Complete(exc);
                    else
                    {
                        RowSet rowset = value as RowSet;
                        if (rowset == null)
                            rowset = new RowSet(null, owner, false);
                        rowset.Info.SetTriedHosts(TriedHosts);
                        rowset.Info.SetAchievedConsistency(Consistency ?? owner._cluster.Configuration.QueryOptions.GetConsistencyLevel());
                        ar.SetResult(rowset);
                        ar.Complete();
                    }
                }
                finally
                {
                    var ts = StartedAt.ElapsedTicks;
                    CassandraCounters.IncrementCqlQueryCount();
                    CassandraCounters.IncrementCqlQueryBeats((ts * 1000000000));
                    CassandraCounters.UpdateQueryTimeRollingAvrg((ts * 1000000000) / Stopwatch.Frequency);
                    CassandraCounters.IncrementCqlQueryBeatsBase();
                }
            }
        }

        internal IAsyncResult BeginBatch(BatchType batchType, List<Statement> queries, AsyncCallback callback, object state, ConsistencyLevel? consistency, bool isTracing = false, Statement query = null, object sender = null, object tag = null)
        {
            var longActionAc = new AsyncResult<RowSet>(-1, callback, state, this, "SessionBatch", sender, tag);
            var token = new LongBatchToken() { Consistency = consistency ?? _cluster.Configuration.QueryOptions.GetConsistencyLevel(), Queries = queries, BatchType = batchType, Query = query, LongActionAc = longActionAc, IsTracing = isTracing };

            ExecConn(token, false);

            return longActionAc;
        }

        internal RowSet EndBatch(IAsyncResult ar)
        {
            return AsyncResult<RowSet>.End(ar, this, "SessionBatch");
        }

        #endregion

        internal const long MaxSchemaAgreementWaitMs = 10000;
        internal const string SelectSchemaPeers = "SELECT peer, rpc_address, schema_version FROM system.peers";
        internal const string SelectSchemaLocal = "SELECT schema_version FROM system.local WHERE key='local'";
        internal static readonly IPAddress BindAllAddress = new IPAddress(new byte[4]);


        public void WaitForSchemaAgreement(RowSet rs)
        {
            WaitForSchemaAgreement(rs.Info.QueriedHost);
        }

        public bool WaitForSchemaAgreement(IPAddress forHost)
        {
            var start = DateTimeOffset.Now;
            long elapsed = 0;
            while (elapsed < MaxSchemaAgreementWaitMs)
            {
                var versions = new HashSet<Guid>();

                int streamId1;
                int streamId2;
                CassandraConnection connection;
                {
                    var localhost = _cluster.Metadata.GetHost(forHost);
                    var iterLiof = new List<Host>() { localhost }.GetEnumerator();
                    iterLiof.MoveNext();
                    List<IPAddress> tr = new List<IPAddress>();
                    Dictionary<IPAddress, List<Exception>> exx = new Dictionary<IPAddress, List<Exception>>();

                    connection = Connect(iterLiof, tr, exx, out streamId1);
                    while (true)
                    {
                        try
                        {
                            streamId2 = connection.AllocateStreamId();
                            break;
                        }
                        catch (CassandraConnection.StreamAllocationException)
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
                {

                    using (var outp = connection.Query(streamId1, SelectSchemaPeers, false, QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()))
                    {
                        if (outp is OutputRows)
                        {
                            var rowset = new RowSet((outp as OutputRows), null, false);
                            foreach (var row in rowset.GetRows())
                            {
                                if (row.IsNull("rpc_address") || row.IsNull("schema_version"))
                                    continue;

                                var rpc = row.GetValue<IPEndPoint>("rpc_address").Address;
                                if (rpc.Equals(BindAllAddress))
                                {
                                    if (!row.IsNull("peer"))
                                        rpc = row.GetValue<IPEndPoint>("peer").Address;
                                }

                                Host peer = _cluster.Metadata.GetHost(rpc);
                                if (peer != null && peer.IsConsiderablyUp)
                                    versions.Add(row.GetValue<Guid>("schema_version"));
                            }
                        }
                    }
                }

                {
                    using (var outp = connection.Query(streamId2, SelectSchemaLocal, false, QueryProtocolOptions.Default, _cluster.Configuration.QueryOptions.GetConsistencyLevel()))
                    {
                        if (outp is OutputRows)
                        {
                            var rowset = new RowSet((outp as OutputRows), null, false);
                            // Update cluster name, DC and rack for the one node we are connected to
                            foreach (var localRow in rowset.GetRows())
                                if (!localRow.IsNull("schema_version"))
                                {
                                    versions.Add(localRow.GetValue<Guid>("schema_version"));
                                    break;
                                }
                        }
                    }
                }


                if (versions.Count <= 1)
                    return true;

                // let's not flood the node too much
                Thread.Sleep(200);
                elapsed = (long)(DateTimeOffset.Now - start).TotalMilliseconds;
            }

            return false;
        }

#if ERRORINJECTION
        public void SimulateSingleConnectionDown()
        {
            if (_connectionPool.Count > 0)
            {
                var endpoints = new List<IPAddress>(_connectionPool.Keys);
                var hostidx = StaticRandom.Instance.Next(endpoints.Count);
                var endpoint = endpoints[hostidx];
                ConcurrentDictionary<Guid, CassandraConnection> pool;
                if (_connectionPool.TryGetValue(endpoint, out pool))
                {
                    var items = new List<Guid>(pool.Keys);
                    if (items.Count == 0)
                        return;
                    var conidx = StaticRandom.Instance.Next(items.Count);
                    var k = items[conidx];
                    CassandraConnection con;
                    if (pool.TryGetValue(k, out con))
                        con.KillSocket();
                }
            }
        }
#endif
    }
}
