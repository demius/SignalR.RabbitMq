﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Messaging;
using Microsoft.AspNet.SignalR.Tracing;

namespace SignalR.RabbitMQ
{
    internal class RabbitMqMessageBus : ScaleoutMessageBus
    {
        private readonly RabbitConnectionBase _rabbitConnectionBase;
        private readonly RabbitMqScaleoutConfiguration _configuration;
        private Task _sendingworkerTask;
        private Task _recievingworkerTask;
        private static readonly BlockingCollection<RabbitMqMessageWrapper> _sendingbuffer
                = new BlockingCollection<RabbitMqMessageWrapper>(new ConcurrentQueue<RabbitMqMessageWrapper>());
        private static readonly BlockingCollection<RabbitMqMessageWrapper> _recievingbuffer
                = new BlockingCollection<RabbitMqMessageWrapper>(new ConcurrentQueue<RabbitMqMessageWrapper>());

        private readonly TraceSource _trace;
        private int _resource = 0;

        public RabbitMqMessageBus(  IDependencyResolver resolver, 
                                    RabbitMqScaleoutConfiguration configuration, 
                                    RabbitConnectionBase advancedConnectionInstance = null)
            : base(resolver, configuration)
        {

            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }
            _configuration = configuration;

            var traceManager = resolver.Resolve<ITraceManager>();
            _trace = traceManager["SignalR.RabbitMQ." + typeof(RabbitMqMessageBus).Name];

            if (advancedConnectionInstance != null)
            {
                advancedConnectionInstance.OnDisconnectionAction = OnConnectionLost;
                advancedConnectionInstance.OnReconnectionAction = ConnectToRabbit;
                advancedConnectionInstance.OnMessageRecieved =
                    wrapper => _recievingbuffer.Add(wrapper);

                _rabbitConnectionBase = advancedConnectionInstance;
            }
            else
            {
                _rabbitConnectionBase = new EasyNetQRabbitConnection(_configuration)
                                            {
                                                OnDisconnectionAction = OnConnectionLost,
                                                OnReconnectionAction = ConnectToRabbit,
                                                OnMessageRecieved = wrapper => _recievingbuffer.Add(wrapper)
                                            };
            }

            ConnectToRabbit();

            _recievingworkerTask = Task.Factory.StartNew(()=>
            {
                while (true)
                {
                    foreach (var message in _recievingbuffer.GetConsumingEnumerable())
                    {
                        try
                        {
                            OnReceived(0, message.Id, message.ScaleoutMessage);
                        }
                        catch
                        {
                            OnConnectionLost();
                        }
                    }
                }
            });
        }

		protected override void Dispose(bool disposing)
		{
			if (disposing && _rabbitConnectionBase != null)
			{
				_rabbitConnectionBase.Dispose();
			}

			base.Dispose(disposing);
		}

        protected void OnConnectionLost()
        {
            Interlocked.Exchange(ref _resource, 0);
            OnError(0, new RabbitMessageBusException("Connection to Rabbit lost."));
        }

        protected void ConnectToRabbit()
        {
            if (1 == Interlocked.Exchange(ref _resource, 1))
            {
                return;
            }
            _rabbitConnectionBase.StartListening();
            Open(0);

            _sendingworkerTask = Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    foreach (var message in _sendingbuffer.GetConsumingEnumerable())
                    {
                        try
                        {
                            _rabbitConnectionBase.Send(message);
                        }
                        catch
                        {
                            OnConnectionLost();
                        }
                    }
                }
            });

        }
        
        protected override Task Send(IList<Message> messages)
        {
            _sendingbuffer.Add(new RabbitMqMessageWrapper(messages));
            var tcs = new TaskCompletionSource<object>();
            return tcs.Task;
        }
    }
}