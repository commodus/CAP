﻿// Copyright (c) .NET Core Community. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Transport;
using Microsoft.Extensions.Options;
using NATS.Client;
using NATS.Client.JetStream;

namespace DotNetCore.CAP.NATS
{
    internal sealed class NATSConsumerClient : IConsumerClient
    {
        private static readonly SemaphoreSlim ConnectionLock = new SemaphoreSlim(initialCount: 1, maxCount: 1);

        private readonly string _groupId;
        private readonly NATSOptions _natsOptions;

        private IConnection _consumerClient;

        public NATSConsumerClient(string groupId, IOptions<NATSOptions> options)
        {
            _groupId = groupId;
            _natsOptions = options.Value ?? throw new ArgumentNullException(nameof(options));
        }

        public event EventHandler<TransportMessage> OnMessageReceived;

        public event EventHandler<LogMessageEventArgs> OnLog;

        public BrokerAddress BrokerAddress => new BrokerAddress("NATS", _natsOptions.Servers);

        public ICollection<string> FetchTopics(IEnumerable<string> topicNames)
        {
            var jsm = _consumerClient.CreateJetStreamManagementContext();

            foreach (var topic in topicNames)
            {
                var norTopic = Helper.Normalized(topic);
                try
                {
                    jsm.GetStreamInfo(norTopic); // this throws if the stream does not exist
                }
                catch (NATSJetStreamException)
                {
                    var builder = StreamConfiguration.Builder()
                        .WithName(norTopic)
                        .WithNoAck(false)
                        .WithStorageType(StorageType.Memory)
                        .WithSubjects(topic);

                    _natsOptions.StreamOptions?.Invoke(builder);

                    try
                    {
                        jsm.AddStream(builder.Build());
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            return topicNames.ToList();
        }

        public void Subscribe(IEnumerable<string> topics)
        {
            if (topics == null)
            {
                throw new ArgumentNullException(nameof(topics));
            }

            Connect();

            var js = _consumerClient.CreateJetStreamContext();

            foreach (var topic in topics)
            {
                var pso = PushSubscribeOptions.Builder()
                    .WithStream(Helper.Normalized(topic))
                    .WithConfiguration(ConsumerConfiguration.Builder().WithDeliverPolicy(DeliverPolicy.New).Build())
                    .WithDeliverGroup(_groupId)
                    .Build();

                js.PushSubscribeAsync(topic, Subscription_MessageHandler, false, pso);
            }
        }

        public void Listening(TimeSpan timeout, CancellationToken cancellationToken)
        {
            //Connect();

            //foreach (var subscription in _asyncSubscriptions)
            //{
            //    subscription.MessageHandler += Subscription_MessageHandler;
            //    subscription.Start();
            //}

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cancellationToken.WaitHandle.WaitOne(timeout);
            }
            // ReSharper disable once FunctionNeverReturns
        }
         
        private void Subscription_MessageHandler(object sender, MsgHandlerEventArgs e)
        {
            var headers = new Dictionary<string, string>();

            foreach (string h in e.Message.Header.Keys)
            {
                headers.Add(h, e.Message.Header[h]);
            }

            headers.Add(Headers.Group, _groupId);

            OnMessageReceived?.Invoke(e.Message, new TransportMessage(headers, e.Message.Data));
        }

        public void Commit(object sender)
        {
            if (sender is Msg msg)
            {
                msg.Ack();
            }
        }

        public void Reject(object sender)
        {
            if (sender is Msg msg)
            {
                msg.Nak();
            }
        }

        public void Dispose()
        {
            _consumerClient?.Dispose();
        }

        public void Connect()
        {
            if (_consumerClient != null)
            {
                return;
            }

            ConnectionLock.Wait();

            try
            {
                if (_consumerClient == null)
                {
                    var opts = _natsOptions.Options ?? ConnectionFactory.GetDefaultOptions();
                    opts.Url = _natsOptions.Servers ?? opts.Url;
                    opts.ClosedEventHandler = ConnectedEventHandler;
                    opts.DisconnectedEventHandler = ConnectedEventHandler;
                    opts.AsyncErrorEventHandler = AsyncErrorEventHandler;
                    opts.Timeout = 5000;
                    _consumerClient = new ConnectionFactory().CreateConnection(opts);
                }
            }
            finally
            {
                ConnectionLock.Release();
            }
        }

        private void ConnectedEventHandler(object sender, ConnEventArgs e)
        {
            var logArgs = new LogMessageEventArgs
            {
                LogType = MqLogType.ConnectError,
                Reason = e.Error?.ToString()
            };
            OnLog?.Invoke(null, logArgs);
        }

        private void AsyncErrorEventHandler(object sender, ErrEventArgs e)
        {
            var logArgs = new LogMessageEventArgs
            {
                LogType = MqLogType.AsyncErrorEvent,
                Reason = e.Error
            };
            OnLog?.Invoke(null, logArgs);
        }
    }
}