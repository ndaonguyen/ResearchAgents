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

/// <summary>
/// A judge's verdict for one (question, answer) pair.
/// <para><see cref="Score"/> is the headline score — the median across <see cref="Scores"/>
/// for a multi-sample (n-of-k) judge, or the single score otherwise. Null when no sample
/// produced a usable score.</para>
/// <para><see cref="Scores"/> holds the raw per-sample votes (the audit artifact: keep the
/// votes, not just the aggregate). <see cref="ScoreStdDev"/> is their spread — null for a
/// single sample (no dispersion to measure). <see cref="Usage"/> is summed across samples.</para>
/// </summary>
public sealed record JudgeVerdict(
    int? Score,
    string? Reasoning,
    AgentUsage Usage,
    IReadOnlyList<int>? Scores = null,
    double? ScoreStdDev = null);
