using System.Text;
using Microsoft.Extensions.Logging;
using Muflone.Messages;
using Muflone.Messages.Events;
using Muflone.Persistence;
using Muflone.Saga;
using Muflone.Transport.RabbitMQ.Abstracts;
using Muflone.Transport.RabbitMQ.Models;
using Muflone.Transport.RabbitMQ.Saga.Abstracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Muflone.Transport.RabbitMQ.Saga.Consumers;

public abstract class SagaEventConsumerBase<T> : ConsumerBase, ISagaEventConsumer<T>, IAsyncDisposable
	where T : Event
{
	private readonly ISerializer _messageSerializer;
	private readonly ConsumerConfiguration _configuration;
	private readonly IMufloneConnectionFactory _connectionFactory;
	private IModel _channel;
	protected abstract ISagaEventHandlerAsync<T> HandlerAsync { get; }

	protected SagaEventConsumerBase(IMufloneConnectionFactory mufloneConnectionFactory, ILoggerFactory loggerFactory)
		: this(new ConsumerConfiguration(), mufloneConnectionFactory, loggerFactory)
	{
	}

	protected SagaEventConsumerBase(ConsumerConfiguration configuration, IMufloneConnectionFactory connectionFactory, ILoggerFactory loggerFactory)
		: base(loggerFactory)
	{
		_connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
		_messageSerializer = new Serializer();

		if (string.IsNullOrWhiteSpace(configuration.ResourceKey))
			configuration.ResourceKey = typeof(T).Name;
		if (string.IsNullOrWhiteSpace(configuration.QueueName))
		{
			configuration.QueueName = GetType().Name;
			if (configuration.QueueName.EndsWith("Consumer", StringComparison.InvariantCultureIgnoreCase))
				configuration.QueueName = configuration.QueueName.Substring(0, configuration.QueueName.Length - "Consumer".Length);
		}
		_configuration = configuration;
	}

	public async Task ConsumeAsync(T message, CancellationToken cancellationToken = default)
	{
		await HandlerAsync.HandleAsync(message);
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		InitChannel();
		InitSubscription();

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken = default)
	{
		StopChannel();
		return Task.CompletedTask;
	}

	private void InitChannel()
	{
		Logger.LogInformation($"initializing retry queue '{_configuration.QueueName}' on exchange '{_connectionFactory.ExchangeEventsName}'...");
		StopChannel();
		_channel = _connectionFactory.CreateChannel();
		_channel.ExchangeDeclare(_connectionFactory.ExchangeEventsName, ExchangeType.Topic);
		_channel.QueueDeclare(_configuration.QueueName, true, false, false);
		_channel.QueueBind(_configuration.QueueName, _connectionFactory.ExchangeEventsName, _configuration.ResourceKey, null);
		_channel.CallbackException += OnChannelException;
	}

	private void StopChannel()
	{
		if (_channel is null)
			return;

		_channel.CallbackException -= OnChannelException;

		if (_channel.IsOpen)
			_channel.Close();

		_channel.Dispose();
		_channel = null;
	}

	private void OnChannelException(object _, CallbackExceptionEventArgs ea)
	{
		Logger.LogError(ea.Exception, $"The RabbitMQ Channel has encountered an error: {ea.Exception.Message}");

		InitChannel();
		InitSubscription();
	}

	private void InitSubscription()
	{
		var consumer = new AsyncEventingBasicConsumer(_channel);

		consumer.Received += OnMessageReceivedAsync;

		Logger.LogInformation($"Initializing subscription on queue '{_configuration.QueueName}' with ResourceKey '{_configuration.ResourceKey}' ...");
		_channel.BasicConsume(_configuration.QueueName, false, consumer);
	}

	private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
	{
		var consumer = sender as IBasicConsumer;
		var channel = consumer?.Model ?? _channel;

		Event message;
		try
		{
			message = await _messageSerializer.DeserializeAsync<T>(Encoding.ASCII.GetString(eventArgs.Body.ToArray()), CancellationToken.None);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex,
				"an exception has occured while decoding queue message from Exchange '{ExchangeName}', message cannot be parsed. Error: {ExceptionMessage}",
				eventArgs.Exchange, ex.Message);
			channel.BasicReject(eventArgs.DeliveryTag, false);
			return;
		}

		Logger.LogInformation($"Received message '{message.MessageId}' from Exchange '{_connectionFactory.ExchangeEventsName}', Queue '{_configuration.QueueName}'. Processing...");

		try
		{
			//TODO: provide valid cancellation token
			await ConsumeAsync((dynamic)message, CancellationToken.None);

			channel.BasicAck(eventArgs.DeliveryTag, false);
		}
		catch (Exception ex)
		{
			HandleConsumerException(ex, eventArgs, channel, message, false);
		}
	}

	private void HandleConsumerException(Exception ex, BasicDeliverEventArgs deliveryProps, IModel channel, IMessage message, bool requeue)
	{
		Logger.LogWarning(ex, $"An error has occurred while processing Message '{message.MessageId}' from Exchange '{deliveryProps.Exchange}' : {ex.Message}. {(requeue ? "Reenqueuing..." : "Nacking...")}");

		if (!requeue)
		{
			channel.BasicReject(deliveryProps.DeliveryTag, false);
		}
		else
		{
			channel.BasicAck(deliveryProps.DeliveryTag, false);
			channel.BasicPublish(_configuration.QueueName, deliveryProps.RoutingKey, deliveryProps.BasicProperties, deliveryProps.Body);
		}
	}

	#region Dispose

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}

	#endregion
}