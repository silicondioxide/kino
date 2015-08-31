using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using rawf.Diagnostics;
using rawf.Framework;

namespace rawf.Connectivity
{
    public class ClusterConfiguration : IClusterConfiguration
    {
        private readonly ConcurrentDictionary<SocketEndpoint, ClusterMemberMeta> clusterMembers;
        private readonly ILogger logger;

        public ClusterConfiguration(ILogger logger)
        {
            this.logger = logger;
            clusterMembers = new ConcurrentDictionary<SocketEndpoint, ClusterMemberMeta>();
        }

        public IEnumerable<SocketEndpoint> GetClusterMembers()
            => clusterMembers.Keys;

        public void AddClusterMember(SocketEndpoint node)
        {
            clusterMembers.TryAdd(node, new ClusterMemberMeta {LastKnownPong = DateTime.UtcNow});

            logger.Debug($"New node added {nameof(node.Uri)}:{node.Uri.AbsoluteUri} " +
                  $"{nameof(node.Identity)}:{node.Identity.GetString()}");
        }

        public bool KeepAlive(SocketEndpoint node)
        {
            ClusterMemberMeta meta;
            var updated = clusterMembers.TryGetValue(node, out meta);
            if (updated)
            {
                meta.LastKnownPong = DateTime.UtcNow;
            }

            return updated;
        }

        public IEnumerable<SocketEndpoint> GetDeadMembers()
        {
            var now = DateTime.UtcNow;
            return clusterMembers
                .Where(mem => now - mem.Value.LastKnownPong > PongSilenceBeforeRouteDeletion)
                .Select(mem => mem.Key)
                .ToList();
        }

        public void DeleteClusterMember(SocketEndpoint node)
        {
            ClusterMemberMeta meta;
            clusterMembers.TryRemove(node, out meta);

            logger.Debug($"Dead route removed {nameof(node.Uri)}:{node.Uri.AbsoluteUri} " +
                             $"{nameof(node.Identity)}:{node.Identity.GetString()}");
        }

        public TimeSpan PingSilenceBeforeRendezvousFailover { get; set; }
        public TimeSpan PongSilenceBeforeRouteDeletion { get; set; }
    }
}