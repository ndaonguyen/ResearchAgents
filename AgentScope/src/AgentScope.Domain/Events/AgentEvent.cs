using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;

namespace AgentScope.Domain.Events;

/// <summary>
/// Base record for every event emitted during an agent run.
/// Domain events know nothing about transport (SignalR), serialization, or persistence.
/// </summary>
public abstract record AgentEvent(RunId RunId, AgentId AgentId, DateTime Timestamp)
{
    /// <summary>Stable discriminator string. Used for client-side routing only.</summary>
    public abstract string Kind { get; }
}

public sealed record AgentStartedEvent(
    RunId RunId,
    AgentId AgentId,
    string AgentName,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "agent.started";
}

public sealed record AgentFinishedEvent(
    RunId RunId,
    AgentId AgentId,
    string FinalText,
    int TokensIn,
    int TokensOut,
    decimal? EstimatedCostUsd,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "agent.finished";
}

public sealed record AgentTokenEvent(
    RunId RunId,
    AgentId AgentId,
    string Delta,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "agent.token";
}

public sealed record ToolCalledEvent(
    RunId RunId,
    AgentId AgentId,
    string ToolName,
    string ArgumentsJson,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "tool.called";
}

public sealed record ToolResultEvent(
    RunId RunId,
    AgentId AgentId,
    string ToolName,
    string ResultJson,
    long DurationMs,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "tool.result";
}

public sealed record AgentErrorEvent(
    RunId RunId,
    AgentId AgentId,
    string Message,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "agent.error";
}

/// <summary>
/// One chunk returned by a corpus search. Carries enough metadata for the UI to
/// render a "Sources used" panel without going back to Qdrant.
/// </summary>
public sealed record CorpusChunk(
    string Book,
    int PageStart,
    int PageEnd,
    double Score,
    string Text);

/// <summary>
/// Emitted by <c>CorpusSearchPlugin</c> after Qdrant returns matches. Lets the UI show
/// exactly which book chunks the LLM saw for a given retrieval — closes the RAG loop
/// from black box ("the model cited Newman pp. 312-315") to transparent ("here are the
/// 5 chunks that were retrieved and ranked").
/// </summary>
public sealed record CorpusChunksRetrievedEvent(
    RunId RunId,
    AgentId AgentId,
    string Corpus,
    string Query,
    IReadOnlyList<CorpusChunk> Chunks,
    DateTime Timestamp) : AgentEvent(RunId, AgentId, Timestamp)
{
    public override string Kind => "corpus.chunks";
}
