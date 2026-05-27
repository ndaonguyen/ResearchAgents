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
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Researches one sub-question. Calls WebSearch as needed. Returns a concise factual
/// summary with citations. Multiple instances run in parallel from the orchestrator.
/// </summary>
public sealed class ResearcherAgent : IResearcherAgent
{
    internal const string SystemPrompt = """
        You research one sub-question and return a concise factual summary.

        Rules:
        - Stay focused on the given sub-question. Do not drift to related topics.
        - Tool preference (pick the most specific corpus that fits, fall back to WebSearch):
          * ArchitectureCorpus.Search — established software-architecture concepts
            (patterns, trade-offs, DDD terminology, microservices design, evolutionary architecture,
            event-driven patterns, monolith-to-microservices migration).
          * SystemDesignCorpus.Search — scalable system-design questions (sharding, replication,
            queueing, caching, load balancing, capacity estimation, worked system-design examples
            like URL shorteners, chat systems, news feeds).
          * AiEngineeringCorpus.Search — production AI/ML system topics (prompt engineering,
            RAG design, LLM evaluation, embeddings, fine-tuning vs prompting trade-offs,
            hallucination mitigation, inference cost & latency, guardrails, end-to-end AI
            product design). Prefer over WebSearch for any established AI-engineering concept.
          * TestingCorpus.Search — software-testing concepts (test pyramid, test doubles,
            London vs Detroit/Chicago TDD, 4-pillar test value framework, designing for
            testability, test smells, mutation testing, property-based testing, integration
            vs unit strategy).
          * CodeCraftCorpus.Search — code-quality and functional-programming topics
            (module/API design, encapsulation, error handling — exceptions vs results vs
            monads, naming, defensive vs declarative, immutability, pure & higher-order
            functions, pattern matching, composition over inheritance, code smells).
          * WebSearch.Search — recent developments, specific products, news, or anything not
            covered by the corpora above.
          * BookLookup.GetBookMetadata — when the question mentions a specific book by name and
            you need its table of contents or summary.
        - You may call multiple tools if a sub-question genuinely spans multiple corpora.
        - Citations:
          * Corpus chunks: cite the book and page range inline, e.g.
            "(Software Architecture - The Hard Parts, pp. 47-49)" or "(System Design - ByteByteGo, p. 112)".
          * Web sources: cite the URL inline, e.g. "(https://...)".
        - 3-6 sentences of prose. No headings, no bullet lists. Be dense.
        - If no tool returns anything useful, say so explicitly — do not invent facts.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IWorkingMemory _memory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<ResearcherAgent> _logger;

    public ResearcherAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IWorkingMemory memory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<ResearcherAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _memory = memory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(ResearchSummary Summary, AgentUsage Usage)> ResearchAsync(
        string subQuestion,
        int index,
        Kernel kernel,
        RunId runId,
        bool searchMemoryFirst = false,
        CancellationToken ct = default)
    {
        var agentId = AgentId.Researcher(index);
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(
            runId, agentId, $"Researcher #{index}: {subQuestion}", DateTime.UtcNow), ct);

        IReadOnlyList<MemoryHit> priorContext = Array.Empty<MemoryHit>();
        if (searchMemoryFirst)
        {
            try
            {
                priorContext = await _memory.SearchAsync(runId, subQuestion, k: 3, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory search failed for run {RunId} index {Index}; proceeding without prior context", runId, index);
            }
        }

        var agent = new ChatCompletionAgent
        {
            Name = $"Researcher-{index}",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(
                functionChoice: FunctionChoiceBehavior.Auto(),
                maxTokens: 600))
        };

        var body = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, ResearcherPromptBuilder.Build(subQuestion, priorContext));

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;

            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            body.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var summary = new ResearchSummary(subQuestion, body.ToString());
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        try
        {
            await _memory.SaveAsync(
                runId,
                agentId,
                summary.Body,
                new Dictionary<string, string>
                {
                    ["sub_question"] = summary.SubQuestion,
                    ["researcher_index"] = index.ToString()
                },
                ct);
        }
        catch (Exception ex)
        {
            // Memory is best-effort — failing to persist must not kill the research output.
            _logger.LogWarning(ex, "Failed to save research summary to memory for run {RunId} index {Index}", runId, index);
        }

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, summary.Body, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (summary, usage);
    }
}

public sealed record ResearchSummary(string SubQuestion, string Body);

public static class ResearcherPromptBuilder
{
    /// <summary>
    /// Builds the researcher's user message. When <paramref name="priorContext"/> is empty,
    /// returns the sub-question verbatim. Otherwise prepends the prior summaries with an
    /// instruction to fill gaps rather than restate what's already known.
    /// </summary>
    public static string Build(string subQuestion, IReadOnlyList<MemoryHit> priorContext)
    {
        if (priorContext.Count == 0) return subQuestion;

        var sb = new StringBuilder();
        sb.AppendLine("Prior research from earlier passes in this run. DO NOT restate these facts — use them as context and focus on what's still missing:");
        for (var i = 0; i < priorContext.Count; i++)
        {
            sb.AppendLine($"[{i + 1}] {priorContext[i].Text}");
        }
        sb.AppendLine();
        sb.AppendLine($"Now answer this sub-question, filling the gap rather than duplicating: {subQuestion}");
        return sb.ToString();
    }
}
