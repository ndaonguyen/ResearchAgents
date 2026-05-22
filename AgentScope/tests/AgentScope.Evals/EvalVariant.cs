using AgentScope.Application.Abstractions;

namespace AgentScope.Evals;

/// <summary>
/// A named orchestrator configuration to run against the question set.
/// The label is harness-level metadata only — the orchestrator never sees it.
/// </summary>
public sealed record EvalVariant(string Label, OrchestratorConfig Config);
