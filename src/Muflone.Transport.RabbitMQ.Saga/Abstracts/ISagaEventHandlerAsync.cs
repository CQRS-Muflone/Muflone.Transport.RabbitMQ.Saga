using Muflone.Messages.Events;
using Muflone.Transport.RabbitMQ.Abstracts;

namespace Muflone.Transport.RabbitMQ.Saga.Abstracts;

public interface ISagaEventConsumer<in T> : IConsumer where T : Event
{
    Task ConsumeAsync(T message, CancellationToken cancellationToken = default);
}