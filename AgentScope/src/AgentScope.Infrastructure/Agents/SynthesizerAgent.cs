using System.Text;
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

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Writes the final answer using the research summaries and critic notes. No tools —
/// synthesis is pure writing, not fact-gathering. Output streams token-by-token to the UI.
/// </summary>
public sealed class SynthesizerAgent : ISynthesizerAgent
{
    internal const string SystemPrompt = """
        You write the final answer to a user's research question.

        Inputs you will receive:
        - The original question.
        - N research summaries (each addressing a sub-question).
        - A critic's notes (missing topics, weak claims, shape mismatches) — address these where possible.

        Rules:
        - Use only facts from the research summaries. Do not introduce new claims.
        - CITATION DISCIPLINE: cite ONLY exact URLs that appear verbatim in the research
          summaries. NEVER write placeholder URLs like "(https://...)" or "(URL)".
          If a claim has no source URL in the summaries, do not cite anything for that
          claim — it is acceptable to have unsourced sentences.
        - SHAPE: if the question asks for a specific shape (a list, per-item briefs,
          a comparison table, etc.) and the summaries contain enough material to
          produce that shape, produce that shape. If they don't, state plainly that
          the sources did not contain enough information to produce it.
        - LENGTH: match the question.
          * Shape-driven questions (lists, tables, comparisons, step-by-step): be tight.
            The shape is the structure; do not pad it with prose.
          * Open-ended questions ("what is", "how does", "explain", "what's new in",
            "summarise", "tell me about"): aim for depth — 6-10 paragraphs covering
            definition, mechanism, examples, and tradeoffs where the research summaries
            support it.
          Never pad. Only add depth the summaries actually justify; if they don't,
          say so instead of inventing material.
        - Acknowledge gaps the critic flagged ("the available sources did not cover X").
        - Use markdown freely on open-ended answers: section headings to organise,
          bold for key terms, bullet lists where they aid scannability. On shape-driven
          answers, keep markdown minimal — let the requested shape speak for itself.
        - Do not restate the question.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<SynthesizerAgent> _logger;

    public SynthesizerAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<SynthesizerAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(string FinalText, AgentUsage Usage)> SynthesizeAsync(
        string originalQuestion,
        IReadOnlyList<ResearchSummary> research,
        Critique critique,
        Kernel kernel,
        RunId runId,
        CancellationToken ct = default)
    {
        var agentId = AgentId.Synthesizer;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Synthesizer", DateTime.UtcNow), ct);

        var prompt = BuildPrompt(originalQuestion, research, critique);

        var agent = new ChatCompletionAgent
        {
            Name = "Synthesizer",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build())
        };

        var body = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;

            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            body.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var finalText = body.ToString();
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, finalText, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (finalText, usage);
    }

    private static string BuildPrompt(
        string question, IReadOnlyList<ResearchSummary> research, Critique critique)
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
        sb.AppendLine($"Critic verdict: ok={critique.Ok}");
        if (critique.MissingTopics.Count > 0)
            sb.AppendLine("Missing topics to acknowledge: " + string.Join("; ", critique.MissingTopics));
        if (critique.WeakClaims.Count > 0)
            sb.AppendLine("Weak claims to address or soften: " + string.Join("; ", critique.WeakClaims));
        if (critique.ShapeMismatch.Count > 0)
            sb.AppendLine("Shape mismatches to correct (rewrite the answer in the asked shape if material allows; otherwise state plainly that the sources did not contain enough information): "
                + string.Join("; ", critique.ShapeMismatch));
        return sb.ToString();
    }
}
