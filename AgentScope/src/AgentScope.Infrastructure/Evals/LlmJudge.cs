using System.Text;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Application.Evals;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Evals;

/// <summary>
/// LLM-as-judge — a single judge call. Scores one (question, answer) pair on a 1-5 scale with
/// one-sentence reasoning. Uses its own minimal kernel (no plugins, no event filter) — judge
/// calls must not pollute the run event stream and don't need WebSearch/BookLookup.
///
/// This type deliberately does exactly ONE model call per score so it stays trivially testable.
/// Multi-sample (n-of-k) aggregation lives in <see cref="PanelJudge"/>, which wraps this and
/// calls <see cref="ScoreOnceAsync"/> k times with distinct seeds.
///
/// Calibration discipline: before trusting these scores at scale, hand-grade ~20 outputs
/// and check agreement with the judge. Without that step, the leaderboard is confidently wrong.
/// </summary>
public sealed class LlmJudge : IAnswerJudge
{
    // Anchored 1-5 scale. Concrete per-level descriptions reduce inter-call variance
    // because the judge no longer has to invent its own bar for each level — the
    // textually adjacent anchors do the work. See docs/evals.md "Calibration discipline".
    private const string DefaultRubric = """
        Score on a 1-5 scale across four dimensions:
        - Factual accuracy (no hallucinations)
        - Completeness (addresses all parts of the question)
        - Shape compliance (matches any requested structure)
        - Citation quality (claims backed by sources where appropriate)

        Anchored levels:
        - 5 — All four dimensions met. Would pass a senior engineer's bar without edits.
        - 4 — Accurate and complete; minor shape or citation gaps. Usable as-is.
        - 3 — Largely correct but missing one significant dimension OR contains a minor factual slip.
        - 2 — Multiple gaps OR a factual error that would mislead the reader.
        - 1 — Wrong, off-topic, or unusable.

        Length alone is not quality — do not reward verbosity. Penalize unnecessary length.
        """;

    private const string SystemPrompt = """
        You are evaluating an AI research assistant's answer to a question.

        You will be given:
        - The original question.
        - Optionally, a reference answer.
        - Optionally, expected shape requirements.
        - A rubric.
        - The candidate answer.

        Return ONLY a JSON object: {"score": <integer 1-5>, "reasoning": "<one sentence>"}.
        """;

    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly double _temperature;
    private readonly long? _seedBase;
    private readonly ILogger<LlmJudge> _logger;

    public LlmJudge(
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<LlmJudge> logger)
    {
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.Judge.Model;
        _temperature = options.Value.Judge.Temperature;
        _seedBase = options.Value.Judge.SeedBase;
        _logger = logger;
    }

    /// <summary>
    /// Port entry point — a single sample using the configured base seed. <see cref="PanelJudge"/>
    /// bypasses this and calls <see cref="ScoreOnceAsync"/> directly to vary the seed per sample.
    /// </summary>
    public Task<JudgeVerdict> ScoreAsync(EvalQuestion question, string answer, CancellationToken ct = default)
        => ScoreOnceAsync(question, answer, _seedBase, ct);

    /// <summary>One judge call. <paramref name="seed"/> is passed through to the model for
    /// best-effort reproducibility; null leaves it unset.</summary>
    public async Task<JudgeVerdict> ScoreOnceAsync(EvalQuestion question, string answer, long? seed, CancellationToken ct = default)
    {
        var kernel = _kernelFactory.Create(modelOverride: _model, includePlugins: false);

        var agent = new ChatCompletionAgent
        {
            Name = "Judge",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ResponseFormat = "json_object",
                Temperature = _temperature,  // 0 for a lone sample; raise for n-of-k diversity
                Seed = seed
            })
        };

        var prompt = BuildPrompt(question, answer);
        var raw = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;
            var delta = update.Message.Content;
            if (!string.IsNullOrEmpty(delta)) raw.Append(delta);
        }

        var json = raw.ToString();
        var (score, reasoning) = ParseVerdict(json);

        // Bounds check: a model that hallucinates score=7 (or 0, -1) would otherwise
        // poison MeanScore in the Past Runs viewer. Null it out and log so calibration
        // surfaces the failure mode instead of silently absorbing it.
        if (score is { } s && (s < 1 || s > 5))
        {
            _logger.LogWarning("Judge returned out-of-range score {Score}; treating as null", s);
            score = null;
        }

        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        if (score is null)
        {
            _logger.LogWarning("Judge returned unparseable output: {Raw}", json);
        }

        return new JudgeVerdict(score, reasoning, usage);
    }

    private static string BuildPrompt(EvalQuestion question, string answer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Question: {question.Question}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(question.ReferenceAnswer))
        {
            sb.AppendLine($"Reference answer:\n{question.ReferenceAnswer}");
            sb.AppendLine();
        }

        if (question.ExpectedShape is { Length: > 0 })
        {
            sb.AppendLine("Expected shape:");
            foreach (var s in question.ExpectedShape) sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        sb.AppendLine($"Rubric:\n{question.Rubric ?? DefaultRubric}");
        sb.AppendLine();
        sb.AppendLine($"Candidate answer:\n{answer}");
        return sb.ToString();
    }

    private static (int? Score, string? Reasoning) ParseVerdict(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int? score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : null;

            string? reasoning = root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            return (score, reasoning);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
