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

    /// <summary>
    /// RAG corpora exposed to the researcher as separate kernel plugins. Each corpus
    /// gets its own Qdrant collection and shows up to the LLM as a distinct tool with
    /// its own description, so the researcher can pick the right corpus per sub-question.
    /// Empty by default; populate via appsettings/user-secrets.
    /// </summary>
    public CorpusOptions[] Corpora { get; init; } = Array.Empty<CorpusOptions>();
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
/// Where the Web UI looks for eval-harness JSONL output, and where it lists question
/// sets the user can pick from when triggering an eval from the browser.
///
/// Defaults are relative to the process's current working directory — fine when the
/// Web app is launched from the repo root. Override in appsettings if running elsewhere.
/// </summary>
public sealed class EvalsOptions
{
    public string ResultsDirectory { get; init; } = "results";
    public string QuestionsDirectory { get; init; } = "tests/AgentScope.Evals/questions";
}

/// <summary>
/// One RAG corpus. The indexer reads <see cref="BooksDirectory"/> + <see cref="Books"/>
/// and writes to <see cref="Collection"/>. The web app registers a kernel plugin named
/// <see cref="PluginName"/> with <see cref="Description"/> as the function description
/// the LLM sees — that's the signal the researcher uses to pick this corpus over others.
///
/// Qdrant connection (host/port/key) is shared with working memory but each corpus
/// lives in its own collection.
/// </summary>
public sealed class CorpusOptions
{
    /// <summary>Internal identifier, used only in logs (e.g. "architecture", "system-design").</summary>
    public string Name { get; init; } = "";

    public bool Enabled { get; init; } = false;

    public string Collection { get; init; } = "";

    /// <summary>Absolute path to the directory containing the book PDFs.</summary>
    public string BooksDirectory { get; init; } = "";

    /// <summary>Filenames (relative to <see cref="BooksDirectory"/>) to index.</summary>
    public string[] Books { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Kernel plugin name surfaced to the LLM (e.g. "ArchitectureCorpus", "SystemDesignCorpus").
    /// The researcher sees this as the prefix on the tool name: <c>{PluginName}.Search</c>.
    /// </summary>
    public string PluginName { get; init; } = "";

    /// <summary>
    /// Description the LLM sees for this corpus's Search function. This is the dominant
    /// signal in tool selection — be specific about WHEN to prefer this corpus and what
    /// it covers. List the actual book titles if useful.
    /// </summary>
    public string Description { get; init; } = "";
}
