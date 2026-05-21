using System.Text;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Decomposes a question into 2-5 focused, standalone sub-questions.
/// JSON output — consumed by the orchestrator, not the user.
/// </summary>
public sealed class PlannerAgent : IPlannerAgent
{
    private const string SystemPrompt = """
        You decompose a research question into 2-5 focused, standalone sub-questions
        that can each be answered independently by a web search.

        Rules:
        - Each sub-question must be self-contained — assume the researcher will see ONLY the sub-question, not the original.
        - PRESERVE STRUCTURAL INTENT. If the original question asks for a specific shape
          (e.g. "list each chapter", "compare A vs B", "give 5 examples", "step by step"),
          at least one sub-question must directly serve that shape — name the artefact
          and quote the key word from the question. For example, if asked
          "brief each chapter of the book X", one sub-question must be
          "What are the chapters (table of contents) of the book X?".
        - Cover distinct angles (definition, mechanism, comparison, examples, tradeoffs, recent developments) where relevant.
        - Aim for 3 sub-questions by default. Use 2 only for very narrow questions, 5 only for broad ones.
        - Return ONLY a JSON object with a single key "sub_questions" whose value is an array of strings.

        Example output:
        {"sub_questions": ["What is X?", "How does X compare to Y?", "What are the tradeoffs of X?"]}
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<PlannerAgent> _logger;

    public PlannerAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<PlannerAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<string> SubQuestions, AgentUsage Usage)> PlanAsync(
        string question, Kernel kernel, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Planner;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Planner", DateTime.UtcNow), ct);

        var agent = new ChatCompletionAgent
        {
            Name = "Planner",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(responseFormat: "json_object"))
        };

        var raw = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, question);

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;

            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            raw.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var json = raw.ToString();
        var subQuestions = ParseSubQuestions(json);
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, json, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        if (subQuestions.Count == 0)
        {
            _logger.LogWarning("Planner returned no sub-questions for run {RunId}; falling back to original question", runId);
            return (new[] { question }, usage);
        }

        return (subQuestions, usage);
    }

    internal static IReadOnlyList<string> ParseSubQuestions(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("sub_questions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
