﻿using System;
using System.Threading;
using Console.Messages;
using NetMQ;

namespace Console
{
    internal class Program
    {
        internal const string EndpointAddress = "tcp://127.0.0.1:5555";
        //TODO: Switch to inproc protocol after https://github.com/zeromq/netmq/pull/343 is released 
        //internal const string EndpointAddress = "inproc://localsocket";

        private static void Main(string[] args)
        {
            var context = NetMQContext.Create();
            var messageRouter = new MessageRouter(context);
            messageRouter.Start();

            Thread.Sleep(TimeSpan.FromSeconds(5));

            var actorHost = new ActorHost(context);
            actorHost.Start();
            var actor = new Actor();
            actorHost.AssignActor(actor);

            Thread.Sleep(TimeSpan.FromSeconds(2));

            var requestSink = new ClientRequestSink(context);
            requestSink.Start();
            var client = new Client(requestSink);

            var callbackPoint = client.CreateCallbackPoint(EhlloMessage.MessageIdentity);
            var message = Message.CreateFlowStartMessage(new HelloMessage {Greeting = "Hello"}, HelloMessage.MessageIdentity);
            message.RegisterCallbackPoint(callbackPoint);
            var response = client.Send(message).GetResponse().Result;
            var msg = response.GetPayload<EhlloMessage>();

            System.Console.WriteLine($"Received: {msg.Ehllo}");

            actorHost.Stop();
            requestSink.Stop();
            messageRouter.Stop();
            context.Dispose();
        }
    }
}