using System.Diagnostics;
using AgentScope.Application.Runs;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentScope.Web.Hubs;

/// <summary>
/// Thin SignalR adapter. Knows nothing about Semantic Kernel or agents — just the
/// <see cref="StartRunUseCase"/> and the event stream it returns.
///
/// Side-effect: persists a JSONL row per completed run via <see cref="RunPersister"/>
/// so the Past Runs viewer can show UI-initiated runs alongside eval CLI runs.
/// </summary>
public sealed class AgentHub : Hub
{
    private readonly StartRunUseCase _startRun;
    private readonly RunPersister _persister;
    private readonly EvalQueue _evalQueue;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        StartRunUseCase startRun,
        RunPersister persister,
        EvalQueue evalQueue,
        ILogger<AgentHub> logger)
    {
        _startRun = startRun;
        _persister = persister;
        _evalQueue = evalQueue;
        _logger = logger;
    }

    // Eval-related hub methods. Kept on the same hub as Ask because the Razor pages
    // already maintain one connection per circuit; a second hub would mean a second
    // connection for no gain.

    public Task SubscribeToEval(string jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"eval-{jobId}");

    public Task UnsubscribeFromEval(string jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"eval-{jobId}");

    public Task SubscribeToAllEvals() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "eval-all");

    public Task UnsubscribeFromAllEvals() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, "eval-all");

    public bool CancelEval(string jobId) => _evalQueue.TryCancel(jobId);

    public async Task Ask(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return;

        var (runId, events) = _startRun.Start(question, ct: Context.ConnectionAborted);

        // Capture state for the JSONL row as we forward events to the client.
        string? finalAnswer = null;
        var tokensIn = 0;
        var tokensOut = 0;
        decimal? costUsd = null;
        var errored = false;
        string? errorMessage = null;
        var clientDisconnected = false;
        var sw = Stopwatch.StartNew();

        try
        {
            await foreach (var evt in events.WithCancellation(Context.ConnectionAborted))
            {
                // SignalR serializes the event record to JSON; the client routes by `kind`.
                await Clients.Caller.SendAsync("event", evt.Kind, evt, Context.ConnectionAborted);

                switch (evt)
                {
                    case AgentFinishedEvent f when f.AgentId == AgentId.System:
                        finalAnswer = f.FinalText;
                        tokensIn = f.TokensIn;
                        tokensOut = f.TokensOut;
                        costUsd = f.EstimatedCostUsd;
                        break;
                    case AgentErrorEvent err when err.AgentId == AgentId.System:
                        errored = true;
                        errorMessage = err.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Client disconnected during run {RunId}", runId);
            clientDisconnected = true;
        }

        sw.Stop();

        // Persistence is best-effort. Use a fresh token so a disconnect doesn't prevent
        // us from recording what we did capture. RunPersister itself swallows errors.
        var row = new EvalRow(
            QuestionId: runId.Value,
            Variant: "ui",
            Question: question,
            RunId: runId.Value,
            Answer: finalAnswer ?? "",
            TokensIn: tokensIn,
            TokensOut: tokensOut,
            CostUsd: costUsd,
            DurationMs: sw.ElapsedMilliseconds,
            Errored: errored || clientDisconnected,
            ErrorMessage: errored ? errorMessage : (clientDisconnected ? "client disconnected before run completed" : null),
            JudgeScore: null,
            JudgeReasoning: null,
            JudgeTokensIn: 0,
            JudgeTokensOut: 0,
            JudgeCostUsd: null,
            CompletedAt: DateTime.UtcNow);

        await _persister.PersistAsync(row, CancellationToken.None);
    }
}
