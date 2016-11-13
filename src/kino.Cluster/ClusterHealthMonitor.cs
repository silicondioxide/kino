﻿using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using C5;
using kino.Cluster.Configuration;
using kino.Connectivity;
using kino.Core;
using kino.Core.Diagnostics;
using kino.Core.Framework;
using kino.Messaging;
using kino.Messaging.Messages;
using kino.Security;

namespace kino.Cluster
{
    public class ClusterHealthMonitor : IClusterHealthMonitor
    {
        private readonly ISocketFactory socketFactory;
        private readonly ISecurityProvider securityProvider;
        private readonly ClusterHealthMonitorConfiguration config;
        private readonly ILocalSendingSocket<IMessage> routerLocalSocket;
        private readonly ILogger logger;
        private CancellationTokenSource cancellationTokenSource;
        private Task processingMessages;
        private readonly ILocalSocket<IMessage> multiplexingSocket;
        private readonly IDictionary<SocketIdentifier, ClusterMemberMeta> peers;
        private Task receivingMessages;
        private TimeSpan deadPeersCheckInterval;
        private IDisposable deadPeersCheckObserver;
        private readonly IDisposable stalePeersCheckObserver;

        public ClusterHealthMonitor(ISocketFactory socketFactory,
                                    ILocalSocketFactory localSocketFactory,
                                    ISecurityProvider securityProvider,
                                    ClusterHealthMonitorConfiguration config,
                                    ILocalSendingSocket<IMessage> routerLocalSocket,
                                    ILogger logger)
        {
            deadPeersCheckInterval = TimeSpan.FromDays(1);
            stalePeersCheckObserver = Observable.Interval(config.StalePeersCheckInterval).Subscribe(_ => CheckStalePeers());
            this.socketFactory = socketFactory;
            this.securityProvider = securityProvider;
            peers = new HashDictionary<SocketIdentifier, ClusterMemberMeta>();
            multiplexingSocket = localSocketFactory.Create<IMessage>();
            this.config = config;
            this.routerLocalSocket = routerLocalSocket;
            this.logger = logger;
        }

        public void StartPeerMonitoring(Node peer, Health health)
            => multiplexingSocket.Send(Message.Create(new StartPeerMonitoringMessage
                                                      {
                                                          SocketIdentity = peer.SocketIdentity,
                                                          Uri = peer.Uri.ToSocketAddress(),
                                                          Health = new Messaging.Messages.Health
                                                                   {
                                                                       Uri = health.Uri,
                                                                       HeartBeatInterval = health.HeartBeatInterval
                                                                   }
                                                      }));

        public void AddPeer(Node peer, Health health)
        {
            //logger.Debug($"AddPeer {peer.SocketIdentity.GetAnyString()}");
            multiplexingSocket.Send(Message.Create(new AddPeerMessage
                                                   {
                                                       SocketIdentity = peer.SocketIdentity,
                                                       Uri = peer.Uri.ToSocketAddress(),
                                                       Health = new Messaging.Messages.Health
                                                                {
                                                                    Uri = health.Uri,
                                                                    HeartBeatInterval = health.HeartBeatInterval
                                                                }
                                                   }));
        }

        public void DeletePeer(SocketIdentifier socketIdentifier)
            => multiplexingSocket.Send(Message.Create(new DeletePeerMessage {SocketIdentity = socketIdentifier.Identity}));

        public void Start()
        {
            cancellationTokenSource = new CancellationTokenSource();
            var participantsCount = 3;
            using (var barrier = new Barrier(participantsCount))
            {
                receivingMessages = Task.Factory.StartNew(_ => ReceiveMessages(cancellationTokenSource.Token, barrier), TaskCreationOptions.LongRunning, cancellationTokenSource.Token);
                processingMessages = Task.Factory.StartNew(_ => ProcessMessages(cancellationTokenSource.Token, barrier), TaskCreationOptions.LongRunning, cancellationTokenSource.Token);
                barrier.SignalAndWait(cancellationTokenSource.Token);
                barrier.SignalAndWait(cancellationTokenSource.Token);
            }
        }

        public void Stop()
        {
            stalePeersCheckObserver?.Dispose();
            deadPeersCheckObserver?.Dispose();
            cancellationTokenSource?.Cancel();
            processingMessages?.Wait();
            receivingMessages?.Wait();
            cancellationTokenSource?.Dispose();
        }

        private void ReceiveMessages(CancellationToken token, Barrier barrier)
        {
            try
            {
                var waitHandles = new[]
                                  {
                                      multiplexingSocket.CanReceive(),
                                      token.WaitHandle
                                  };
                using (var publisherSocket = CreatePublisherSocket())
                {
                    barrier.SignalAndWait(token);
                    barrier.SignalAndWait(token);
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (WaitHandle.WaitAny(waitHandles) == 0)
                            {
                                var message = multiplexingSocket.TryReceive();
                                if (message != null)
                                {
                                    publisherSocket.SendMessage(message);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception err)
                        {
                            logger.Error(err);
                        }
                    }
                }
            }
            catch (Exception err)
            {
                logger.Error(err);
            }

            logger.Warn($"{GetType().Name} message processing stopped.");
        }

        private void ProcessMessages(CancellationToken token, Barrier barrier)
        {
            try
            {
                barrier.SignalAndWait(token);
                using (var socket = CreateSubscriberSocket())
                {
                    barrier.SignalAndWait(token);
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var message = socket.ReceiveMessage(token);
                            if (message != null)
                            {
                                //logger.Debug($"{GetType().Name} received {message.Identity.GetAnyString()} message");
                                ProcessMessage(message, socket);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception err)
                        {
                            logger.Error(err);
                        }
                    }
                }
            }
            catch (Exception err)
            {
                logger.Error(err);
            }

            logger.Warn($"{GetType().Name} stopped.");
        }

        private void ProcessMessage(IMessage message, ISocket socket)
        {
            var _ = ProcessHeartBeatMessage(message)
                    || ProcessStartPeerMonitoringMessage(message, socket)
                    || ProcessAddPeerMessage(message, socket)
                    || ProcessCheckDeadPeersMessage(message)
                    || ProcessCheckStalePeersMessage(message)
                    || ProcessDeletePeerMessage(message, socket);
        }

        private bool ProcessDeletePeerMessage(IMessage message, ISocket socket)
        {
            var shouldHandle = IsDeletePeerMessage(message);
            if (shouldHandle)
            {
                var payload = message.GetPayload<DeletePeerMessage>();
                ClusterMemberMeta meta;
                var socketIdentifier = new SocketIdentifier(payload.SocketIdentity);
                if (peers.Find(ref socketIdentifier, out meta))
                {
                    peers.Remove(socketIdentifier);

                    logger.Debug($"Left {peers.Count} to monitor.");
                    if (meta.ConnectionEstablished)
                    {
                        socket.Disconnect(new Uri(meta.HealthUri));
                        logger.Warn($"Stopped HeartBeat monitoring peer {payload.SocketIdentity.GetAnyString()}@{meta.HealthUri}");
                    }
                }
                else
                {
                    logger.Warn($"Unable to disconnect from unknown peer: SocketIdentity [{payload.SocketIdentity.GetAnyString()}]");
                }
            }

            return shouldHandle;
        }

        private bool ProcessCheckStalePeersMessage(IMessage message)
        {
            var shouldHandle = IsCheckStalePeersMessage(message);
            if (shouldHandle)
            {
                var now = DateTime.UtcNow;
                var staleNodes = peers.Where(p => PeerIsStale(now, p))
                                      .ToList();
                if (staleNodes.Any())
                {
                    logger.Debug($"Stale nodes detected: {staleNodes.Count()}. Connectivity check scheduled.");
                    Task.Factory.StartNew(() => CheckConnectivity(cancellationTokenSource.Token, staleNodes), TaskCreationOptions.LongRunning);
                }
            }

            return shouldHandle;
        }

        private void CheckConnectivity(CancellationToken token, System.Collections.Generic.IReadOnlyList<KeyValuePair<SocketIdentifier, ClusterMemberMeta>> staleNodes)
        {
            try
            {
                for (var i = 0; i < staleNodes.Count && !token.IsCancellationRequested; i++)
                {
                    var staleNode = staleNodes[i];
                    if (!staleNode.Value.ConnectionEstablished)
                    {
                        using (var socket = socketFactory.CreateRouterSocket())
                        {
                            var uri = new Uri(staleNode.Value.ScaleOutUri);
                            try
                            {
                                socket.SetMandatoryRouting();
                                socket.Connect(uri, true);
                                var message = Message.Create(new PingMessage(), securityProvider.GetDomain(KinoMessages.Ping.Identity)).As<Message>();
                                message.SetSocketIdentity(staleNode.Key.Identity);
                                message.SignMessage(securityProvider);
                                socket.SendMessage(message);
                                socket.Disconnect(uri);
                                staleNode.Value.LastKnownHeartBeat = DateTime.UtcNow;
                            }
                            catch (Exception err)
                            {
                                routerLocalSocket.Send(Message.Create(new UnregisterUnreachableNodeMessage {SocketIdentity = staleNode.Key.Identity}));
                                logger.Error($"Failed trying to check connectivity to peer {staleNode.Key}@{uri.ToSocketAddress()}. Peer deletion scheduled. {err}");
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
                logger.Error(err);
            }
        }

        private bool ProcessCheckDeadPeersMessage(IMessage message)
        {
            var shouldHandle = IsCheckDeadPeersMessage(message);
            if (shouldHandle)
            {
                var now = DateTime.UtcNow;
                var deadNodes = peers.Where(p => HeartBeatExpired(now, p))
                                     .ToList();
                foreach (var deadNode in deadNodes)
                {
                    routerLocalSocket.Send(Message.Create(new UnregisterUnreachableNodeMessage {SocketIdentity = deadNode.Key.Identity}));
                    logger.Debug($"Unreachable node {deadNode.Key}@{deadNode.Value.ScaleOutUri} detected. Route deletion scheduled.");
                }
            }

            return shouldHandle;
        }

        private bool PeerIsStale(DateTime now, KeyValuePair<SocketIdentifier, ClusterMemberMeta> p)
            => !p.Value.ConnectionEstablished
               && now - p.Value.LastKnownHeartBeat > config.PeerIsStaleAfter;

        private bool HeartBeatExpired(DateTime now, KeyValuePair<SocketIdentifier, ClusterMemberMeta> p)
            => p.Value.ConnectionEstablished
               && now - p.Value.LastKnownHeartBeat > p.Value.HeartBeatInterval.MultiplyBy(config.MissingHeartBeatsBeforeDeletion);

        private bool ProcessAddPeerMessage(IMessage message, ISocket _)
        {
            var shouldHandle = IsAddPeerMessage(message);
            if (shouldHandle)
            {
                var payload = message.GetPayload<AddPeerMessage>();

                logger.Debug($"New peer {payload.SocketIdentity.GetAnyString()} added.");

                var meta = new ClusterMemberMeta
                           {
                               HealthUri = payload.Health.Uri,
                               HeartBeatInterval = payload.Health.HeartBeatInterval,
                               ScaleOutUri = payload.Uri,
                               LastKnownHeartBeat = DateTime.UtcNow
                           };
                peers.FindOrAdd(new SocketIdentifier(payload.SocketIdentity), ref meta);
            }

            return shouldHandle;
        }

        private bool ProcessStartPeerMonitoringMessage(IMessage message, ISocket socket)
        {
            var shouldHandle = IsStartPeerMonitoringMessage(message);
            if (shouldHandle)
            {
                var payload = message.GetPayload<StartPeerMonitoringMessage>();

                logger.Debug($"Received {typeof(StartPeerMonitoringMessage).Name} for node {payload.SocketIdentity.GetAnyString()}");

                var meta = new ClusterMemberMeta
                           {
                               HealthUri = payload.Health.Uri,
                               HeartBeatInterval = payload.Health.HeartBeatInterval,
                               ScaleOutUri = payload.Uri,
                               LastKnownHeartBeat = DateTime.UtcNow
                           };
                peers.FindOrAdd(new SocketIdentifier(payload.SocketIdentity), ref meta);
                meta.ConnectionEstablished = true;
                StartDeadPeersCheck(meta.HeartBeatInterval);
                socket.Connect(new Uri(meta.HealthUri));

                logger.Debug($"Connected to peer {payload.SocketIdentity.GetAnyString()}@{meta.HealthUri} for HeartBeat monitoring.");
            }

            return shouldHandle;
        }

        private void StartDeadPeersCheck(TimeSpan newHeartBeatInterval)
        {
            if (newHeartBeatInterval < deadPeersCheckInterval)
            {
                deadPeersCheckInterval = newHeartBeatInterval;
                deadPeersCheckObserver?.Dispose();
                deadPeersCheckObserver = Observable.Interval(deadPeersCheckInterval).Subscribe(_ => CheckDeadPeers());
            }
        }

        private void CheckDeadPeers()
            => multiplexingSocket.Send(Message.Create(new CheckDeadPeersMessage()));

        private void CheckStalePeers()
            => multiplexingSocket.Send(Message.Create(new CheckStalePeersMessage()));

        private bool ProcessHeartBeatMessage(IMessage message)
        {
            var shouldHandle = IsHeartBeatMessage(message);
            if (shouldHandle)
            {
                var payload = message.GetPayload<HeartBeatMessage>();
                var socketIdentifier = new SocketIdentifier(payload.SocketIdentity);
                ClusterMemberMeta meta;
                if (peers.Find(ref socketIdentifier, out meta))
                {
                    meta.LastKnownHeartBeat = DateTime.UtcNow;
                    //logger.Debug($"Received HeartBeat from node {socketIdentifier}");
                }
                else
                {
                    //TODO: Send DicoveryMessage? What if peer is not supporting message Domains to be used by this node?
                    logger.Warn($"HeartBeat came from unknown peer: SocketIdentity [{payload.SocketIdentity.GetAnyString()}]");
                }
            }

            return shouldHandle;
        }

        private ISocket CreateSubscriberSocket()
        {
            var socket = socketFactory.CreateSubscriberSocket();
            socket.Connect(config.IntercomEndpoint, true);
            socket.Subscribe();

            return socket;
        }

        private ISocket CreatePublisherSocket()
        {
            var socket = socketFactory.CreatePublisherSocket();
            socket.Bind(config.IntercomEndpoint);

            return socket;
        }

        private static bool IsHeartBeatMessage(IMessage message)
            => message.Equals(KinoMessages.HeartBeat);

        private static bool IsCheckDeadPeersMessage(IMessage message)
            => message.Equals(KinoMessages.CheckDeadPeers);

        private static bool IsStartPeerMonitoringMessage(IMessage message)
            => message.Equals(KinoMessages.StartPeerMonitoring);

        private static bool IsAddPeerMessage(IMessage message)
            => message.Equals(KinoMessages.AddPeer);

        private static bool IsDeletePeerMessage(IMessage message)
            => message.Equals(KinoMessages.DeletePeer);

        private bool IsCheckStalePeersMessage(IMessage message)
            => message.Equals(KinoMessages.CheckStalePeers);
    }
}