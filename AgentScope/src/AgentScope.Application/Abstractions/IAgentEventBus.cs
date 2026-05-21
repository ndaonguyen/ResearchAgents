using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;

namespace AgentScope.Application.Abstractions;

/// <summary>
/// Port: in-process pub/sub for agent events.
/// Application code publishes here; infrastructure provides the implementation
/// (channel-backed in v1; could be Redis/RabbitMQ later).
/// </summary>
public interface IAgentEventBus
{
    ValueTask PublishAsync(AgentEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Subscribes to events for a single run. The returned stream completes when
    /// the run emits <see cref="AgentFinishedEvent"/> or an <see cref="AgentErrorEvent"/>
    /// for the system agent, or when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<AgentEvent> SubscribeAsync(RunId runId, CancellationToken ct = default);
}
