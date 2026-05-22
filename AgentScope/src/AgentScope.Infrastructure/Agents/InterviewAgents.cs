using System.Text;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Interview;
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
/// All four interview-mode agents share the same shape: build a ChatCompletionAgent,
/// stream the response, publish events, parse the result. Keeping them in one file
/// reduces duplication of using statements and the streaming boilerplate.
/// </summary>

public sealed class InterviewerAgent : IInterviewerAgent
{
    private const string SystemPrompt = """
        You are a senior engineering interviewer running a system-design interview.
        Generate ONE realistic interview question for the given topic.

        Rules:
        - Use SystemDesignCorpus.Search FIRST to ground the question in what the books
          actually cover. Aim for a question similar in style and depth to those in
          Alex Xu's "System Design Interview" or ByteByteGo's worked examples.
        - The question should be open-ended enough to test depth, but specific enough
          that a candidate can begin reasoning about it within a minute.
        - Include explicit scale/constraints when the topic warrants them (e.g.
          "handle 1M requests/sec with p99 < 100ms").
        - Do NOT add commentary, hints, or follow-ups. Output ONLY the question text.
        - Keep it 1-3 sentences.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<InterviewerAgent> _logger;

    public InterviewerAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<InterviewerAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(string Question, AgentUsage Usage)> AskAsync(
        InterviewTopic topic, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Interviewer;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Interviewer", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "Interviewer",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(functionChoice: FunctionChoiceBehavior.Auto()))
        };

        var userMessage = new ChatMessageContent(AuthorRole.User,
            $"Topic: {topic.DisplayName}\n\nGenerate one interview question on this topic.");

        var (text, usage) = await StreamAgent(agent, userMessage, runId, agentId, ct);
        return (text.Trim(), usage);
    }

    private async Task<(string Text, AgentUsage Usage)> StreamAgent(
        ChatCompletionAgent agent, ChatMessageContent userMessage,
        RunId runId, AgentId agentId, CancellationToken ct)
    {
        var sb = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            sb.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var text = sb.ToString();
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, text, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (text, usage);
    }
}

public sealed class ProbeAgent : IProbeAgent
{
    private const string SystemPrompt = """
        You are a senior interviewer deciding whether to press the candidate with one
        clarifying or probing follow-up question.

        Rules:
        - Read the transcript carefully. If the candidate covered the high-value angles
          adequately, return null — do NOT invent a probe just to fill space.
        - If they missed a clear angle (scaling assumption unstated, edge case ignored,
          a key trade-off skipped), ask ONE focused question that targets the gap.
        - Output JSON only: {"probe": "<question text>"} or {"probe": null}.
        - The probe should be 1-2 sentences. No commentary outside the JSON.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<ProbeAgent> _logger;

    public ProbeAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<ProbeAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(string? Probe, AgentUsage Usage)> ConsiderProbeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Probe;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Probe", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "Probe",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(responseFormat: "json_object"))
        };

        var prompt = BuildTranscriptPrompt(session);
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        var sb = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            sb.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var json = sb.ToString();
        var probe = ParseProbe(json);
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, probe ?? "(no probe)", usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (probe, usage);
    }

    internal static string? ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("probe", out var el)) return null;
            if (el.ValueKind == JsonValueKind.Null) return null;
            if (el.ValueKind != JsonValueKind.String) return null;
            var s = el.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string BuildTranscriptPrompt(InterviewSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Topic: {session.Topic.DisplayName}");
        sb.AppendLine();
        sb.AppendLine("Transcript so far:");
        foreach (var turn in session.Transcript)
        {
            sb.AppendLine($"[{turn.Speaker}] {turn.Text}");
        }
        sb.AppendLine();
        sb.AppendLine("Should you probe? Respond with JSON.");
        return sb.ToString();
    }
}

public sealed class GraderAgent : IGraderAgent
{
    private const string SystemPrompt = """
        You are grading a candidate's system-design interview transcript on a 1-5 scale.
        Use SystemDesignCorpus.Search to look up what canonical answers cover, so your
        gaps are grounded in specific book content (cite the book + page when possible).

        Scoring rubric:
        - 5: comprehensive — covers trade-offs, scale, edge cases, with concrete reasoning
        - 4: strong — covers most key angles with minor gaps
        - 3: adequate — basic answer, missing depth or important angles
        - 2: weak — significant gaps or incorrect reasoning
        - 1: unable to engage with the question

        Output JSON only:
        {
          "score": <1-5>,
          "strengths": ["<bullet>", ...],
          "gaps": ["<bullet with citation>", ...]
        }
        Gap bullets should reference book chunks where possible, e.g.
        "Did not mention write-through vs write-behind caching (ByteByteGo, pp. 76-80)".
        No commentary outside the JSON.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<GraderAgent> _logger;

    public GraderAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<GraderAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(Grade Grade, AgentUsage Usage)> GradeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Grader;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Grader", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "Grader",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(
                responseFormat: "json_object",
                functionChoice: FunctionChoiceBehavior.Auto()))
        };

        var prompt = ProbeAgent.BuildTranscriptPrompt(session) +
                     "\nGrade the candidate's performance. Use SystemDesignCorpus.Search to ground gaps.";
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        var sb = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            sb.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var json = sb.ToString();
        var grade = ParseGrade(json);
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, json, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (grade, usage);
    }

    internal static Grade ParseGrade(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var score = root.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number
                ? Math.Clamp(s.GetInt32(), 1, 5)
                : 3;
            var strengths = ReadStringArray(root, "strengths");
            var gaps = ReadStringArray(root, "gaps");
            return new Grade(score, strengths, gaps);
        }
        catch (JsonException)
        {
            return new Grade(3, Array.Empty<string>(), new[] { "Grader output was not valid JSON." });
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
                var v = item.GetString();
                if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
            }
        }
        return list;
    }
}

public sealed class CoachAgent : ICoachAgent
{
    private const string SystemPrompt = """
        You are a coach giving the candidate actionable feedback after a system-design
        interview practice session.

        Inputs you receive:
        - The transcript.
        - The grader's score, strengths, and gaps.

        Output JSON only:
        {
          "summary": "<2-4 sentence narrative feedback in second person ('You...')>",
          "suggestedReading": ["<book + chapter or page range>", ...]
        }
        Be specific and constructive, not generic. Reference the corpus citations the
        grader gave you. No commentary outside the JSON.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<CoachAgent> _logger;

    public CoachAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<CoachAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(Coaching Coaching, AgentUsage Usage)> CoachAsync(
        InterviewSession session, Grade grade, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Coach;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Coach", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "Coach",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(responseFormat: "json_object"))
        };

        var prompt = BuildPrompt(session, grade);
        var userMessage = new ChatMessageContent(AuthorRole.User, prompt);

        var sb = new StringBuilder();
        IReadOnlyDictionary<string, object?>? lastMetadata = null;
        var thread = new ChatHistoryAgentThread();

        await foreach (var update in agent.InvokeStreamingAsync(userMessage, thread, cancellationToken: ct))
        {
            if (update.Message.Metadata is { Count: > 0 } md) lastMetadata = md;
            var delta = update.Message.Content;
            if (string.IsNullOrEmpty(delta)) continue;
            sb.Append(delta);
            await _bus.PublishAsync(new AgentTokenEvent(runId, agentId, delta, DateTime.UtcNow), ct);
        }

        var json = sb.ToString();
        var coaching = ParseCoaching(json);
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, json, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (coaching, usage);
    }

    private static string BuildPrompt(InterviewSession session, Grade grade)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ProbeAgent.BuildTranscriptPrompt(session));
        sb.AppendLine();
        sb.AppendLine($"Grader's score: {grade.Score}/5");
        if (grade.Strengths.Count > 0)
        {
            sb.AppendLine("Strengths:");
            foreach (var s in grade.Strengths) sb.AppendLine($"  - {s}");
        }
        if (grade.Gaps.Count > 0)
        {
            sb.AppendLine("Gaps:");
            foreach (var g in grade.Gaps) sb.AppendLine($"  - {g}");
        }
        return sb.ToString();
    }

    internal static Coaching ParseCoaching(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";
            var reading = ReadStringArray(root, "suggestedReading");
            return new Coaching(summary, reading);
        }
        catch (JsonException)
        {
            return new Coaching("Coach output was not valid JSON.", Array.Empty<string>());
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
                var v = item.GetString();
                if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
            }
        }
        return list;
    }
}
