using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace AgentScope.Application.Runs;

/// <summary>
/// Use case: start an agent run and return a stream of events for the caller to consume.
/// Owns the run lifecycle: assigns RunId, kicks off the agent on a background task,
/// surfaces unexpected errors as system-level <see cref="AgentErrorEvent"/>s.
/// </summary>
public sealed class StartRunUseCase
{
    private readonly IResearchAgent _agent;
    private readonly IAgentEventBus _bus;
    private readonly ILogger<StartRunUseCase> _logger;

    public StartRunUseCase(
        IResearchAgent agent,
        IAgentEventBus bus,
        ILogger<StartRunUseCase> logger)
    {
        _agent = agent;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// Starts a run for <paramref name="question"/> and returns the event stream.
    /// The caller is responsible for consuming the stream until it completes
    /// (or cancelling via <paramref name="ct"/>).
    /// </summary>
    public (RunId RunId, IAsyncEnumerable<AgentEvent> Events) Start(
        string question, CancellationToken ct = default)
    {
        var runId = RunId.New();
        var request = new AgentRunRequest(runId, question);

        // Subscribe BEFORE starting the agent so we don't miss the first events.
        // The bus implementation must register the subscription synchronously.
        var stream = _bus.SubscribeAsync(runId, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await _agent.RunAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — no error event needed.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent run {RunId} failed unexpectedly", runId);
                await _bus.PublishAsync(new AgentErrorEvent(
                    runId, AgentId.System, ex.Message, DateTime.UtcNow), CancellationToken.None);
            }
        }, ct);

        return (runId, stream);
    }
}
