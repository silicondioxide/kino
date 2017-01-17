using System.Collections.Generic;
using System.Linq;
using kino.Core;
using kino.Core.Framework;

namespace kino.Cluster.Configuration
{
    public class NullScaleOutConfigurationManager : IScaleOutConfigurationManager
    {
        private readonly SocketEndpoint localEndpoint;

        public NullScaleOutConfigurationManager()
        {
            localEndpoint = new SocketEndpoint("tcp://localhost", IdentityExtensions.Empty);
        }

        public int GetScaleOutReceiveMessageQueueLength()
            => 0;

        public SocketEndpoint GetScaleOutAddress()
            => localEndpoint;

        public IEnumerable<SocketEndpoint> GetScaleOutAddressRange()
            => Enumerable.Empty<SocketEndpoint>();

        public void SetActiveScaleOutAddress(SocketEndpoint activeAddress)
        {
        }
    }
}