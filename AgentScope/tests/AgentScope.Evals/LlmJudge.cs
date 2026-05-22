using System.Text;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Evals;

/// <summary>
/// LLM-as-judge. Scores one (question, answer) pair on a 1-5 scale with one-sentence reasoning.
/// Uses its own minimal kernel (no plugins, no event filter) — judge calls must not pollute
/// the run event stream and don't need WebSearch/BookLookup.
///
/// Calibration discipline: before trusting these scores at scale, hand-grade ~20 outputs
/// and check agreement with the judge. Without that step, the leaderboard is confidently wrong.
/// </summary>
public sealed class LlmJudge
{
    private const string DefaultRubric = """
        Score on a 1-5 scale based on:
        - Factual accuracy (no hallucinations)
        - Completeness (addresses all parts of the question)
        - Shape compliance (matches any requested structure)
        - Citation quality (claims backed by sources where appropriate)

        5 = excellent on all dimensions; 3 = adequate but with notable gaps; 1 = wrong or unusable.
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
        _logger = logger;
    }

    public async Task<JudgeVerdict> ScoreAsync(EvalQuestion question, string answer, CancellationToken ct = default)
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
                Temperature = 0.0  // judge should be as deterministic as we can get it
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

    internal static (int? Score, string? Reasoning) ParseVerdict(string json)
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

public sealed record JudgeVerdict(int? Score, string? Reasoning, AgentUsage Usage);
