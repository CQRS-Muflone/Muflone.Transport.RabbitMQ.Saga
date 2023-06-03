using Muflone.Messages.Commands;
using Muflone.Transport.RabbitMQ.Abstracts;

namespace Muflone.Transport.RabbitMQ.Saga.Abstracts;

public interface ISagaStartedByConsumer<in T> : IConsumer where T : Command
{
    Task ConsumeAsync(T message, CancellationToken cancellationToken = default);
}