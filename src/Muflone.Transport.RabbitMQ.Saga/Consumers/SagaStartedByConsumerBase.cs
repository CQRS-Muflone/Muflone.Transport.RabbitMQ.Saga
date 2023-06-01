using System.Text;
using Microsoft.Extensions.Logging;
using Muflone.Messages;
using Muflone.Messages.Commands;
using Muflone.Persistence;
using Muflone.Saga;
using Muflone.Transport.RabbitMQ.Abstracts;
using Muflone.Transport.RabbitMQ.Models;
using Muflone.Transport.RabbitMQ.Saga.Abstracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Muflone.Transport.RabbitMQ.Saga.Consumers;

public abstract class SagaStartedByConsumerBase<T> : ConsumerBase, IAsyncDisposable, ISagaStartedByConsumer<T>
	where T : Command
{
	private readonly ISerializer _messageSerializer;
	private readonly IMufloneConnectionFactory _mufloneConnectionFactory;
	private IModel _channel;
	private readonly RabbitMQReference _rabbitMQReference;

	protected abstract ISagaStartedByAsync<T> HandlerAsync { get; }

	public string TopicName { get; }

	/// <summary>
	/// For now just as a proxy to pass directly to the Handler this class is wrapping
	/// </summary>
	protected IRepository Repository { get; }

	protected SagaStartedByConsumerBase(IRepository repository, IMufloneConnectionFactory mufloneConnectionFactory,
		RabbitMQReference rabbitMQReference, ILoggerFactory loggerFactory) : base(loggerFactory)
	{
		Repository = repository ?? throw new ArgumentNullException(nameof(repository));
		_rabbitMQReference = rabbitMQReference ?? throw new ArgumentNullException(nameof(rabbitMQReference));

		_mufloneConnectionFactory =
			mufloneConnectionFactory ?? throw new ArgumentNullException(nameof(mufloneConnectionFactory));

		_messageSerializer = new Serializer();

		TopicName = typeof(T).Name;
	}

	public async Task ConsumeAsync(T message, CancellationToken cancellationToken = default)
	{
		await HandlerAsync.StartedByAsync(message /*, cancellationToken*/);
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
		StopChannel();

		_channel = _mufloneConnectionFactory.CreateChannel();

		Logger.LogInformation(
			$"initializing retry queue '{TopicName}' on exchange '{_rabbitMQReference.ExchangeCommandsName}'...");

		_channel.ExchangeDeclare(_rabbitMQReference.ExchangeCommandsName, ExchangeType.Direct);
		_channel.QueueDeclare(TopicName,
			true,
			false,
			false);
		_channel.QueueBind(typeof(T).Name,
			_rabbitMQReference.ExchangeCommandsName,
			TopicName,
			null);

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
		Logger.LogError(ea.Exception, "the RabbitMQ Channel has encountered an error: {ExceptionMessage}",
			ea.Exception.Message);

		InitChannel();
		InitSubscription();
	}

	private void InitSubscription()
	{
		var consumer = new AsyncEventingBasicConsumer(_channel);

		consumer.Received += OnMessageReceivedAsync;

		Logger.LogInformation($"initializing subscription on queue '{TopicName}' ...");
		_channel.BasicConsume(TopicName, false, consumer);
	}

	private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs eventArgs)
	{
		var consumer = sender as IBasicConsumer;
		var channel = consumer?.Model ?? _channel;

		ICommand command;
		try
		{
			command = await _messageSerializer.DeserializeAsync<T>(Encoding.ASCII.GetString(eventArgs.Body.ToArray()),
				CancellationToken.None);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex,
				"an exception has occured while decoding queue message from Exchange '{ExchangeName}', message cannot be parsed. Error: {ExceptionMessage}",
				eventArgs.Exchange, ex.Message);
			channel.BasicReject(eventArgs.DeliveryTag, false);
			return;
		}

		Logger.LogInformation(
			"received message '{MessageId}' from Exchange '{ExchangeName}', Queue '{QueueName}'. Processing...",
			command.MessageId, TopicName, TopicName);

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

	private void HandleConsumerException(Exception ex, BasicDeliverEventArgs deliveryProps, IModel channel,
		IMessage message, bool requeue)
	{
		var errorMsg =
			"an error has occurred while processing Message '{MessageId}' from Exchange '{ExchangeName}' : {ExceptionMessage} . "
			+ (requeue ? "Reenqueuing..." : "Nacking...");

		Logger.LogWarning(ex, errorMsg, message.MessageId, TopicName, ex.Message);

		if (!requeue)
		{
			channel.BasicReject(deliveryProps.DeliveryTag, false);
		}
		else
		{
			channel.BasicAck(deliveryProps.DeliveryTag, false);
			channel.BasicPublish(
				TopicName,
				deliveryProps.RoutingKey,
				deliveryProps.BasicProperties,
				deliveryProps.Body);
		}
	}

	public ValueTask DisposeAsync()
	{
		return ValueTask.CompletedTask;
	}
}