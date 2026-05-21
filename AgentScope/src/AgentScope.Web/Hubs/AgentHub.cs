using AgentScope.Application.Runs;
using AgentScope.Domain.Events;
using Microsoft.AspNetCore.SignalR;

namespace AgentScope.Web.Hubs;

/// <summary>
/// Thin SignalR adapter. Knows nothing about Semantic Kernel or agents — just the
/// <see cref="StartRunUseCase"/> and the event stream it returns.
/// </summary>
public sealed class AgentHub : Hub
{
    private readonly StartRunUseCase _startRun;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(StartRunUseCase startRun, ILogger<AgentHub> logger)
    {
        _startRun = startRun;
        _logger = logger;
    }

    public async Task Ask(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        var (runId, events) = _startRun.Start(question, Context.ConnectionAborted);

        try
        {
            await foreach (var evt in events.WithCancellation(Context.ConnectionAborted))
            {
                // SignalR serializes the event record to JSON; the client routes by `kind`.
                await Clients.Caller.SendAsync("event", evt.Kind, evt, Context.ConnectionAborted);
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Client disconnected during run {RunId}", runId);
        }
    }
}
