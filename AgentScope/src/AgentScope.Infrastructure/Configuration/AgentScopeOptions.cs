using System.ComponentModel.DataAnnotations;

namespace AgentScope.Infrastructure.Configuration;

/// <summary>
/// Configuration for the Semantic Kernel infrastructure.
/// Bound from the "AgentScope" section of appsettings/user-secrets.
/// </summary>
public sealed class AgentScopeOptions
{
    public const string SectionName = "AgentScope";

    public OpenAiOptions OpenAi { get; init; } = new();
    public TavilyOptions Tavily { get; init; } = new();
    public QdrantOptions Qdrant { get; init; } = new();
    public JudgeOptions Judge { get; init; } = new();
    public EvalsOptions Evals { get; init; } = new();
}

public sealed class OpenAiOptions
{
    [Required]
    public string ApiKey { get; init; } = "";

    public string Model { get; init; } = "gpt-4o-mini";

    public string EmbeddingModel { get; init; } = "text-embedding-3-small";
}

public sealed class TavilyOptions
{
    [Required]
    public string ApiKey { get; init; } = "";
}

/// <summary>
/// Qdrant vector store for per-run working memory. Disabled by default — when
/// <see cref="Enabled"/> is false, the app uses a no-op memory and runs without a vector store.
/// </summary>
public sealed class QdrantOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Host (without scheme), e.g. "localhost".</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>gRPC port. Qdrant defaults to 6334 for gRPC.</summary>
    public int Port { get; init; } = 6334;

    public bool UseHttps { get; init; } = false;

    /// <summary>Optional API key for Qdrant Cloud / secured local instances.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Collection name. Created lazily on first write.</summary>
    public string Collection { get; init; } = "agentscope-working-memory";
}

/// <summary>
/// LLM-as-judge model used by the eval harness. Kept in its own section so the judge model
/// is decoupled from the orchestrator's default model — typically you want a cheap judge
/// (e.g. <c>gpt-4o-mini</c>) regardless of what the agents are running.
/// API key is reused from <see cref="OpenAiOptions"/>.
/// </summary>
public sealed class JudgeOptions
{
    public string Model { get; init; } = "gpt-4o-mini";
}

/// <summary>
/// Where the Web UI looks for eval-harness JSONL output. Default is <c>results</c>
/// relative to the current working directory — which matches where the eval CLI writes
/// when launched from the repo root.
/// </summary>
public sealed class EvalsOptions
{
    public string ResultsDirectory { get; init; } = "results";
}
