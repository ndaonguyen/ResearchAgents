namespace AgentScope.Domain.Runs;

/// <summary>
/// Strongly-typed identifier for an agent run.
/// Wrapping the string prevents accidentally passing AgentId where RunId is expected.
/// </summary>
public readonly record struct RunId(string Value)
{
    public static RunId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}
