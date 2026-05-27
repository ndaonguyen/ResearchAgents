namespace AgentScope.Application.Abstractions;

/// <summary>
/// Per-run knobs for the orchestrator. Nullable model fields fall back to
/// <c>AgentScopeOptions.OpenAi.Model</c> inside the orchestrator. Used by the eval harness
/// to compare variants; production callers pass <see cref="Default"/>.
///
/// Intentionally lives in Application, not Domain — Domain has no notion of "per-role model"
/// or "orchestrator knobs", those are an Application-layer concept.
/// </summary>
public sealed record OrchestratorConfig(
    string? PlannerModel = null,
    string? ResearcherModel = null,
    string? CriticModel = null,
    string? SynthesizerModel = null,
    bool EnableCriticRetry = true,
    int MaxResearcherConcurrency = 3)
{
    /// <summary>
    /// Default orchestrator config. Researchers are pinned to a small, high-TPM model
    /// independently of <c>OpenAi.Model</c> so that bumping the global model later
    /// (e.g. setting the synthesizer to <c>gpt-4o</c>) doesn't also upgrade every
    /// researcher — which would silently push token usage past Tier-1 rate limits
    /// (the failure we hit at <c>https://platform.openai.com/account/rate-limits</c>).
    /// Override per-variant when comparing a premium researcher.
    /// </summary>
    public static OrchestratorConfig Default { get; } = new(
        ResearcherModel: "gpt-4o-mini-2024-07-18");
}
