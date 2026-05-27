using AgentScope.Application.Abstractions;

namespace AgentScope.Application.Evals;

/// <summary>
/// Port for an LLM-as-judge. Scores one (question, answer) pair on a 1-5 scale with
/// one-sentence reasoning. Implementations live in Infrastructure (e.g. LlmJudge over
/// Semantic Kernel) so the Application layer stays free of SK references.
/// </summary>
public interface IAnswerJudge
{
    Task<JudgeVerdict> ScoreAsync(EvalQuestion question, string answer, CancellationToken ct = default);
}

public sealed record JudgeVerdict(int? Score, string? Reasoning, AgentUsage Usage);
