using System;
using System.Collections.Generic;
using System.Linq;
using kino.Core.Diagnostics;
using kino.Core.Framework;
using kino.Core.Messaging;
using kino.Core.Messaging.Messages;
using kino.Core.Security;
using kino.Core.Sockets;

namespace kino.Core.Connectivity.ServiceMessageHandlers
{
    public class InternalMessageRouteRegistrationHandler
    {
        private readonly IClusterMonitor clusterMonitor;
        private readonly IInternalRoutingTable internalRoutingTable;
        private readonly ISecurityProvider securityProvider;
        private readonly ILogger logger;

        public InternalMessageRouteRegistrationHandler(IClusterMonitorProvider clusterMonitorProvider,
                                                       IInternalRoutingTable internalRoutingTable,
                                                       ISecurityProvider securityProvider,
                                                       ILogger logger)
        {
            clusterMonitor = clusterMonitorProvider.GetClusterMonitor();
            this.internalRoutingTable = internalRoutingTable;
            this.securityProvider = securityProvider;
            this.logger = logger;
        }

        public bool Handle(InternalRouteRegistration routeRegistration)
        {
            //var shouldHandle = IsInternalMessageRoutingRegistration(message);
            //if (shouldHandle)
            //{
            //var payload = message.GetPayload<RegisterInternalMessageRouteMessage>();
            //var handlerSocketIdentifier = new SocketIdentifier(payload.SocketIdentity);

            if (routeRegistration.MessageContracts != null)
            {
                var newRoutes = UpdateLocalRoutingTable(routeRegistration);
                var messageGroups = GetMessageHandlers(newRoutes).Concat(GetMessageHubs(newRoutes))
                                                                 .GroupBy(mh => mh.Domain);
                foreach (var group in messageGroups)
                {
                    clusterMonitor.RegisterSelf(group.Select(g => g.Identity).ToList(), group.Key);
                }
            }
            //}

            return true;
        }

        private IEnumerable<IdentityDomainMap> GetMessageHandlers(IEnumerable<Identifier> newRoutes)
            => newRoutes.Where(mi => !mi.IsMessageHub())
                        .Select(mh => new IdentityDomainMap {Identity = mh, Domain = securityProvider.GetDomain(mh.Identity)});

        private IEnumerable<IdentityDomainMap> GetMessageHubs(IEnumerable<Identifier> newRoutes)
            => newRoutes.Where(mi => mi.IsMessageHub())
                        .SelectMany(mi => securityProvider.GetAllowedDomains().Select(dom => new IdentityDomainMap {Identity = mi, Domain = dom}));

        private IEnumerable<Identifier> UpdateLocalRoutingTable(InternalRouteRegistration routeRegistration)
        {
            var handlers = new List<Identifier>();

            foreach (var registration in routeRegistration.MessageContracts)
            {
                try
                {
                    //var messageIdentifier = registration.IsAnyIdentifier
                    //                            ? (Identifier) new AnyIdentifier(registration.Identity)
                    //                            : (Identifier) new MessageIdentifier(registration.Identity,
                    //                                                                 registration.Version,
                    //                                                                 registration.Partition);
                    internalRoutingTable.AddMessageRoute(new IdentityRegistration(registration.Identifier, registration.KeepRegistrationLocal),
                                                         routeRegistration.DestinationSocket);
                    if (!registration.KeepRegistrationLocal)
                    {
                        handlers.Add(registration.Identifier);
                    }
                }
                catch (Exception err)
                {
                    logger.Error(err);
                }
            }

            return handlers;
        }

        private static bool IsInternalMessageRoutingRegistration(IMessage message)
            => message.Equals(KinoMessages.RegisterInternalMessageRoute);
    }

    internal class IdentityDomainMap
    {
        internal Identifier Identity { get; set; }

        internal string Domain { get; set; }
    }
}