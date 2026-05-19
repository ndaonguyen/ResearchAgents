using AgentScope.Domain.Agents;

namespace AgentScope.Application.Abstractions;

/// <summary>
/// Port: runs a research agent against a question, publishing events to <see cref="IAgentEventBus"/>.
/// Implementations live in Infrastructure (Semantic Kernel adapter).
///
/// Week 1: a single ChatCompletionAgent with web search.
/// Week 2+: a full orchestrator (planner → researchers → critic → synthesizer).
/// The port stays the same; only the implementation grows.
/// </summary>
public interface IResearchAgent
{
    Task RunAsync(AgentRunRequest request, CancellationToken ct = default);
}
