using AgentScope.Application.Abstractions;
using AgentScope.Domain.Runs;
using Microsoft.SemanticKernel;

namespace AgentScope.Infrastructure.Agents;

public interface IPlannerAgent
{
    Task<(IReadOnlyList<string> SubQuestions, AgentUsage Usage)> PlanAsync(
        string question, Kernel kernel, RunId runId, CancellationToken ct = default);
}

public interface IResearcherAgent
{
    /// <summary>
    /// Research one sub-question.
    /// </summary>
    /// <param name="searchMemoryFirst">
    /// When true, the researcher searches working memory for prior summaries in this run
    /// and includes them as context (so it can fill gaps instead of duplicating). Set true
    /// only for the critic-driven retry pass — the initial parallel researchers have
    /// nothing to read.
    /// </param>
    Task<(ResearchSummary Summary, AgentUsage Usage)> ResearchAsync(
        string subQuestion,
        int index,
        Kernel kernel,
        RunId runId,
        bool searchMemoryFirst = false,
        CancellationToken ct = default);
}

public interface ICriticAgent
{
    Task<(Critique Critique, AgentUsage Usage)> CritiqueAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default);
}

public interface ISynthesizerAgent
{
    Task<(string FinalText, AgentUsage Usage)> SynthesizeAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Critique critique,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default);
}
