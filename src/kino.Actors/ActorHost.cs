﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kino.Core.Connectivity;
using kino.Core.Diagnostics;
using kino.Core.Diagnostics.Performance;
using kino.Core.Framework;
using kino.Core.Messaging;
using kino.Core.Messaging.Messages;
using kino.Core.Security;
using MessageContract = kino.Core.Connectivity.MessageContract;

namespace kino.Actors
{
    public partial class ActorHost : IActorHost
    {
        private readonly IActorHandlerMap actorHandlerMap;
        private Task syncProcessing;
        private Task asyncProcessing;
        private Task registrationsProcessing;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly IAsyncQueue<AsyncMessageContext> asyncQueue;
        private readonly ISecurityProvider securityProvider;
        private readonly IPerformanceCounterManager<KinoPerformanceCounters> performanceCounterManager;
        private readonly ILocalSocket<IMessage> localRouterSocket;
        private readonly ILocalSendingSocket<InternalRouteRegistration> internalRegistrationsSender;
        private readonly IAsyncQueue<IEnumerable<ActorMessageHandlerIdentifier>> actorRegistrationsQueue;
        private readonly ILogger logger;
        private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(3);
        private readonly ILocalSocket<IMessage> receivingSocket;

        public ActorHost(IActorHandlerMap actorHandlerMap,
                         IAsyncQueue<AsyncMessageContext> asyncQueue,
                         IAsyncQueue<IEnumerable<ActorMessageHandlerIdentifier>> actorRegistrationsQueue,
                         ISecurityProvider securityProvider,
                         IPerformanceCounterManager<KinoPerformanceCounters> performanceCounterManager,
                         ILocalSocket<IMessage> localRouterSocket,
                         ILocalSendingSocket<InternalRouteRegistration> internalRegistrationsSender,
                         ILocalSocketFactory localSocketFactory,
                         ILogger logger)
        {
            this.logger = logger;
            this.actorHandlerMap = actorHandlerMap;
            this.securityProvider = securityProvider;
            this.performanceCounterManager = performanceCounterManager;
            this.localRouterSocket = localRouterSocket;
            this.internalRegistrationsSender = internalRegistrationsSender;
            this.asyncQueue = asyncQueue;
            this.actorRegistrationsQueue = actorRegistrationsQueue;
            cancellationTokenSource = new CancellationTokenSource();
            receivingSocket = localSocketFactory.Create<IMessage>();
        }

        public void AssignActor(IActor actor)
        {
            var registrations = actorHandlerMap.Add(actor);
            if (registrations.Any())
            {
                actorRegistrationsQueue.Enqueue(registrations, cancellationTokenSource.Token);
            }
            else
            {
                logger.Warn($"Actor {actor.GetType().FullName} seems to not handle any message!");
            }
        }

        public bool CanAssignActor(IActor actor)
        {
            return actorHandlerMap.CanAdd(actor);
        }

        public void Start()
        {
            registrationsProcessing = Task.Factory.StartNew(_ => SafeExecute(() => RegisterActors(cancellationTokenSource.Token)),
                                                            cancellationTokenSource.Token,
                                                            TaskCreationOptions.LongRunning);
            syncProcessing = Task.Factory.StartNew(_ => SafeExecute(() => ProcessRequests(cancellationTokenSource.Token)),
                                                   cancellationTokenSource.Token,
                                                   TaskCreationOptions.LongRunning);
            asyncProcessing = Task.Factory.StartNew(_ => SafeExecute(() => ProcessAsyncResponses(cancellationTokenSource.Token)),
                                                    cancellationTokenSource.Token,
                                                    TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            cancellationTokenSource.Cancel();
            registrationsProcessing?.Wait(TerminationWaitTimeout);
            syncProcessing?.Wait(TerminationWaitTimeout);
            asyncProcessing?.Wait(TerminationWaitTimeout);
        }

        private void RegisterActors(CancellationToken token)
        {
            try
            {
                //using (var socket = CreateOneWaySocket(KinoPerformanceCounters.ActorHostRegistrationSocketSendRate))
                //{
                //var localSocketIdentity = localSocketIdentityPromise.Task.Result;

                foreach (var registrations in actorRegistrationsQueue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        SendActorRegistrationMessage(registrations);
                    }
                    catch (Exception err)
                    {
                        logger.Error(err);
                    }
                }
                //}
            }
            finally
            {
                actorRegistrationsQueue.Dispose();
            }
        }

        private void SendActorRegistrationMessage(IEnumerable<ActorMessageHandlerIdentifier> registrations)
        {
            //var payload = new RegisterInternalMessageRouteMessage
            //              {
            //                  SocketIdentity = identity,
            //                  MessageContracts = registrations.Select(mh => new MessageContract
            //                                                                {
            //                                                                    Identity = mh.Identifier.Identity,
            //                                                                    Version = mh.Identifier.Version,
            //                                                                    Partition = mh.Identifier.Partition,
            //                                                                    KeepRegistrationLocal = mh.KeepRegistrationLocal,
            //                                                                    IsAnyIdentifier = false
            //                                                                })
            //                                                  .ToArray<>()
            //              };
            //var message = Message.Create(payload);
            //socket.Send(message);

            var registration = new InternalRouteRegistration
                               {
                                   MessageContracts = registrations.Select(mh => new MessageContract
                                                                                 {
                                                                                     Identifier = new MessageIdentifier(mh.Identifier.Identity,
                                                                                                                        mh.Identifier.Version,
                                                                                                                        mh.Identifier.Partition),
                                                                                     KeepRegistrationLocal = mh.KeepRegistrationLocal
                                                                                 })
                                                                   .ToArray(),
                                   DestinationSocket = receivingSocket
                               };

            internalRegistrationsSender.Send(registration);
        }

        private void ProcessAsyncResponses(CancellationToken token)
        {
            try
            {
                //using (var localSocket = CreateOneWaySocket(KinoPerformanceCounters.ActorHostAsyncResponseSocketSendRate))
                //{
                foreach (var messageContext in asyncQueue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        foreach (var messageOut in messageContext.OutMessages.Cast<Message>())
                        {
                            messageOut.RegisterCallbackPoint(messageContext.CallbackReceiverIdentity,
                                                             messageContext.CallbackPoint,
                                                             messageContext.CallbackKey);
                            messageOut.SetCorrelationId(messageContext.CorrelationId);
                            messageOut.CopyMessageRouting(messageContext.MessageHops);
                            messageOut.TraceOptions |= messageContext.TraceOptions;

                            localRouterSocket.Send(messageOut);

                            ResponseSent(messageOut, false);
                        }
                    }
                    catch (Exception err)
                    {
                        logger.Error(err);
                    }
                }
                //}
            }
            finally
            {
                asyncQueue.Dispose();
            }
        }

        private void ProcessRequests(CancellationToken token)
        {
            //using (var localSocket = CreateRoutableSocket())
            //{
            //localSocketIdentityPromise.SetResult(localSocket.GetIdentity());

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (WaitHandle.WaitAny(new[]
                                           {
                                               receivingSocket.CanReceive(),
                                               token.WaitHandle
                                           }) == 0)
                    {
                        var message = (Message) receivingSocket.TryReceive();
                        if (message != null)
                        {
                            try
                            {
                                var actorIdentifier = new MessageIdentifier(message);
                                var handler = actorHandlerMap.Get(actorIdentifier);
                                if (handler != null)
                                {
                                    var task = handler(message);

                                    HandleTaskResult(token, task, message);
                                }
                                else
                                {
                                    HandlerNotFound(message);
                                }
                            }
                            catch (Exception err)
                            {
                                //TODO: Add more context to exception about which Actor failed
                                CallbackException(err, message);
                                logger.Error(err);
                            }
                        }
                    }
                }
                catch (Exception err)
                {
                    logger.Error(err);
                }
            }
            //}
        }

        private void HandleTaskResult(CancellationToken token, Task<IActorResult> task, Message messageIn)
        {
            if (task != null)
            {
                if (task.IsCompleted)
                {
                    var response = CreateTaskResultMessage(task).Messages;

                    MessageProcessed(messageIn, response);

                    foreach (var messageOut in response.Cast<Message>())
                    {
                        messageOut.RegisterCallbackPoint(messageIn.CallbackReceiverIdentity,
                                                         messageIn.CallbackPoint,
                                                         messageIn.CallbackKey);
                        messageOut.SetCorrelationId(messageIn.CorrelationId);
                        messageOut.CopyMessageRouting(messageIn.GetMessageRouting());
                        messageOut.TraceOptions |= messageIn.TraceOptions;

                        localRouterSocket.Send(messageOut);

                        ResponseSent(messageOut, true);
                    }
                }
                else
                {
                    task.ContinueWith(completed => SafeExecute(() => EnqueueTaskForCompletion(token, completed, messageIn)), token)
                        .ConfigureAwait(false);
                }
            }
        }

        private void CallbackException(Exception err, Message messageIn)
        {
            var messageOut = Message.Create(new ExceptionMessage
                                            {
                                                Exception = err,
                                                StackTrace = err.StackTrace
                                            },
                                            securityProvider.GetDomain(KinoMessages.Exception.Identity))
                                    .As<Message>();
            messageOut.RegisterCallbackPoint(messageIn.CallbackReceiverIdentity,
                                             messageIn.CallbackPoint,
                                             messageIn.CallbackKey);
            messageOut.SetCorrelationId(messageIn.CorrelationId);
            messageOut.CopyMessageRouting(messageIn.GetMessageRouting());
            messageOut.TraceOptions |= messageIn.TraceOptions;

            localRouterSocket.Send(messageOut);
        }

        private void EnqueueTaskForCompletion(CancellationToken token, Task<IActorResult> task, Message messageIn)
        {
            var asyncMessageContext = new AsyncMessageContext
                                      {
                                          OutMessages = CreateTaskResultMessage(task).Messages,
                                          CallbackPoint = messageIn.CallbackPoint,
                                          CallbackKey = messageIn.CallbackKey,
                                          CallbackReceiverIdentity = messageIn.CallbackReceiverIdentity,
                                          CorrelationId = messageIn.CorrelationId,
                                          MessageHops = messageIn.GetMessageRouting(),
                                          TraceOptions = messageIn.TraceOptions
                                      };
            asyncQueue.Enqueue(asyncMessageContext, token);
        }

        private IActorResult CreateTaskResultMessage(Task<IActorResult> task)
        {
            if (task.IsCanceled)
            {
                return new ActorResult(Message.Create(new ExceptionMessage
                                                      {
                                                          Exception = new OperationCanceledException()
                                                      },
                                                      securityProvider.GetDomain(KinoMessages.Exception.Identity)));
            }
            if (task.IsFaulted)
            {
                var err = task.Exception?.InnerException ?? task.Exception;

                return new ActorResult(Message.Create(new ExceptionMessage
                                                      {
                                                          Exception = err,
                                                          StackTrace = err?.StackTrace
                                                      },
                                                      securityProvider.GetDomain(KinoMessages.Exception.Identity)));
            }

            return task.Result ?? ActorResult.Empty;
        }

        //private ISocket CreateOneWaySocket(KinoPerformanceCounters couter)
        //{
        //    var socket = socketFactory.CreateDealerSocket();
        //    socket.SendRate = performanceCounterManager.GetCounter(couter);
        //    SocketHelper.SafeConnect(() => socket.Connect(routerConfiguration.RouterAddress.Uri));

        //    return socket;
        //}

        //private ISocket CreateRoutableSocket()
        //{
        //    var socket = socketFactory.CreateDealerSocket();
        //    socket.SetIdentity(SocketIdentifier.CreateIdentity());
        //    socket.ReceiveRate = performanceCounterManager.GetCounter(KinoPerformanceCounters.ActorHostRequestSocketReceiveRate);
        //    socket.SendRate = performanceCounterManager.GetCounter(KinoPerformanceCounters.ActorHostRequestSocketSendRate);
        //    SocketHelper.SafeConnect(() => socket.Connect(routerConfiguration.RouterAddress.Uri));

        //    return socket;
        //}

        private void SafeExecute(Action wrappedMethod)
        {
            try
            {
                wrappedMethod();
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