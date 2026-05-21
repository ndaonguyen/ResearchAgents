using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;

namespace AgentScope.Infrastructure.Memory;

/// <summary>
/// Default <see cref="IWorkingMemory"/> implementation: discards writes, returns no hits.
/// Registered when Qdrant is not configured so the orchestration still runs end-to-end
/// without a vector store.
/// </summary>
public sealed class NullWorkingMemory : IWorkingMemory
{
    public Task SaveAsync(
        RunId runId,
        AgentId agentId,
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MemoryHit>> SearchAsync(
        RunId runId,
        string query,
        int k = 5,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryHit>>(Array.Empty<MemoryHit>());
}
