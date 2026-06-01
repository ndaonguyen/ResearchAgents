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

    /// <summary>
    /// Default chat-completion model for every agent role (planner / researcher / critic
    /// / synthesizer). Pinned to a dated snapshot so eval scores stay comparable across
    /// silent OpenAI alias rotations — same reason <see cref="JudgeOptions.Model"/> is
    /// pinned. Override per-role via <c>OrchestratorConfig</c> at the variant level when
    /// you want to compare different models on the same questions.
    /// </summary>
    public string Model { get; init; } = "gpt-4o-mini-2024-07-18";

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
///
/// Pinned to a dated snapshot by default. The floating alias (<c>gpt-4o-mini</c>) rotates
/// to a new underlying snapshot periodically — historical scores stop being comparable
/// across that boundary, which silently corrupts variant comparisons. Override in appsettings
/// when you intentionally want to upgrade the judge.
/// </summary>
public sealed class JudgeOptions
{
    public string Model { get; init; } = "gpt-4o-mini-2024-07-18";

    /// <summary>
    /// Number of independent judge calls per answer (n-of-k self-consistency). The headline
    /// score is the median of the samples; their spread is recorded as the dispersion.
    /// Default 1 = single call, behaviour unchanged. For a meaningful spread you must also
    /// raise <see cref="Temperature"/> above 0 — k identical greedy draws carry no variance.
    /// </summary>
    public int Samples { get; init; } = 1;

    /// <summary>
    /// Sampling temperature for judge calls. 0 (default) is greedy/near-deterministic — correct
    /// for a single sample. Raise to ~0.5-0.7 when <see cref="Samples"/> &gt; 1 so the draws
    /// actually differ and the dispersion measures something.
    /// </summary>
    public double Temperature { get; init; } = 0.0;

    /// <summary>
    /// Base seed for judge calls. When set, sample <c>i</c> uses <c>SeedBase + i</c>, making the
    /// whole panel reproducible: re-running an eval reproduces the same k scores (best-effort —
    /// OpenAI seeds hold only while the backend <c>system_fingerprint</c> is unchanged). Null
    /// leaves the seed unset (non-reproducible sampling).
    /// </summary>
    public long? SeedBase { get; init; }
}

/// <summary>
/// Where the Web UI looks for eval-harness JSONL output, and where it lists question
/// sets the user can pick from when triggering an eval from the browser.
///
/// Relative paths are resolved against the repo root via <see cref="RepoPath"/>, so
/// defaults work regardless of which directory the host is launched from. Absolute
/// paths in appsettings bypass that and are used as-is.
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
