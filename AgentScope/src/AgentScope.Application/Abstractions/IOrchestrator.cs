using AgentScope.Domain.Agents;

namespace AgentScope.Application.Abstractions;

/// <summary>
/// Port: coordinates a multi-agent run (planner → researchers → critic → synthesizer)
/// against a single question, publishing events for every agent and tool to
/// <see cref="IAgentEventBus"/>. The orchestrator owns the final
/// system-level <c>AgentFinishedEvent</c> that terminates the run's event stream.
///
/// Implementations live in Infrastructure (Semantic Kernel adapter).
/// </summary>
public interface IOrchestrator
{
    Task RunAsync(AgentRunRequest request, OrchestratorConfig config, CancellationToken ct = default);
}
