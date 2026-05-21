using AgentScope.Domain.Runs;
using Microsoft.SemanticKernel;

namespace AgentScope.Infrastructure.Agents;

public interface IPlannerAgent
{
    Task<IReadOnlyList<string>> PlanAsync(
        string question, Kernel kernel, RunId runId, CancellationToken ct = default);
}

public interface IResearcherAgent
{
    Task<ResearchSummary> ResearchAsync(
        string subQuestion, int index, Kernel kernel, RunId runId, CancellationToken ct = default);
}

public interface ICriticAgent
{
    Task<Critique> CritiqueAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default);
}

public interface ISynthesizerAgent
{
    Task<string> SynthesizeAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Critique critique,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default);
}
