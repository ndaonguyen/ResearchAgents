using System.Text;
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
/// Researches one sub-question. Calls WebSearch as needed. Returns a concise factual
/// summary with citations. Multiple instances run in parallel from the orchestrator.
/// </summary>
public sealed class ResearcherAgent : IResearcherAgent
{
    private const string SystemPrompt = """
        You research one sub-question and return a concise factual summary.

        Rules:
        - Stay focused on the given sub-question. Do not drift to related topics.
        - Use WebSearch when you need fresh or specific facts.
        - Cite sources by URL inline, e.g. "WebAssembly runs in a sandbox (https://...)".
        - 3-6 sentences of prose. No headings, no bullet lists. Be dense.
        - If WebSearch returns nothing useful, say so explicitly — do not invent facts.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IWorkingMemory _memory;
    private readonly ILogger<ResearcherAgent> _logger;

    public ResearcherAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IWorkingMemory memory,
        ILogger<ResearcherAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _memory = memory;
        _logger = logger;
    }

    public async Task<ResearchSummary> ResearchAsync(
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
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            })
        };

        var body = new StringBuilder();
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, ResearcherPromptBuilder.Build(subQuestion, priorContext));

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            body.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var summary = new ResearchSummary(subQuestion, body.ToString());

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
            runId, agentId, summary.Body, 0, 0, DateTime.UtcNow), ct);

        return summary;
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
