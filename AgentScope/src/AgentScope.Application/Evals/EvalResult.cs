namespace AgentScope.Application.Evals;

/// <summary>
/// One row in the JSONL results file. Both agent-side and judge-side token/cost are
/// captured separately so you can see how much the judge itself cost across the eval.
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
    DateTime CompletedAt);
