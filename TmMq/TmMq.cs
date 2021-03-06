﻿using System;
using System.Collections.Concurrent;
using System.Configuration;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Builders;

namespace TmMq
{
    public abstract class TmMq : IDisposable
    {
        protected static readonly TmMqConfig g_config;
        private static MongoServer g_server = null;
        protected static readonly Lazy<MongoDatabase> g_db;
        private readonly ConcurrentDictionary<string, MongoCollection> m_collections = new ConcurrentDictionary<string, MongoCollection>();
        protected MongoCollection ErrorCollection { get; private set; }
        protected abstract MongoCollection MessagesCollection { get; }

        private readonly static object g_pubsubUpdateSync = new object();
        private static bool g_pubsubUpdating;
        private static readonly Timer g_pubsubUpdateTimer;
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, DateTime>> g_pubsubQueues = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, DateTime>>();
        private readonly MongoCollection m_configCollection;

        public MongoDatabase Db { get { return g_db.Value; } }

        static TmMq()
        {
            BsonSerializer.RegisterSerializer( typeof( DynamicDictionary ), new DynamicDictionarySerializer() );

            BsonClassMap.RegisterClassMap<DynamicDictionary>( cm =>
            {
                cm.AutoMap();
                cm.SetDiscriminator( "DynamicDictionary" );
            } );

            g_config = LoadConfig();

            g_db = new Lazy<MongoDatabase>( () =>
                {
                    var server = CreateMongoDbServer();
                    return server["tmmq"];
                },
                true );

            g_pubsubUpdateTimer = new Timer( MonitorPubSubConfig, null, g_config.FirstPubSubPollAfterMilliseconds, g_config.PubSubPollEveryMilliseconds );
        }

        private static TmMqConfig LoadConfig()
        {
            string typeName = ConfigurationManager.AppSettings["TmMq.ConfigType"] ?? "TmMq.TmMqConfig";
            Type type = Type.GetType( typeName, true );

            return (TmMqConfig)Activator.CreateInstance( type );
        }

        protected TmMq()
        {
            ErrorCollection = GetCollection( "error" );
            m_configCollection = GetCollection( "config" );

            m_configCollection.Update( Query.EQ( "Type", "pubsub" ), Update.Set( "Type", "pubsub" ), UpdateFlags.Upsert, SafeMode.True );

            //Find orphaned pub/sub collections
            lock( g_pubsubUpdateSync )
            {
                g_pubsubUpdating = true;
                try
                {
                    foreach( var name in Db.GetCollectionNames() )
                    {
                        if( name.StartsWith( "pubsub_" ) )
                        {
                            int idxSubscriber = name.LastIndexOf( '_' );

                            if( idxSubscriber < 7 )
                            {
                                continue;
                            }

                            string queueName = name.Substring( 7, idxSubscriber - 7 );
                            string subscriberName = name.Substring( idxSubscriber + 1 );

                            ConcurrentDictionary<Guid, DateTime> subscribers;

                            if( !g_pubsubQueues.TryGetValue( queueName, out subscribers ) )
                            {
                                subscribers = new ConcurrentDictionary<Guid, DateTime>();
                                g_pubsubQueues[queueName] = subscribers;
                            }

                            subscribers[Guid.Parse( subscriberName )] = DateTime.MinValue;
                        }
                    }
                }
                finally
                {
                    g_pubsubUpdating = false;
                }
            }
        }

        private static MongoServer CreateMongoDbServer()
        {
            string con = ConfigurationManager.AppSettings["MongoDB.Server"];
            return MongoServer.Create( con );
        }

        public ITmMqMessage CreateMessage()
        {
            return new TmMqMessage();
        }

        public void Acknowledge( ITmMqMessage msg )
        {
            MessagesCollection.Remove( Query.EQ( "MessageId", msg.MessageId ), SafeMode.False );
        }

        public void Fail( ITmMqMessage msg, Exception exception )
        {
            MessagesCollection.Update(
                         Query.EQ( "MessageId", msg.MessageId ),
                         Update.Inc( "RetryCount", 1 ) );

            MessagesCollection.Update(
                         Query.EQ( "MessageId", msg.MessageId ),
                         Update.PushWrapped( "Errors", new TmMqMessageError( exception.ToString() ) ) );
        }

        public MongoCollection GetCollection( string name )
        {
            #region param checks
            if( string.IsNullOrWhiteSpace( name ) )
            {
                throw new ArgumentException( "name must be specified", "name" );
            }
            #endregion

            MongoCollection col;

            if( !m_collections.TryGetValue( name, out col ) )
            {
                if( !Db.CollectionExists( name ) )
                {
                    col = Db[name];
                    col.EnsureIndex( new[] { "TimeStamp" } );
                    col.EnsureIndex( new[] { "DeliveredAt" } );
                    col.EnsureIndex( new[] { "HoldUntil" } );
                    col.EnsureIndex( new[] { "ExpireAt" } );
                    col.EnsureIndex( new[] { "MessageId" } );
                    m_collections[name] = col;
                }
                else
                {
                    col = Db[name];
                }
            }

            return col;
        }

        private static void MonitorPubSubConfig( object state )
        {
            if( g_pubsubUpdating )
            {
                return;
            }

            g_pubsubUpdating = true;

            try
            {
                if( g_server == null )
                {
                    string con = ConfigurationManager.AppSettings[ "MongoDB.Server" ];
                    g_server = MongoServer.Create( con );
                }

                var db = g_server["tmmq"];
                var configCollection = db["config"];

                var config = configCollection.FindOne( Query.EQ( "Type", "pubsub" ) );

                if( config == null )
                {
                    return;
                }

                lock( g_pubsubUpdateSync )
                {
                    foreach( var element in config )
                    {
                        string name = element.Name;

                        if( !name.StartsWith( "pubsub_" ) )
                        {
                            continue;
                        }

                        int idxSubscriber = name.LastIndexOf( '_' );

                        if( idxSubscriber < 7 )
                        {
                            continue;
                        }

                        string queueName = name.Substring( 7, idxSubscriber - 7 );
                        string subscriberName = name.Substring( idxSubscriber + 1 );

                        DateTime at = element.Value.AsDateTime;

                        if( (DateTime.UtcNow - at).TotalSeconds > g_config.NeedPubSubPingSeconds )
                        {
                            ConcurrentDictionary<Guid, DateTime> x;
                            g_pubsubQueues.TryRemove( name.Substring( 7 ), out x );

                            configCollection.Update( Query.EQ( "Type", "pubsub" ), Update.Unset( name ) );

                            if( db.CollectionExists( name ) )
                            {
                                db.DropCollection( name );
                            }
                        }
                        else
                        {
                            ConcurrentDictionary<Guid, DateTime> subscribers;

                            if( !g_pubsubQueues.TryGetValue( queueName, out subscribers ) )
                            {
                                subscribers = new ConcurrentDictionary<Guid, DateTime>();
                                g_pubsubQueues[queueName] = subscribers;
                            }

                            subscribers[Guid.Parse( subscriberName )] = at;
                        }
                    }

                    foreach( var queueName in g_pubsubQueues.Keys )
                    {
                        ConcurrentDictionary<Guid, DateTime> queue;

                        if( g_pubsubQueues.TryGetValue( queueName, out queue ) )
                        {
                            foreach( var last in queue )
                            {
                                if( (DateTime.UtcNow - last.Value).TotalSeconds > g_config.NeedPubSubPingSeconds )
                                {
                                    DateTime x;
                                    queue.TryRemove( last.Key, out x );

                                    if( db.CollectionExists( "pubsub_" + queueName + "_" + last.Key ) )
                                    {
                                        db.DropCollection( "pubsub_" + queueName + "_" + last.Key );
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch( Exception ex )
            {
                Console.WriteLine( ex ); //TODO
            }
            finally
            {
                g_pubsubUpdating = false;
            }
        }

        //TODO consider implementing tailable cursor for a trigger
        private static void MonitorPubSubConfig_TailableCursor()
        {
            string con = ConfigurationManager.AppSettings["MongoDB.Server"];
            var server = MongoServer.Create( con );
            var db = server["local"];
            var oplog = db["oplog.rs"];
            BsonTimestamp lastTs = BsonTimestamp.Create( 0 );

            while( true )
            {
                var query = Query.GT( "ts", lastTs );

                var cursor = oplog.Find( query )
                        .SetFlags( QueryFlags.TailableCursor | QueryFlags.AwaitData )
                        .SetSortOrder( "$natural" );

                using( var enumerator = (MongoCursorEnumerator<BsonDocument>)cursor.GetEnumerator() )
                {
                    while( true )
                    {
                        if( enumerator.MoveNext() )
                        {
                            var document = enumerator.Current;
                            lastTs = document["ts"].AsBsonTimestamp;

                            Console.WriteLine( document.GetValue( "op" ) );
                            //ProcessDocument(document);
                        }
                        else
                        {
                            if( enumerator.IsDead )
                            {
                                break;
                            }
                            if( !enumerator.IsServerAwaitCapable )
                            {
                                Thread.Sleep( 100 );
                            }
                        }
                    }
                }
            }
        }

        protected void KeepAlive( string pubsubQueueName, Guid subscriberName )
        {
            #region param checks
            if( pubsubQueueName == null )
            {
                throw new ArgumentNullException( "pubsubQueueName" );
            }
            #endregion

            try
            {
                lock( g_pubsubUpdateSync )
                {
                    ConcurrentDictionary<Guid, DateTime> subscribers;

                    if( !g_pubsubQueues.TryGetValue( pubsubQueueName, out subscribers ) )
                    {
                        subscribers = new ConcurrentDictionary<Guid, DateTime>();
                        g_pubsubQueues[ pubsubQueueName ] = subscribers;
                    }

                    subscribers[ subscriberName ] = DateTime.UtcNow;

                    m_configCollection.Update( Query.EQ( "Type", "pubsub" ), Update.Set( "pubsub_" + pubsubQueueName + "_" + subscriberName, DateTime.UtcNow ), UpdateFlags.Upsert );
                }
            }
            catch( Exception ex )
            {
                Console.WriteLine( ex ); //TODO
            }
        }

        protected void DistributestringMessage( string pubsubQueueName, ITmMqMessage message, bool safeSend )
        {
            #region param checks
            if( pubsubQueueName == null )
            {
                throw new ArgumentNullException( "pubsubQueueName" );
            }

            if( message == null )
            {
                throw new ArgumentNullException( "message" );
            }
            #endregion

            ConcurrentDictionary<Guid, DateTime> subscribers;

            if( g_pubsubQueues.TryGetValue( pubsubQueueName, out subscribers ) )
            {
                foreach( var subscriber in subscribers.Keys )
                {
                    var col = GetCollection( "pubsub_" + pubsubQueueName + "_" + subscriber );
                    col.Insert( message, safeSend ? SafeMode.True : SafeMode.False );
                }
            }
        }

        public virtual void Dispose()
        {
        }
    }
}