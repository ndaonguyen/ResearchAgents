using AgentScope.Domain.Runs;

namespace AgentScope.Domain.Agents;

/// <summary>
/// Domain-level description of "run an agent against this question".
/// No transport concerns, no SK types — just the data needed to start a run.
/// </summary>
public sealed record AgentRunRequest(RunId RunId, string Question);
