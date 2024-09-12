using Microsoft.Extensions.Logging;
using Muflone.Messages;
using Muflone.Messages.Commands;
using Muflone.Persistence;
using Muflone.Saga;
using Muflone.Transport.RabbitMQ.Abstracts;
using Muflone.Transport.RabbitMQ.Consumers;
using Muflone.Transport.RabbitMQ.Models;
using Muflone.Transport.RabbitMQ.Saga.Abstracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;

namespace Muflone.Transport.RabbitMQ.Saga.Consumers;

public abstract class SagaStartedByConsumerBase<T> : ConsumerBase, ISagaStartedByConsumer<T>, IAsyncDisposable
	where T : Command
{
	private readonly ISerializer _messageSerializer;
	private readonly ConsumerConfiguration _configuration;
	private readonly IRabbitMQConnectionFactory _connectionFactory;
	private IModel _channel = default!;
	protected abstract ISagaStartedByAsync<T> HandlerAsync { get; }

	/// <summary>
	/// For now just as a proxy to pass directly to the Handler this class is wrapping
	/// </summary>
	protected IRepository Repository { get; } = default!;

	protected SagaStartedByConsumerBase(IRepository repository, IRabbitMQConnectionFactory connectionFactory,
		ILoggerFactory loggerFactory)
		: this(new ConsumerConfiguration(), repository, connectionFactory, loggerFactory)
	{
	}

	protected SagaStartedByConsumerBase(ConsumerConfiguration configuration, IRepository repository,
		IRabbitMQConnectionFactory connectionFactory, ILoggerFactory loggerFactory)
		: base(loggerFactory)
	{
		Repository = repository ?? throw new ArgumentNullException(nameof(repository));
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

	protected SagaStartedByConsumerBase(ConsumerConfiguration configuration,
		IRabbitMQConnectionFactory connectionFactory, ILoggerFactory loggerFactory)
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
		await HandlerAsync.StartedByAsync(message);
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
		Logger.LogInformation($"initializing retry queue '{_configuration.QueueName}' on exchange '{_connectionFactory.ExchangeCommandsName}'...");

		StopChannel();

		_channel = _connectionFactory.CreateChannel();
		_channel.ExchangeDeclare(_connectionFactory.ExchangeCommandsName, ExchangeType.Direct);
		_channel.QueueDeclare(_configuration.QueueName, true, false, false);
		_channel.QueueBind(_configuration.QueueName, _connectionFactory.ExchangeCommandsName, _configuration.ResourceKey, null);
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
	}

	private void OnChannelException(object? sender, CallbackExceptionEventArgs ea)
	{
		Logger.LogError(ea.Exception, $"RabbitMQ Channel has encountered an error: {ea.Exception.Message}");

		InitChannel();
		InitSubscription();
	}

	private void InitSubscription()
	{
		Logger.LogInformation($"Initializing subscription on queue '{_configuration.QueueName}' ...");
		var consumer = new AsyncEventingBasicConsumer(_channel);
		consumer.Received += OnMessageReceivedAsync;
		_channel.BasicConsume(_configuration.QueueName, false, consumer);
	}

	private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
	{
		var consumer = sender as IBasicConsumer;
		var channel = consumer?.Model ?? _channel;

		Command command;
		try
		{
			var deserializedMessage = await _messageSerializer.DeserializeAsync<T>(Encoding.ASCII.GetString(eventArgs.Body.ToArray()), CancellationToken.None) ?? throw new InvalidOperationException("Deserialized message cannot be null.");
			command = deserializedMessage;
		}
		catch (Exception ex)
		{
			Logger.LogError(ex,
				"an exception has occured while decoding queue message from Exchange '{ExchangeName}', message cannot be parsed. Error: {ExceptionMessage}",
				eventArgs.Exchange, ex.Message);
			channel.BasicReject(eventArgs.DeliveryTag, false);
			return;
		}

		Logger.LogInformation($"Received message '{command.MessageId}' from Exchange '{_connectionFactory.ExchangeCommandsName}', Queue '{_configuration.QueueName}'. Processing...");

		try
		{
			//TODO: provide valid cancellation token
			await ConsumeAsync((dynamic)command, CancellationToken.None);

			channel.BasicAck(eventArgs.DeliveryTag, false);
		}
		catch (Exception ex)
		{
			HandleConsumerException(ex, eventArgs, channel, command, false);
		}
	}

	private void HandleConsumerException(Exception ex, BasicDeliverEventArgs deliveryProps, IModel channel, IMessage message, bool requeue)
	{
		var errorMsg = $"An error has occurred while processing Message '{message.MessageId}' from Exchange '{deliveryProps.Exchange}' : {ex.Message} . {(requeue ? "Reenqueuing..." : "Nacking...")}";
		Logger.LogWarning(ex, errorMsg);

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

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}