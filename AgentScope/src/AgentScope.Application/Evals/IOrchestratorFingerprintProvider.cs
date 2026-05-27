using AgentScope.Application.Abstractions;

namespace AgentScope.Application.Evals;

/// <summary>
/// Captures the *exact* configuration the orchestrator will run under for a given
/// <see cref="OrchestratorConfig"/>: the resolved per-role models and a content hash
/// over the four agent system prompts.
///
/// Stamped into every <see cref="EvalResult"/> so two rows tagged the same variant
/// label but produced from different commits / config can be distinguished. Without
/// this, "variant=baseline" lies once the prompts or default model change underneath.
/// </summary>
public sealed record OrchestratorFingerprint(
    string PromptHash,
    string PlannerModel,
    string ResearcherModel,
    string CriticModel,
    string SynthesizerModel);

public interface IOrchestratorFingerprintProvider
{
    OrchestratorFingerprint Capture(OrchestratorConfig config);
}
