﻿using System.Collections.Generic;
using ProtoBuf;

namespace kino.Messaging.Messages
{
    [ProtoContract]
    public class RendezvousConfigurationChangedMessage : Payload
    {
        private static readonly byte[] MessageIdentity = BuildFullIdentity("RNDZVRECONFIG");
        private static readonly ushort MessageVersion = Message.CurrentVersion;

        [ProtoMember(1)]
        public IEnumerable<RendezvousNode> RendezvousNodes { get; set; }

        public override ushort Version => MessageVersion;

        public override byte[] Identity => MessageIdentity;
    }
}