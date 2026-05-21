using System.Text;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Reviews the research summaries against the original question. Flags missing topics
/// and weak claims. JSON output — consumed by the synthesizer to shape the final answer.
/// </summary>
public sealed class CriticAgent : ICriticAgent
{
    private const string SystemPrompt = """
        You are a research critic. Given an original question and N research summaries,
        evaluate whether the summaries together adequately answer the original question.

        Rules:
        - Be strict but fair. Only flag genuine issues, not minor nitpicks.
        - "missing_topics": angles of the original question that no summary addressed.
        - "weak_claims": specific claims in the summaries that lack citations or are vague.
        - "shape_mismatch": structural mismatches between the question's asked shape
          and the summaries' content. Examples:
            * Question asks "list each chapter" but no summary contains a chapter list.
            * Question asks to compare A vs B but only one is covered.
            * Question asks for 5 examples but only general theory is provided.
          Be specific — quote the structural word from the question.
        - "ok": true only when there are no missing topics, no weak claims, AND no shape mismatches.
        - Return ONLY a JSON object with keys "ok", "missing_topics", "weak_claims", "shape_mismatch".

        Example:
        {"ok": false, "missing_topics": ["security tradeoffs"], "weak_claims": ["'much faster' lacks numbers"], "shape_mismatch": ["question asks to 'list each step' but summaries give continuous prose"]}
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly ILogger<CriticAgent> _logger;

    public CriticAgent(IAgentEventBus bus, AgentRunContext runContext, ILogger<CriticAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _logger = logger;
    }

    public async Task<Critique> CritiqueAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default)
    {
        var agentId = AgentId.Critic;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Critic", DateTime.UtcNow), ct);

        var prompt = BuildCriticPrompt(originalQuestion, research);

        var agent = new ChatCompletionAgent
        {
            Name = "Critic",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ResponseFormat = "json_object"
            })
        };

        var raw = new StringBuilder();
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            raw.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var json = raw.ToString();
        var critique = ParseCritique(json);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, json, 0, 0, DateTime.UtcNow), ct);

        return critique;
    }

    private static string BuildCriticPrompt(string question, IReadOnlyList<ResearchSummary> research)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Original question: {question}");
        sb.AppendLine();
        sb.AppendLine("Research summaries:");
        for (var i = 0; i < research.Count; i++)
        {
            sb.AppendLine($"--- Summary {i + 1} (sub-question: {research[i].SubQuestion}) ---");
            sb.AppendLine(research[i].Body);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    internal static Critique ParseCritique(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okEl) &&
                     okEl.ValueKind == JsonValueKind.True;

            var missing = ReadStringArray(root, "missing_topics");
            var weak = ReadStringArray(root, "weak_claims");
            var shape = ReadStringArray(root, "shape_mismatch");

            return new Critique(ok, missing, weak, shape);
        }
        catch (JsonException)
        {
            return new Critique(
                false,
                Array.Empty<string>(),
                new[] { "Critic output was not valid JSON." },
                Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}

public sealed record Critique(
    bool Ok,
    IReadOnlyList<string> MissingTopics,
    IReadOnlyList<string> WeakClaims,
    IReadOnlyList<string> ShapeMismatch);
