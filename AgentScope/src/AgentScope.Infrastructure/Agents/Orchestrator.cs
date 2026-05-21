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

    public async Task RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        try
        {
            var kernel = _kernelFactory.Create();

            // 1. PLAN
            var subQuestions = await _planner.PlanAsync(request.Question, kernel, request.RunId, ct);

            // 2. RESEARCH — parallel fanout
            var researchTasks = subQuestions
                .Select((q, i) => _researcher.ResearchAsync(q, i + 1, kernel, request.RunId, ct))
                .ToList();
            var research = await Task.WhenAll(researchTasks);

            // 3. CRITIQUE
            var critique = await _critic.CritiqueAsync(request.Question, research, kernel, request.RunId, ct);

            // 4. SYNTHESIZE — streams the final answer to the UI.
            var finalAnswer = await _synthesizer.SynthesizeAsync(
                request.Question, research, critique, kernel, request.RunId, ct);

            // 5. Terminate the run.
            await _bus.PublishAsync(new AgentFinishedEvent(
                request.RunId, AgentId.System, finalAnswer, 0, 0, DateTime.UtcNow), ct);
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
}
