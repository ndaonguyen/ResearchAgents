using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;

namespace AgentScope.Infrastructure.EventBus;

/// <summary>
/// In-process event bus with one <see cref="Channel{T}"/> per run.
/// Each run's stream completes when an <see cref="AgentFinishedEvent"/> or a system-level
/// <see cref="AgentErrorEvent"/> arrives, or when the subscriber cancels.
///
/// This fixes the v1 design where a single shared channel meant concurrent runs would
/// steal each other's events.
/// </summary>
public sealed class ChannelAgentEventBus : IAgentEventBus
{
    private readonly ConcurrentDictionary<string, Channel<AgentEvent>> _channels = new();

    public ValueTask PublishAsync(AgentEvent evt, CancellationToken ct = default)
    {
        var channel = _channels.GetOrAdd(evt.RunId.Value, _ => CreateChannel());
        return channel.Writer.WriteAsync(evt, ct);
    }

    public async IAsyncEnumerable<AgentEvent> SubscribeAsync(
        RunId runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // GetOrAdd ensures publishers and subscribers race-safely converge on the same channel,
        // regardless of which side arrives first.
        var channel = _channels.GetOrAdd(runId.Value, _ => CreateChannel());

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;

                if (IsTerminal(evt))
                    yield break;
            }
        }
        finally
        {
            // Remove the channel on completion so the dictionary doesn't grow unbounded.
            // Any late events for this run will recreate the channel but never have a reader —
            // bounded channels would drop them gracefully if we cared.
            _channels.TryRemove(runId.Value, out _);
        }
    }

    private static bool IsTerminal(AgentEvent evt) =>
        (evt is AgentFinishedEvent finished && finished.AgentId == AgentId.System) ||
        (evt is AgentErrorEvent err && err.AgentId == AgentId.System);

    private static Channel<AgentEvent> CreateChannel() =>
        Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,    // Exactly one subscriber per run.
            SingleWriter = false,   // Multiple agents publish to the same run.
            AllowSynchronousContinuations = false
        });
}
