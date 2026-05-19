using System.Text;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Week 1: single ChatCompletionAgent with web search.
/// The <see cref="IResearchAgent"/> port stays the same as orchestration grows; this class
/// will sprout planner/critic/synthesizer in week 2.
/// </summary>
public sealed class SemanticKernelResearchAgent : IResearchAgent
{
    private static readonly AgentId ResearcherAgentId = new("researcher");

    private readonly IKernelFactory _kernelFactory;
    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly ILogger<SemanticKernelResearchAgent> _logger;

    public SemanticKernelResearchAgent(
        IKernelFactory kernelFactory,
        IAgentEventBus bus,
        AgentRunContext runContext,
        ILogger<SemanticKernelResearchAgent> logger)
    {
        _kernelFactory = kernelFactory;
        _bus = bus;
        _runContext = runContext;
        _logger = logger;
    }

    public async Task RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var runId = request.RunId;
        var agentId = ResearcherAgentId;

        // Scope the AsyncLocal context so the function filter knows which run/agent is active.
        using var _ = _runContext.Push(runId, agentId);

        var kernel = _kernelFactory.Create();

        var agent = new ChatCompletionAgent
        {
            Name = "Researcher",
            Instructions = """
                You are a careful research assistant.
                When the user asks a question, decide whether to call the WebSearch tool.
                Always cite sources by URL when you use search results.
                Be concise. If the question is purely conceptual, answer from your own knowledge
                without searching.
                """,
            Kernel = kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            })
        };

        await _bus.PublishAsync(new AgentStartedEvent(
            runId, agentId, agent.Name!, DateTime.UtcNow), ct);

        var finalText = new StringBuilder();
        var thread = new ChatHistoryAgentThread();
        var userMessage = new ChatMessageContent(AuthorRole.User, request.Question);

        try
        {
            await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
            {
                var delta = update.Message.Content;
                if (string.IsNullOrEmpty(delta))
                    continue;

                finalText.Append(delta);
                await _bus.PublishAsync(new AgentTokenEvent(
                    runId, agentId, delta, DateTime.UtcNow), ct);
            }

            // Token usage wiring comes in week 4 (SK's UsageDetails).
            await _bus.PublishAsync(new AgentFinishedEvent(
                runId, agentId, finalText.ToString(), 0, 0, DateTime.UtcNow), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Researcher agent failed for run {RunId}", runId);
            await _bus.PublishAsync(new AgentErrorEvent(
                runId, agentId, ex.Message, DateTime.UtcNow), CancellationToken.None);

            // Also publish a system-level error so the bus stream completes.
            // The bus only treats system AgentErrorEvent as terminal — without this,
            // a subscriber would hang waiting for AgentFinishedEvent that never comes.
            await _bus.PublishAsync(new AgentErrorEvent(
                runId, AgentId.System, ex.Message, DateTime.UtcNow), CancellationToken.None);
        }
    }
}
