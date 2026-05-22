namespace AgentScope.Domain.Agents;

/// <summary>
/// Stable identifier for an agent within a run (e.g. "planner", "researcher-1", "synthesizer").
/// Use "system" for orchestrator-level events.
/// </summary>
public readonly record struct AgentId(string Value)
{
    public static AgentId System { get; } = new("system");
    public static AgentId Planner { get; } = new("planner");
    public static AgentId Critic { get; } = new("critic");
    public static AgentId Synthesizer { get; } = new("synthesizer");

    // Interview-mode agents.
    public static AgentId Interviewer { get; } = new("interviewer");
    public static AgentId Probe { get; } = new("probe");
    public static AgentId Hint { get; } = new("hint");
    public static AgentId ModelAnswer { get; } = new("model-answer");
    public static AgentId Grader { get; } = new("grader");
    public static AgentId Coach { get; } = new("coach");

    public static AgentId Researcher(int index) => new($"researcher-{index}");

    public override string ToString() => Value;
}
