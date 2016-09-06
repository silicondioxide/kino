﻿using System;
using System.Security;
using System.Threading;
using kino.Core.Connectivity;
using kino.Core.Diagnostics;
using kino.Core.Framework;
using kino.Core.Messaging;
using kino.Core.Security;
using kino.Tests.Actors.Setup;
using kino.Tests.Helpers;
using Moq;
using NUnit.Framework;

namespace kino.Tests.Connectivity
{
    [TestFixture]
    public class RouteDiscoveryTests
    {
        private static readonly TimeSpan AsyncOp = TimeSpan.FromMilliseconds(500);
        private IRouteDiscovery routerDiscovery;
        private Mock<IClusterMessageSender> clusterMessageSender;
        private RouterConfiguration routerConfiguration;
        private readonly string localhost = "tcp://localhost:43";
        private ClusterMembershipConfiguration membershipConfiguration;
        private Mock<ISecurityProvider> securityProvider;
        private string domain;
        private Mock<ILogger> logger;
        private Mock<IRouterConfigurationProvider> routerConfigurationProvider;

        [SetUp]
        public void Setup()
        {
            membershipConfiguration = new ClusterMembershipConfiguration
                                      {
                                          PongSilenceBeforeRouteDeletion = TimeSpan.FromSeconds(10),
                                          RouteDiscovery = new RouteDiscoveryConfiguration
                                                           {
                                                               SendingPeriod = AsyncOp.DivideBy(20),
                                                               RequestsPerSend = 10,
                                                               MaxRequestsQueueLength = 1000
                                                           }
                                      };
            clusterMessageSender = new Mock<IClusterMessageSender>();
            logger = new Mock<ILogger>();
            domain = Guid.NewGuid().ToString();
            securityProvider = new Mock<ISecurityProvider>();
            securityProvider.Setup(m => m.DomainIsAllowed(domain)).Returns(true);
            securityProvider.Setup(m => m.GetAllowedDomains()).Returns(new[] {domain});
            securityProvider.As<ISignatureProvider>().Setup(m => m.CreateSignature(domain, It.IsAny<byte[]>())).Returns(new byte[0]);
            securityProvider.As<ISignatureProvider>().Setup(m => m.CreateSignature(It.Is<string>(d => d != domain), It.IsAny<byte[]>())).Throws<SecurityException>();
            var routerConfiguration = new RouterConfiguration
                                      {
                                          RouterAddress = new SocketEndpoint(new Uri("inproc://router"), SocketIdentifier.CreateIdentity())
                                      };
            var scaleOutAddress = new SocketEndpoint(new Uri("tcp://127.0.0.1:5000"), SocketIdentifier.CreateIdentity());
            routerConfigurationProvider = new Mock<IRouterConfigurationProvider>();
            routerConfigurationProvider.Setup(m => m.GetRouterConfiguration()).Returns(routerConfiguration);
            routerConfigurationProvider.Setup(m => m.GetScaleOutAddress()).Returns(scaleOutAddress);
            routerDiscovery = new RouteDiscovery(clusterMessageSender.Object,
                                                 routerConfigurationProvider.Object,
                                                 membershipConfiguration,
                                                 securityProvider.Object,
                                                 logger.Object);
        }

        [Test]
        public void RouteDiscoveryRequest_IsSentOnlyForMessagesFromAllowedDomains()
        {
            try
            {
                routerDiscovery.Start();

                var unsupportedIdentifier = MessageIdentifier.Create<SimpleMessage>();
                var messageIdentifiers = new[]
                                         {
                                             MessageIdentifier.Create<NullMessage>(),
                                             MessageIdentifier.Create<AsyncMessage>()
                                         };
                foreach (var messageIdentifier in messageIdentifiers)
                {
                    securityProvider.Setup(m => m.GetDomain(messageIdentifier.Identity)).Returns(domain);
                }
                securityProvider.Setup(m => m.GetDomain(unsupportedIdentifier.Identity)).Throws(new MessageNotSupportedException(""));

                routerDiscovery.RequestRouteDiscovery(unsupportedIdentifier);
                foreach (var messageIdentifier in messageIdentifiers)
                {
                    routerDiscovery.RequestRouteDiscovery(messageIdentifier);
                }
                AsyncOp.Sleep();

                clusterMessageSender.Verify(m => m.EnqueueMessage(It.IsAny<IMessage>()), Times.Exactly(messageIdentifiers.Length));
                logger.Verify(m => m.Error(It.IsAny<MessageNotSupportedException>()), Times.Once);
            }
            finally
            {
                routerDiscovery.Stop();
            }
        }

        [Test]
        public void RouteDiscoveryRequestForMessageHubIdentifier_IsSentForAllAllowedDomains()
        {
            try
            {
                routerDiscovery.Start();

                var domains = new[] {Guid.NewGuid().ToString(), Guid.NewGuid().ToString()};
                securityProvider.Setup(m => m.GetAllowedDomains()).Returns(domains);
                foreach (var domain in domains)
                {
                    securityProvider.As<ISignatureProvider>().Setup(m => m.CreateSignature(domain, It.IsAny<byte[]>())).Returns(new byte[0]);
                }
                var messageHubIdentifier = new MessageIdentifier(Guid.NewGuid().ToByteArray());
                //
                routerDiscovery.RequestRouteDiscovery(messageHubIdentifier);
                //
                AsyncOp.Sleep();

                clusterMessageSender.Verify(m => m.EnqueueMessage(It.IsAny<IMessage>()), Times.Exactly(domains.Length));
            }
            finally
            {
                routerDiscovery.Stop();
            }
        }
    }
}