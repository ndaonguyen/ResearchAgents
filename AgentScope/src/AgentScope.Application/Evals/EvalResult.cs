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
///
/// <para><see cref="JudgeScore"/> is the headline judge score. When the judge runs n-of-k
/// self-consistency, <see cref="JudgeScores"/> holds the raw per-sample votes and
/// <see cref="JudgeScoreStdDev"/> their spread — both null/absent for single-sample or
/// pre-feature rows. <see cref="SchemaVersion"/> tags the row format (absent ⇒ pre-versioning).</para>
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
    string? SynthesizerModel = null,
    IReadOnlyList<int>? JudgeScores = null,
    double? JudgeScoreStdDev = null,
    int SchemaVersion = 2)  // keep in sync with CurrentSchemaVersion below
{
    /// <summary>Bumped when the row shape changes. 2 = added n-of-k judge fields.</summary>
    public const int CurrentSchemaVersion = 2;
}
