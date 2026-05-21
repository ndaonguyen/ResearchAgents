using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Ambient context that tells the function filter which run/agent is currently active.
/// Backed by <see cref="AsyncLocal{T}"/> so concurrent agent runs (week 2) don't cross-contaminate.
///
/// Use the <c>using</c> pattern via <see cref="Push"/> to scope a section of code.
/// </summary>
public sealed class AgentRunContext
{
    private static readonly AsyncLocal<State?> _state = new();

    public RunId? RunId => _state.Value?.RunId;
    public AgentId? AgentId => _state.Value?.AgentId;

    public IDisposable Push(RunId runId, AgentId agentId)
    {
        var previous = _state.Value;
        _state.Value = new State(runId, agentId);
        return new Scope(previous);
    }

    private sealed record State(RunId RunId, AgentId AgentId);

    private sealed class Scope : IDisposable
    {
        private readonly State? _previous;

        public Scope(State? previous) => _previous = previous;

        public void Dispose() => _state.Value = _previous;
    }
}
