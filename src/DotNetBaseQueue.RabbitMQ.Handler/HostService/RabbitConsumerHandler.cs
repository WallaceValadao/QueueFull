﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetBaseQueue.Interfaces.Configs;
using DotNetBaseQueue.Interfaces.Event;
using DotNetBaseQueue.RabbitMQ.Handler.Consumir;
using DotNetBaseQueue.RabbitMQ.Core.Exceptions;
using DotNetBaseQueue.RabbitMQ.Interfaces;
using DotNetBaseQueue.RabbitMQ.HostService;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using DotNetBaseQueue.RabbitMQ.Core;

namespace DotNetBaseQueue.QueueMQ.HostService
{
    public class RabbitConsumerHandler<IEntidade, IEvent> : 
        IConsumerHandler where IEntidade : class, IQueueEvent 
        where IEvent : class, IQueueEventHandler<IEntidade>
    {
        private readonly ILogger<RabbitConsumerHandler<IEntidade, IEvent>> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly QueueConfiguration _queueConfiguration;

        private readonly string _mensagemAguardando;
        private TimeSpan _secondsToRetry;

        public RabbitConsumerHandler(ILogger<RabbitConsumerHandler<IEntidade, IEvent>> logger, IServiceProvider serviceProvider, ConsumerConfiguration<IEntidade, IEvent> queueMQConfiguration)
        {
            _logger = logger;
            _queueConfiguration = queueMQConfiguration.QueueConfiguration;
            _serviceProvider = serviceProvider;
            _mensagemAguardando = $"({_queueConfiguration.QueueName}) Waiting for messages...";
            _secondsToRetry = TimeSpan.FromSeconds(_queueConfiguration.SecondsToRetry);
        }

        public IEnumerable<Task> CreateTask(CancellationToken stoppingToken)
        {
            foreach (var i in Enumerable.Range(0, _queueConfiguration.NumberOfWorkroles))
                yield return ProcessarAsync(stoppingToken);
        }

        private async Task ProcessarAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await QueueConsumerAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating connection.");
                    await Task.Delay(_secondsToRetry, stoppingToken);
                }
            }
        }

        private async Task QueueConsumerAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Opening channel and connection with {_queueConfiguration.HostName}.");
            var channel = ConnectionHandler.CreateConnection(_queueConfiguration, _logger);
            channel.BasicQos(prefetchSize: 0, prefetchCount: _queueConfiguration.PrefetchCount, global: false);
            _logger.LogInformation("Connection started successfully.");

            _logger.LogInformation("Starting reading messages.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var scope = _serviceProvider.CreateScope();
                var commandHandler = scope.ServiceProvider.GetService<IEvent>();

                var consumer = new EventingBasicConsumer(channel);

                consumer.Received += (sender, ea) => ProcessMessageAsync(commandHandler, channel, ea).GetAwaiter().GetResult();

                _logger.LogInformation("Starting consumer.");
                var tagConsummer = channel.BasicConsume(_queueConfiguration.QueueName, false, $"{Environment.MachineName}-{Guid.NewGuid()}", consumer: consumer);

                do
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                while (consumer.IsRunning);

                channel.BasicCancel(tagConsummer);

                _logger.LogInformation("Consumer stopped working.");
            }
        }

        private async Task ProcessMessageAsync(IQueueEventHandler<IEntidade> commandHandler, IModel channel, BasicDeliverEventArgs basicGetResult)
        {
            _logger.LogInformation("Message received! starting processing.");

            try
            {
                var entidade = basicGetResult.GetEntityQueue<IEntidade>(channel);

                await commandHandler.HandleAsync(entidade);

                channel.BasicAck(deliveryTag: basicGetResult.DeliveryTag, multiple: false);

                _logger.LogInformation("Message processed successfully.");
            }
            catch (RetryException ex)
            {
                _logger.LogError(ex, "Retry message error");
                channel.BasicPublish(exchange: _queueConfiguration.ExchangeName,
                                    routingKey: $"{_queueConfiguration.QueueName}{QueueConstraints.PATH_RETRY}",
                                    basicProperties: basicGetResult.BasicProperties,
                                    body: basicGetResult.Body);
                channel.BasicAck(basicGetResult.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error message");
                channel.BasicNack(basicGetResult.DeliveryTag, false, false);
            }
        }
    }
}
