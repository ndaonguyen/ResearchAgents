namespace AgentScope.Application.Abstractions;

/// <summary>
/// Token + cost usage for a single agent invocation. Returned alongside each agent's
/// primary result so the orchestrator can aggregate run-level totals without shared state.
///
/// <see cref="CostUsd"/> is nullable because we can't always compute it (unknown model,
/// usage missing from the LLM response, test paths). Null means "unknown", which is
/// distinct from zero ("known to be free").
/// </summary>
public sealed record AgentUsage(int TokensIn, int TokensOut, decimal? CostUsd)
{
    public static AgentUsage Empty { get; } = new(0, 0, 0m);

    /// <summary>Field-wise sum. Null cost is treated as 0 only when at least one side is non-null.</summary>
    public AgentUsage Add(AgentUsage other) => new(
        TokensIn + other.TokensIn,
        TokensOut + other.TokensOut,
        (CostUsd, other.CostUsd) switch
        {
            (null, null) => null,
            (null, var b) => b,
            (var a, null) => a,
            (var a, var b) => a + b,
        });
}
