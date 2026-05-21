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
