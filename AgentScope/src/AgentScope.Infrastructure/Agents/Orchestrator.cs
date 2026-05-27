using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using Microsoft.Extensions.Logging;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Multi-agent orchestrator: planner → researchers (parallel) → critic → synthesizer.
/// Publishes a final system-level <see cref="AgentFinishedEvent"/> that terminates
/// the run's event stream.
/// </summary>
public sealed class Orchestrator : IOrchestrator
{
    private readonly IKernelFactory _kernelFactory;
    private readonly IPlannerAgent _planner;
    private readonly IResearcherAgent _researcher;
    private readonly ICriticAgent _critic;
    private readonly ISynthesizerAgent _synthesizer;
    private readonly IAgentEventBus _bus;
    private readonly ILogger<Orchestrator> _logger;

    public Orchestrator(
        IKernelFactory kernelFactory,
        IPlannerAgent planner,
        IResearcherAgent researcher,
        ICriticAgent critic,
        ISynthesizerAgent synthesizer,
        IAgentEventBus bus,
        ILogger<Orchestrator> logger)
    {
        _kernelFactory = kernelFactory;
        _planner = planner;
        _researcher = researcher;
        _critic = critic;
        _synthesizer = synthesizer;
        _bus = bus;
        _logger = logger;
    }

    public async Task RunAsync(AgentRunRequest request, OrchestratorConfig config, CancellationToken ct = default)
    {
        try
        {
            // One kernel per role so per-role model overrides take effect at the SK
            // ChatCompletion service. The plugins (Tavily, BookLookup) are only meaningful
            // for the researcher, but keeping them on every kernel keeps the code uniform
            // and the cost is just plugin registration, not extra HTTP calls.
            var plannerKernel     = _kernelFactory.Create(config.PlannerModel);
            var researcherKernel  = _kernelFactory.Create(config.ResearcherModel);
            var criticKernel      = _kernelFactory.Create(config.CriticModel);
            var synthKernel       = _kernelFactory.Create(config.SynthesizerModel);

            var totalUsage = AgentUsage.Empty;

            // 1. PLAN
            var (subQuestions, plannerUsage) = await _planner.PlanAsync(request.Question, plannerKernel, request.RunId, ct);
            totalUsage = totalUsage.Add(plannerUsage);

            // 2. RESEARCH — parallel fanout, capped at MaxResearcherConcurrency.
            // Without the cap, a planner returning 5 sub-questions launches 5 researchers
            // simultaneously, and each researcher makes 2-4 LLM calls (tool selection +
            // summary). That's 10-20 concurrent OpenAI requests on a single question —
            // enough to trip Tier-1 TPM limits on gpt-4o (30k/min). The cap trades a
            // little wall-clock for not blowing through the rate limit; cancellation
            // and per-research errors flow through normally.
            using var researcherGate = new SemaphoreSlim(Math.Max(1, config.MaxResearcherConcurrency));
            var researchTasks = subQuestions
                .Select(async (q, i) =>
                {
                    await researcherGate.WaitAsync(ct);
                    try
                    {
                        return await _researcher.ResearchAsync(q, i + 1, researcherKernel, request.RunId, ct: ct);
                    }
                    finally
                    {
                        researcherGate.Release();
                    }
                })
                .ToList();
            var researchResults = await Task.WhenAll(researchTasks);
            var research = researchResults.Select(r => r.Summary).ToArray();
            foreach (var r in researchResults) totalUsage = totalUsage.Add(r.Usage);

            // 3. CRITIQUE
            var (critique, criticUsage) = await _critic.CritiqueAsync(request.Question, research, criticKernel, request.RunId, ct);
            totalUsage = totalUsage.Add(criticUsage);

            // 3b. CRITIC-DRIVEN RETRY — one focused pass if the critic flagged a fixable gap.
            //     We don't re-run the critic afterwards (cap retries at 1) to keep latency bounded
            //     and avoid potential loops on stubborn questions.
            //     Disabled when the eval harness wants to compare "no retry" as a variant.
            var augmentedResearch = research.ToList();
            if (config.EnableCriticRetry && TryDeriveRetryQuestion(critique, out var focusedQuestion))
            {
                var (retrySummary, retryUsage) = await _researcher.ResearchAsync(
                    focusedQuestion,
                    augmentedResearch.Count + 1,
                    researcherKernel,
                    request.RunId,
                    searchMemoryFirst: true,
                    ct: ct);
                augmentedResearch.Add(retrySummary);
                totalUsage = totalUsage.Add(retryUsage);
            }

            // 4. SYNTHESIZE — streams the final answer to the UI.
            var (finalAnswer, synthUsage) = await _synthesizer.SynthesizeAsync(
                request.Question, augmentedResearch, critique, synthKernel, request.RunId, ct);
            totalUsage = totalUsage.Add(synthUsage);

            // 5. Terminate the run — system-level AgentFinishedEvent carries run-wide totals.
            await _bus.PublishAsync(new AgentFinishedEvent(
                request.RunId, AgentId.System, finalAnswer,
                totalUsage.TokensIn, totalUsage.TokensOut, totalUsage.CostUsd,
                DateTime.UtcNow), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator failed for run {RunId}", request.RunId);
            await _bus.PublishAsync(new AgentErrorEvent(
                request.RunId, AgentId.System, ex.Message, DateTime.UtcNow), CancellationToken.None);
        }
    }

    /// <summary>
    /// Pick the strongest signal from the critique (shape mismatch beats weak claim — the
    /// shape failure typically dominates whether the answer is useful) and turn it into a
    /// focused sub-question for the retry researcher.
    /// </summary>
    internal static bool TryDeriveRetryQuestion(Critique critique, out string focusedQuestion)
    {
        if (critique.Ok)
        {
            focusedQuestion = string.Empty;
            return false;
        }

        var signal = critique.ShapeMismatch.FirstOrDefault()
                     ?? critique.WeakClaims.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(signal))
        {
            focusedQuestion = string.Empty;
            return false;
        }

        focusedQuestion = $"Provide specific facts (with citations) to address this gap: {signal}";
        return true;
    }
}
