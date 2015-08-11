﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using rawf.Framework;
using rawf.Messaging;
using rawf.Messaging.Messages;
using rawf.Sockets;

namespace rawf.Connectivity
{
    public class ClusterConfigurationMonitor : IClusterConfigurationMonitor
    {
        private readonly ISocketFactory socketFactory;
        private CancellationTokenSource monitoringToken;
        private CancellationTokenSource messageProcessingToken;
        private readonly BlockingCollection<IMessage> outgoingMessages;
        private readonly IClusterConfiguration clusterConfiguration;
        private readonly IRouterConfiguration routerConfiguration;
        private Task sendingMessages;
        private Task listenningMessages;
        private Task monitorRendezvous;
        private readonly ManualResetEventSlim pingReceived;
        private readonly IRendezvousConfiguration rendezvousConfiguration;
        private ISocket clusterMonitorSubscriptionSocket;
        private ISocket clusterMonitorSendingSocket;

        public ClusterConfigurationMonitor(ISocketFactory socketFactory,
                                           IRouterConfiguration routerConfiguration,
                                           IClusterConfiguration clusterConfiguration,
                                           IRendezvousConfiguration rendezvousConfiguration)
        {
            pingReceived = new ManualResetEventSlim(false);
            this.socketFactory = socketFactory;
            this.routerConfiguration = routerConfiguration;
            this.clusterConfiguration = clusterConfiguration;
            this.rendezvousConfiguration = rendezvousConfiguration;
            outgoingMessages = new BlockingCollection<IMessage>(new ConcurrentQueue<IMessage>());
            
        }

        public void Start()
        {
            StartProcessingClusterMessages();
            StartRendezvousMonitoring();
        }
        
        public void Stop()
        {
            StartRendezvousMonitoring();
            StopProcessingClusterMessages();
        }
        
        private void StartRendezvousMonitoring()
        {
            monitoringToken = new CancellationTokenSource();
            monitorRendezvous = Task.Factory.StartNew(_ => RendezvousConnectionMonitor(monitoringToken.Token),
                                                          TaskCreationOptions.LongRunning);
        }
        
        private void StartProcessingClusterMessages()
        {
            messageProcessingToken = new CancellationTokenSource();
            const int participantCount = 3;
            using (var gateway = new Barrier(participantCount))
            {
                sendingMessages = Task.Factory.StartNew(_ => SendMessages(messageProcessingToken.Token, gateway),
                                                            TaskCreationOptions.LongRunning);
                listenningMessages = Task.Factory.StartNew(_ => ListenMessages(messageProcessingToken.Token, gateway),
                                                           TaskCreationOptions.LongRunning);
                gateway.SignalAndWait(messageProcessingToken.Token);
            }
        }

        private void StopProcessingClusterMessages()
        {
            messageProcessingToken.Cancel(true);
            sendingMessages.Wait();
            listenningMessages.Wait();
            messageProcessingToken.Dispose();
        }
        
        private void StopRendezvousMonitoring()
        {
            monitoringToken.Cancel(true);
            monitorRendezvous.Wait();
            monitoringToken.Dispose();
            pingReceived.Dispose();
        }

        private void RendezvousConnectionMonitor(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (PingSilence(token))
                    {
                        rendezvousConfiguration.RotateRendezvousServers();
                        
                        StopProcessingClusterMessages();
                        StartProcessingClusterMessages();
                        
                        var rendezvousServer = rendezvousConfiguration.GetCurrentRendezvousServer();
                        Console.WriteLine($"Reconnected to {rendezvousServer.BroadcastUri.AbsoluteUri}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception err)
            {
                Console.WriteLine(err);
            }
        }

        private bool PingSilence(CancellationToken token)
        {
            if (pingReceived.Wait(clusterConfiguration.PingSilenceBeforeRendezvousFailover, token))
            {
                pingReceived.Reset();
                return false;
            } 
            
            return true;
        }

        private void SendMessages(CancellationToken token, Barrier gateway)
        {
            try
            {
                using (clusterMonitorSendingSocket = CreateClusterMonitorSendingSocket())
                {
                    gateway.SignalAndWait(token);
                    try
                    {
                        foreach (var messageOut in outgoingMessages.GetConsumingEnumerable(token))
                        {
                            clusterMonitorSendingSocket.SendMessage(messageOut);
                            // TODO: Block immediatelly for the response
                            // Otherwise, consider the RS dead and switch to failover partner
                            //sendingSocket.ReceiveMessage(token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    clusterMonitorSendingSocket.SendMessage(CreateUnregisterRoutingMessage());
                }
            }
            catch (Exception err)
            {
                Console.WriteLine(err);
            }
        }

        private void ListenMessages(CancellationToken token, Barrier gateway)
        {
            try
            {
                using (clusterMonitorSubscriptionSocket = CreateClusterMonitorSubscriptionSocket())
                {
                    using (var routerNotificationSocket = CreateRouterCommunicationSocket())
                    {
                        gateway.SignalAndWait(token);

                        while (!token.IsCancellationRequested)
                        {
                            var message = clusterMonitorSubscriptionSocket.ReceiveMessage(token);
                            if (message != null)
                            {
                                ProcessIncomingMessage(message, routerNotificationSocket);
                                UnregisterDeadNodes(routerNotificationSocket);
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Console.WriteLine(err);
            }
        }

        private ISocket CreateClusterMonitorSubscriptionSocket()
        {
            var rendezvousServer = rendezvousConfiguration.GetCurrentRendezvousServer();
            var socket = socketFactory.CreateSubscriberSocket();
            socket.Connect(rendezvousServer.BroadcastUri);
            socket.Subscribe();

            Console.WriteLine($"Connected to {rendezvousServer.BroadcastUri.AbsoluteUri}");

            return socket;
        }

        private ISocket CreateClusterMonitorSendingSocket()
        {
            var rendezvousServer = rendezvousConfiguration.GetCurrentRendezvousServer();
            var socket = socketFactory.CreateDealerSocket();            
            //socket.SetIdentity(SocketIdentifier.CreateNew());
            socket.Connect(rendezvousServer.UnicastUri);

            return socket;
        }

        private ISocket CreateRouterCommunicationSocket()
        {
            var socket = socketFactory.CreateDealerSocket();
            socket.Connect(routerConfiguration.RouterAddress.Uri);

            return socket;
        }

        private bool ProcessIncomingMessage(IMessage message, ISocket routerNotificationSocket)
            => RegisterExternalRoute(message, routerNotificationSocket)
               || Ping(message)
               || Pong(message)
               || RequestMessageHandlersRouting(message, routerNotificationSocket)
               || UnregisterRoute(message, routerNotificationSocket);

        private bool UnregisterRoute(IMessage message, ISocket routerNotificationSocket)
        {
            var shouldHandle = IsUnregisterMessageHandlersRouting(message);
            if (shouldHandle)
            {
                var payload = message.GetPayload<UnregisterMessageHandlersRoutingMessage>();
                clusterConfiguration.DeleteClusterMember(new SocketEndpoint(new Uri(payload.Uri), payload.SocketIdentity));
                routerNotificationSocket.SendMessage(message);
            }

            return shouldHandle;
        }

        private bool RegisterExternalRoute(IMessage message, ISocket routerNotificationSocket)
        {
            var shouldHandler = IsRegisterExternalRoute(message);
            if (shouldHandler)
            {
                AddClusterMember(message);
                routerNotificationSocket.SendMessage(message);
            }

            return shouldHandler;
        }

        private bool Ping(IMessage message)
        {
            var shouldHandle = IsPing(message);
            if (shouldHandle)
            {
                pingReceived.Set();
                SendPong();
                //Console.WriteLine("Ping");
            }

            return shouldHandle;
        }

        private bool Pong(IMessage message)
        {
            var shouldHandle = IsPong(message);
            if (shouldHandle)
            {
                ProcessPongMessage(message);
            }

            return shouldHandle;
        }

        private bool RequestMessageHandlersRouting(IMessage message, ISocket routerNotificationSocket)
        {
            var shouldHandle = IsRequestAllMessageHandlersRouting(message)
                               || IsRequestNodeMessageHandlersRouting(message);
            if (shouldHandle)
            {
                routerNotificationSocket.SendMessage(message);
            }

            return shouldHandle;
        }

        public void RegisterSelf(IEnumerable<MessageHandlerIdentifier> messageHandlers)
        {
            var message = Message.Create(new RegisterMessageHandlersRoutingMessage
                                         {
                                             Uri = routerConfiguration.ScaleOutAddress.Uri.ToSocketAddress(),
                                             SocketIdentity = routerConfiguration.ScaleOutAddress.Identity,
                                             MessageHandlers = messageHandlers.Select(mh => new MessageHandlerRegistration
                                                                                            {
                                                                                                Version = mh.Version,
                                                                                                Identity = mh.Identity
                                                                                            }).ToArray()
                                         },
                                         RegisterMessageHandlersRoutingMessage.MessageIdentity);
            outgoingMessages.Add(message);

            Console.WriteLine($"Self-registration URI: {routerConfiguration.ScaleOutAddress.Uri.ToSocketAddress()} SOCKID: {routerConfiguration.ScaleOutAddress.Identity.GetString()} sent");
        }

        public void RequestMessageHandlersRouting()
        {
            var message = Message.Create(new RequestAllMessageHandlersRoutingMessage
                                         {
                                             RequestorSocketIdentity = routerConfiguration.ScaleOutAddress.Identity,
                                             RequestorUri = routerConfiguration.ScaleOutAddress.Uri.ToSocketAddress()
                                         },
                                         RequestAllMessageHandlersRoutingMessage.MessageIdentity);
            outgoingMessages.Add(message);
        }

        private IMessage CreateUnregisterRoutingMessage()
            => Message.Create(new UnregisterMessageHandlersRoutingMessage
                              {
                                  Uri = routerConfiguration.ScaleOutAddress.Uri.ToSocketAddress(),
                                  SocketIdentity = routerConfiguration.ScaleOutAddress.Identity
                              },
                              UnregisterMessageHandlersRoutingMessage.MessageIdentity);

        private bool IsRegisterExternalRoute(IMessage message)
        {
            if (Unsafe.Equals(RegisterMessageHandlersRoutingMessage.MessageIdentity, message.Identity))
            {
                var payload = message.GetPayload<RegisterMessageHandlersRoutingMessage>();

                return !ThisNodeSocket(payload.SocketIdentity);
            }

            return false;
        }

        private bool IsPing(IMessage message)
            => Unsafe.Equals(PingMessage.MessageIdentity, message.Identity);

        private bool ThisNodeSocket(byte[] socketIdentity)
            => Unsafe.Equals(routerConfiguration.ScaleOutAddress.Identity, socketIdentity);

        private bool IsPong(IMessage message)
        {
            if (Unsafe.Equals(PongMessage.MessageIdentity, message.Identity))
            {
                var payload = message.GetPayload<PongMessage>();

                //Console.WriteLine($"Pong from URI:{payload.Uri} SOCKID;{payload.SocketIdentity.GetString()}");

                return !ThisNodeSocket(payload.SocketIdentity);
            }

            return false;
        }

        private bool IsUnregisterMessageHandlersRouting(IMessage message)
        {
            if (Unsafe.Equals(UnregisterMessageHandlersRoutingMessage.MessageIdentity, message.Identity))
            {
                var payload = message.GetPayload<UnregisterMessageHandlersRoutingMessage>();

                return !ThisNodeSocket(payload.SocketIdentity);
            }

            return false;
        }

        private bool IsRequestAllMessageHandlersRouting(IMessage message)
        {
            if (Unsafe.Equals(RequestAllMessageHandlersRoutingMessage.MessageIdentity, message.Identity))
            {
                var payload = message.GetPayload<RequestAllMessageHandlersRoutingMessage>();

                return !ThisNodeSocket(payload.RequestorSocketIdentity);
            }

            return false;
        }

        private bool IsRequestNodeMessageHandlersRouting(IMessage message)
        {
            if (Unsafe.Equals(RequestNodeMessageHandlersRoutingMessage.MessageIdentity, message.Identity))
            {
                var payload = message.GetPayload<RequestNodeMessageHandlersRoutingMessage>();

                return ThisNodeSocket(payload.TargetNodeIdentity);
            }

            return false;
        }

        private void SendPong()
        {
            var message = Message.Create(new PongMessage
                                         {
                                             Uri = routerConfiguration.ScaleOutAddress.Uri.ToSocketAddress(),
                                             SocketIdentity = routerConfiguration.ScaleOutAddress.Identity
                                         },
                                         PongMessage.MessageIdentity);
            outgoingMessages.Add(message);
        }

        private void AddClusterMember(IMessage message)
        {
            var registration = message.GetPayload<RegisterMessageHandlersRoutingMessage>();
            var clusterMember = new SocketEndpoint(new Uri(registration.Uri), registration.SocketIdentity);
            clusterConfiguration.AddClusterMember(clusterMember);
            Console.WriteLine($"Route added URI:{clusterMember.Uri.AbsoluteUri} SOCKID:{clusterMember.Identity.GetString()}");
        }

        private void ProcessPongMessage(IMessage message)
        {
            var payload = message.GetPayload<PongMessage>();

            var nodeNotFound = clusterConfiguration.KeepAlive(new SocketEndpoint(new Uri(payload.Uri), payload.SocketIdentity));
            if (!nodeNotFound)
            {
                RequestNodeMessageHandlersRouting(payload);
                Console.WriteLine($"Route nod found, requesting URI:{payload.Uri} SOCKID:{payload.SocketIdentity.GetString()}");
            }
        }

        private void RequestNodeMessageHandlersRouting(PongMessage payload)
        {
            var request = Message.Create(new RequestNodeMessageHandlersRoutingMessage
                                         {
                                             TargetNodeIdentity = payload.SocketIdentity,
                                             TargetNodeUri = payload.Uri
                                         },
                                         RequestNodeMessageHandlersRoutingMessage.MessageIdentity);
            outgoingMessages.Add(request);
        }

        private void UnregisterDeadNodes(ISocket routerNotificationSocket)
        {
            foreach (var deadNode in clusterConfiguration.GetDeadMembers())
            {
                var message = Message.Create(new UnregisterMessageHandlersRoutingMessage
                                             {
                                                 Uri = deadNode.Uri.ToSocketAddress(),
                                                 SocketIdentity = deadNode.Identity
                                             },
                                             UnregisterMessageHandlersRoutingMessage.MessageIdentity);
                clusterConfiguration.DeleteClusterMember(deadNode);
                routerNotificationSocket.SendMessage(message);

                Console.WriteLine($"Route removed URI:{deadNode.Uri.AbsoluteUri} SOCKID:{deadNode.Identity.GetString()}");
            }
        }
    }
}