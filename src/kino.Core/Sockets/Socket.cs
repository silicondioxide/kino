﻿using System;
using System.Threading;
using kino.Core.Framework;
using kino.Core.Messaging;
using NetMQ;
using NetMQ.Sockets;

namespace kino.Core.Sockets
{
    internal class Socket : ISocket
    {
        private readonly NetMQSocket socket;
        private static readonly TimeSpan ReceiveWaitTimeout;
        private readonly TimeSpan sendingTimeout;

        static Socket()
        {
            ReceiveWaitTimeout = TimeSpan.FromSeconds(3);
        }

        internal Socket(NetMQSocket socket, SocketConfiguration config)
        {
            socket.Options.Linger = config.Linger;
            socket.Options.ReceiveHighWatermark = config.ReceivingHighWatermark;
            socket.Options.SendHighWatermark = config.SendingHighWatermark;
            sendingTimeout = config.SendTimeout;
            this.socket = socket;
        }

        public void SendMessage(IMessage message)
        {
            var multipart = new MultipartMessage((Message) message);
            if (!socket.TrySendMultipartMessage(sendingTimeout, new NetMQMessage(multipart.Frames)))
            {
                throw new TimeoutException($"Sending timed out after {sendingTimeout.TotalMilliseconds} ms!");
            }
        }

        public IMessage ReceiveMessage(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = new NetMQMessage();
                if (socket.TryReceiveMultipartMessage(ReceiveWaitTimeout, ref message))
                {
                    var multipart = new MultipartMessage(message);
                    return new Message(multipart);
                }
            }

            return null;
        }

        public void Connect(Uri address)
            => socket.Connect(address.ToSocketAddress());

        public void Disconnect(Uri address)
            => socket.Disconnect(address.ToSocketAddress());

        public void Bind(Uri address)
            => socket.Bind(address.ToSocketAddress());

        public void Unbind(Uri address)
            => socket.Unbind(address.ToSocketAddress());

        public void Subscribe(string topic = "")
            => ((SubscriberSocket)socket).Subscribe(topic);

        public void Subscribe(byte[] topic)
            => ((SubscriberSocket)socket).Subscribe(topic);

        public void Unsubscribe(string topic = "")
            => ((SubscriberSocket)socket).Unsubscribe(topic);

        public void SetMandatoryRouting(bool mandatory = true)
            => socket.Options.RouterMandatory = mandatory;

        public void SetReceiveHighWaterMark(int hwm)
            => socket.Options.ReceiveHighWatermark = hwm;

        public void SetIdentity(byte[] identity)
            => socket.Options.Identity = identity;

        public byte[] GetIdentity()
            => socket.Options.Identity;

        public void Dispose()
            => socket.Dispose();
    }
}