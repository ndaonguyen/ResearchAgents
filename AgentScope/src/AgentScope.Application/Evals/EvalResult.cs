namespace AgentScope.Application.Evals;

/// <summary>
/// One row in the JSONL results file. Both agent-side and judge-side token/cost are
/// captured separately so you can see how much the judge itself cost across the eval.
///
/// The fingerprint fields (<see cref="PromptHash"/> + the four <c>*Model</c> fields)
/// pin down *what was actually run* — without them, two rows tagged <c>variant=baseline</c>
/// from different commits or different config can look identical but came from different
/// systems. All five are optional for backward compatibility with JSONL written before
/// the fingerprint was added.
/// </summary>
public sealed record EvalResult(
    string QuestionId,
    string Variant,
    string Question,
    string RunId,
    string Answer,
    int TokensIn,
    int TokensOut,
    decimal? CostUsd,
    long DurationMs,
    bool Errored,
    string? ErrorMessage,
    int? JudgeScore,
    string? JudgeReasoning,
    int JudgeTokensIn,
    int JudgeTokensOut,
    decimal? JudgeCostUsd,
    DateTime CompletedAt,
    string? PromptHash = null,
    string? PlannerModel = null,
    string? ResearcherModel = null,
    string? CriticModel = null,
    string? SynthesizerModel = null);
