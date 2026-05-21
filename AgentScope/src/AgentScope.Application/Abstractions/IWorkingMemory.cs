using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;

namespace AgentScope.Application.Abstractions;

/// <summary>
/// Port: per-run vector-backed working memory. Agents save text snippets keyed by
/// <see cref="RunId"/>; later agents in the same run can semantically search them.
/// Memory is scoped to a single run — concurrent runs MUST NOT see each other's data.
///
/// Implementations: <c>NullWorkingMemory</c> (default no-op) and <c>QdrantWorkingMemory</c>.
/// </summary>
public interface IWorkingMemory
{
    /// <summary>
    /// Persist <paramref name="text"/> under <paramref name="runId"/>. The implementation
    /// embeds the text and stores the vector with metadata for later retrieval.
    /// </summary>
    Task SaveAsync(
        RunId runId,
        AgentId agentId,
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Return up to <paramref name="k"/> snippets from <paramref name="runId"/> most
    /// similar to <paramref name="query"/>. Results from other runs MUST be filtered out.
    /// </summary>
    Task<IReadOnlyList<MemoryHit>> SearchAsync(
        RunId runId,
        string query,
        int k = 5,
        CancellationToken ct = default);
}

public sealed record MemoryHit(
    string Text,
    float Score,
    IReadOnlyDictionary<string, string> Metadata);
