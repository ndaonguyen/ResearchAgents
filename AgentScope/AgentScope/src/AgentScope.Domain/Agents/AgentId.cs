namespace AgentScope.Domain.Agents;

/// <summary>
/// Stable identifier for an agent within a run (e.g. "planner", "researcher-1", "synthesizer").
/// Use "system" for orchestrator-level events.
/// </summary>
public readonly record struct AgentId(string Value)
{
    public static AgentId System { get; } = new("system");

    public override string ToString() => Value;
}
