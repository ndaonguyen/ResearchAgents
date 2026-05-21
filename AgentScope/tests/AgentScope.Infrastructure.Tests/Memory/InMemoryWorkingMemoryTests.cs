using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Memory;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Memory;

public class NullWorkingMemoryTests
{
    [Fact]
    public async Task SaveAsync_is_a_noop_and_does_not_throw()
    {
        var memory = new NullWorkingMemory();
        await memory.SaveAsync(new RunId("r"), AgentId.Researcher(1), "hello");
        // No exception is the expectation.
    }

    [Fact]
    public async Task SearchAsync_returns_empty()
    {
        var memory = new NullWorkingMemory();
        var hits = await memory.SearchAsync(new RunId("r"), "anything");
        hits.Should().BeEmpty();
    }
}

/// <summary>
/// In-memory fake used by orchestrator/researcher tests to verify writes happen
/// and per-run isolation is respected, without needing Qdrant.
/// </summary>
public sealed class InMemoryWorkingMemory : IWorkingMemory
{
    private readonly List<(RunId RunId, AgentId AgentId, string Text, IReadOnlyDictionary<string, string> Metadata)> _store = new();

    public IReadOnlyList<(RunId RunId, AgentId AgentId, string Text, IReadOnlyDictionary<string, string> Metadata)> Writes
    {
        get { lock (_store) return _store.ToArray(); }
    }

    public Task SaveAsync(
        RunId runId,
        AgentId agentId,
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        lock (_store)
        {
            _store.Add((runId, agentId, text,
                metadata ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryHit>> SearchAsync(
        RunId runId,
        string query,
        int k = 5,
        CancellationToken ct = default)
    {
        lock (_store)
        {
            // Substring "search" filtered by RunId — good enough for tests; isolation is
            // the property under test, not relevance ranking.
            var hits = _store
                .Where(e => e.RunId == runId && e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(k)
                .Select(e => new MemoryHit(e.Text, 1.0f, e.Metadata))
                .ToList();
            return Task.FromResult<IReadOnlyList<MemoryHit>>(hits);
        }
    }
}

public class InMemoryWorkingMemoryTests
{
    [Fact]
    public async Task Search_filters_by_run_id()
    {
        var memory = new InMemoryWorkingMemory();
        await memory.SaveAsync(new RunId("a"), AgentId.Researcher(1), "shared topic");
        await memory.SaveAsync(new RunId("b"), AgentId.Researcher(1), "shared topic");

        var hitsA = await memory.SearchAsync(new RunId("a"), "shared");
        var hitsB = await memory.SearchAsync(new RunId("b"), "shared");

        hitsA.Should().ContainSingle();
        hitsB.Should().ContainSingle();
    }
}
