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
    bool EnableCriticRetry = true)
{
    public static OrchestratorConfig Default { get; } = new();
}
