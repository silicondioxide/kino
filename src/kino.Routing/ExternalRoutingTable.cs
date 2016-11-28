﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using C5;
using kino.Core;
using kino.Core.Diagnostics;
using kino.Core.Framework;
using kino.Messaging;
using Bcl = System.Collections.Generic;

namespace kino.Routing
{
    public class ExternalRoutingTable : IExternalRoutingTable
    {
        private readonly Bcl.IDictionary<ReceiverIdentifier, Bcl.HashSet<ReceiverIdentifier>> nodeMessageHubs;
        private readonly Bcl.IDictionary<ReceiverIdentifier, Bcl.HashSet<ReceiverIdentifier>> nodeActors;
        private readonly Bcl.IDictionary<ReceiverIdentifier, Bcl.HashSet<MessageIdentifier>> actorToMessageMap;
        private readonly Bcl.IDictionary<MessageIdentifier, HashedLinkedList<ReceiverIdentifier>> messageToNodeMap;
        private readonly Bcl.IDictionary<ReceiverIdentifier, PeerConnection> nodeToConnectionMap;
        private readonly Bcl.IDictionary<PeerConnection, Bcl.HashSet<ReceiverIdentifier>> connectionToNodeMap;
        private readonly ILogger logger;

        public ExternalRoutingTable(ILogger logger)
        {
            this.logger = logger;
            nodeMessageHubs = new Bcl.Dictionary<ReceiverIdentifier, Bcl.HashSet<ReceiverIdentifier>>();
            nodeActors = new Bcl.Dictionary<ReceiverIdentifier, Bcl.HashSet<ReceiverIdentifier>>();
            actorToMessageMap = new Bcl.Dictionary<ReceiverIdentifier, Bcl.HashSet<MessageIdentifier>>();
            messageToNodeMap = new Bcl.Dictionary<MessageIdentifier, HashedLinkedList<ReceiverIdentifier>>();
            nodeToConnectionMap = new Bcl.Dictionary<ReceiverIdentifier, PeerConnection>();
            connectionToNodeMap = new ConcurrentDictionary<PeerConnection, Bcl.HashSet<ReceiverIdentifier>>();
        }

        public PeerConnection AddMessageRoute(ExternalRouteRegistration routeRegistration)
        {
            var nodeIdentifier = new ReceiverIdentifier(routeRegistration.Peer.SocketIdentity);

            if (routeRegistration.Route.Receiver.IsActor())
            {
                MapMessageToNode(routeRegistration, nodeIdentifier);
                MapActorToMessage(routeRegistration);
                MapActorToNode(routeRegistration, nodeIdentifier);
            }
            else
            {
                if (routeRegistration.Route.Receiver.IsMessageHub())
                {
                    MapMessageHubToNode(routeRegistration, nodeIdentifier);
                }
                else
                {
                    throw new ArgumentException($"Requested registration is for unknown Receiver type: [{routeRegistration.Route.Receiver}]!");
                }
            }

            var connection = MapNodeToConnection(routeRegistration, nodeIdentifier);
            MapConnectionToNode(connection, nodeIdentifier);

            return connection;
        }

        private void MapConnectionToNode(PeerConnection connection, ReceiverIdentifier nodeIdentifier)
        {
            Bcl.HashSet<ReceiverIdentifier> nodes;
            if (!connectionToNodeMap.TryGetValue(connection, out nodes))
            {
                nodes = new Bcl.HashSet<ReceiverIdentifier>();
                connectionToNodeMap[connection] = nodes;
            }

            nodes.Add(nodeIdentifier);
        }

        private PeerConnection MapNodeToConnection(ExternalRouteRegistration routeRegistration, ReceiverIdentifier nodeIdentifier)
        {
            var peerConnection = default(PeerConnection);
            if (!nodeToConnectionMap.TryGetValue(nodeIdentifier, out peerConnection))
            {
                peerConnection = new PeerConnection
                                 {
                                     Node = routeRegistration.Peer,
                                     Health = routeRegistration.Health,
                                     Connected = false
                                 };
                nodeToConnectionMap[nodeIdentifier] = peerConnection;
            }
            return peerConnection;
        }

        private void MapMessageHubToNode(ExternalRouteRegistration routeRegistration, ReceiverIdentifier nodeIdentifier)
        {
            var messageHub = routeRegistration.Route.Receiver;
            Bcl.HashSet<ReceiverIdentifier> messageHubs;
            if (!nodeMessageHubs.TryGetValue(nodeIdentifier, out messageHubs))
            {
                messageHubs = new Bcl.HashSet<ReceiverIdentifier>();
                nodeMessageHubs[nodeIdentifier] = messageHubs;
            }
            messageHubs.Add(messageHub);
        }

        private void MapActorToMessage(ExternalRouteRegistration routeRegistration)
        {
            Bcl.HashSet<MessageIdentifier> actorMessages;
            if (!actorToMessageMap.TryGetValue(routeRegistration.Route.Receiver, out actorMessages))
            {
                actorMessages = new Bcl.HashSet<MessageIdentifier>();
                actorToMessageMap[routeRegistration.Route.Receiver] = actorMessages;
            }
            actorMessages.Add(routeRegistration.Route.Message);
        }

        private void MapActorToNode(ExternalRouteRegistration routeRegistration, ReceiverIdentifier nodeIdentifier)
        {
            Bcl.HashSet<ReceiverIdentifier> actors;
            if (!nodeActors.TryGetValue(nodeIdentifier, out actors))
            {
                actors = new Bcl.HashSet<ReceiverIdentifier>();
                nodeActors[nodeIdentifier] = actors;
            }
            actors.Add(routeRegistration.Route.Receiver);
        }

        private void MapMessageToNode(ExternalRouteRegistration routeRegistration, ReceiverIdentifier nodeIdentifier)
        {
            var messageIdentifier = routeRegistration.Route.Message;
            HashedLinkedList<ReceiverIdentifier> nodes;
            if (!messageToNodeMap.TryGetValue(messageIdentifier, out nodes))
            {
                nodes = new HashedLinkedList<ReceiverIdentifier>();
                messageToNodeMap[messageIdentifier] = nodes;
            }
            if (!nodes.Contains(nodeIdentifier))
            {
                nodes.InsertLast(nodeIdentifier);
            }
        }

        public Bcl.IEnumerable<PeerConnection> FindRoutes(ExternalRouteLookupRequest lookupRequest)
        {
            var peers = new Bcl.List<PeerConnection>();
            PeerConnection peerConnection;
            if (lookupRequest.ReceiverNodeIdentity.IsSet()
                && nodeToConnectionMap.TryGetValue(lookupRequest.ReceiverNodeIdentity, out peerConnection))
            {
                peers.Add(peerConnection);
            }
            else
            {
                HashedLinkedList<ReceiverIdentifier> nodes;
                if (messageToNodeMap.TryGetValue(lookupRequest.Message, out nodes))
                {
                    if (lookupRequest.Distribution == DistributionPattern.Unicast)
                    {
                        peers.Add(nodeToConnectionMap[Get(nodes)]);
                    }
                    else
                    {
                        if (lookupRequest.Distribution == DistributionPattern.Broadcast)
                        {
                            foreach (var node in nodes)
                            {
                                peers.Add(nodeToConnectionMap[node]);
                            }
                        }
                    }
                }
            }

            return peers;
        }

        private static T Get<T>(HashedLinkedList<T> hashSet)
        {
            var count = hashSet.Count;
            if (count > 0)
            {
                var first = (count > 1) ? hashSet.RemoveFirst() : hashSet.First;
                if (count > 1)
                {
                    hashSet.InsertLast(first);
                }
                return first;
            }

            return default(T);
        }

        public PeerRemoveResult RemoveNodeRoute(ReceiverIdentifier nodeIdentifier)
        {
            PeerConnection connection;
            var peerConnectionAction = PeerConnectionAction.NotFound;

            if (nodeToConnectionMap.TryGetValue(nodeIdentifier, out connection))
            {
                nodeToConnectionMap.Remove(nodeIdentifier);
                peerConnectionAction = RemovePeerNode(connection, nodeIdentifier);
                nodeMessageHubs.Remove(nodeIdentifier);
                Bcl.HashSet<ReceiverIdentifier> actors;
                if (nodeActors.TryGetValue(nodeIdentifier, out actors))
                {
                    nodeActors.Remove(nodeIdentifier);

                    foreach (var actor in actors)
                    {
                        Bcl.HashSet<MessageIdentifier> messages;
                        if (actorToMessageMap.TryGetValue(actor, out messages))
                        {
                            actorToMessageMap.Remove(actor);

                            foreach (var message in messages)
                            {
                                HashedLinkedList<ReceiverIdentifier> nodes;
                                if (messageToNodeMap.TryGetValue(message, out nodes))
                                {
                                    nodes.Remove(nodeIdentifier);
                                    if (!nodes.Any())
                                    {
                                        messageToNodeMap.Remove(message);
                                    }
                                }
                            }
                        }
                    }
                }

                logger.Debug($"External route removed Uri:{connection.Node.Uri.AbsoluteUri} " +
                             $"Socket:{nodeIdentifier.Identity.GetAnyString()}");
            }
            return new PeerRemoveResult
                   {
                       Uri = connection?.Node.Uri,
                       ConnectionAction = peerConnectionAction
                   };
        }

        private PeerConnectionAction RemovePeerNode(PeerConnection connection, ReceiverIdentifier nodeIdentifier)
        {
            Bcl.HashSet<ReceiverIdentifier> nodes;
            if (connectionToNodeMap.TryGetValue(connection, out nodes))
            {
                if (nodes.Remove(nodeIdentifier))
                {
                    if (!nodes.Any())
                    {
                        connectionToNodeMap.Remove(connection);

                        return PeerConnectionAction.Disconnect;
                    }

                    return PeerConnectionAction.KeepConnection;
                }
            }

            return PeerConnectionAction.NotFound;
        }

        public PeerRemoveResult RemoveMessageRoute(ExternalRouteRemoval routeRemoval)
        {
            PeerConnection connection = null;
            var connectionAction = PeerConnectionAction.NotFound;

            var nodeIdentifier = new ReceiverIdentifier(routeRemoval.Peer.SocketIdentity);
            if (nodeToConnectionMap.TryGetValue(nodeIdentifier, out connection))
            {
                if (routeRemoval.Route.Receiver.IsMessageHub())
                {
                    RemoveMessageHubRoute(routeRemoval);
                }
                else
                {
                    if (routeRemoval.Route.Receiver.IsActor())
                    {
                        RemoveActorRoute(routeRemoval);
                    }
                    else
                    {
                        //TODO: Remove all actors by message
                        HashedLinkedList<ReceiverIdentifier> nodes;
                        if (messageToNodeMap.TryGetValue(routeRemoval.Route.Message, out nodes))
                        {
                            if (nodes.Remove(nodeIdentifier))
                            {
                                if (!nodes.Any())
                                {
                                    messageToNodeMap.Remove(routeRemoval.Route.Message);
                                }
                                Bcl.HashSet<ReceiverIdentifier> actors;
                                var emptyActors = new Bcl.List<ReceiverIdentifier>();
                                if (nodeActors.TryGetValue(nodeIdentifier, out actors))
                                {
                                    foreach (var actor in actors)
                                    {
                                        Bcl.HashSet<MessageIdentifier> messages;
                                        if (actorToMessageMap.TryGetValue(actor, out messages))
                                        {
                                            if (messages.Remove(routeRemoval.Route.Message))
                                            {
                                                if (!messages.Any())
                                                {
                                                    actorToMessageMap.Remove(actor);
                                                    emptyActors.Add(actor);
                                                }
                                            }
                                        }
                                    }
                                    foreach (var emptyActor in emptyActors)
                                    {
                                        actors.Remove(emptyActor);
                                    }
                                    if (!actors.Any())
                                    {
                                        nodeActors.Remove(nodeIdentifier);
                                    }
                                }
                            }
                        }
                    }
                }
                if (!nodeActors.ContainsKey(nodeIdentifier) && !nodeMessageHubs.ContainsKey(nodeIdentifier))
                {
                    nodeToConnectionMap.Remove(nodeIdentifier);
                    connectionAction = RemovePeerNode(connection, nodeIdentifier);
                    logger.Debug($"External route removed Uri:{connection?.Node.Uri.AbsoluteUri} " +
                                 $"Socket:{nodeIdentifier}");
                }
            }

            return new PeerRemoveResult
                   {
                       ConnectionAction = connectionAction,
                       Uri = connection?.Node.Uri
                   };
        }

        private void RemoveActorRoute(ExternalRouteRemoval routeRemoval)
        {
            var nodeIdentifier = new ReceiverIdentifier(routeRemoval.Peer.SocketIdentity);
            Bcl.HashSet<MessageIdentifier> messages;
            if (actorToMessageMap.TryGetValue(routeRemoval.Route.Receiver, out messages))
            {
                messages.Remove(routeRemoval.Route.Message);
                if (!messages.Any())
                {
                    actorToMessageMap.Remove(routeRemoval.Route.Receiver);
                    RemoveNodeActor(routeRemoval, nodeIdentifier);
                }
                logger.Debug("External message route removed " +
                             $"Socket:{nodeIdentifier} " +
                             $"Message:[{routeRemoval.Route.Message}]");
            }
        }

        private void RemoveNodeActor(ExternalRouteRemoval routeRemoval, ReceiverIdentifier nodeIdentifier)
        {
            Bcl.HashSet<ReceiverIdentifier> actors;
            if (nodeActors.TryGetValue(nodeIdentifier, out actors))
            {
                if (actors.Remove(routeRemoval.Route.Receiver))
                {
                    if (!actors.Any())
                    {
                        nodeActors.Remove(nodeIdentifier);
                    }
                    RemoveMessageToNodeMap(routeRemoval, nodeIdentifier);
                }
            }
        }

        private void RemoveMessageToNodeMap(ExternalRouteRemoval routeRemoval, ReceiverIdentifier receiverNode)
        {
            HashedLinkedList<ReceiverIdentifier> nodes;
            if (messageToNodeMap.TryGetValue(routeRemoval.Route.Message, out nodes))
            {
                nodes.Remove(receiverNode);
                if (!nodes.Any())
                {
                    messageToNodeMap.Remove(routeRemoval.Route.Message);
                }
            }
        }

        private void RemoveMessageHubRoute(ExternalRouteRemoval routeRemoval)
        {
            var nodeIdentifier = new ReceiverIdentifier(routeRemoval.Peer.SocketIdentity);
            Bcl.HashSet<ReceiverIdentifier> messageHubs;
            if (nodeMessageHubs.TryGetValue(nodeIdentifier, out messageHubs))
            {
                if (messageHubs.Remove(routeRemoval.Route.Receiver))
                {
                    if (!messageHubs.Any())
                    {
                        nodeMessageHubs.Remove(nodeIdentifier);
                    }
                    logger.Debug("External MessageHub removed " +
                                 $"Node:[{nodeIdentifier}] " +
                                 $"Identity:[{routeRemoval.Route.Receiver}]");
                }
            }
        }
    }
}