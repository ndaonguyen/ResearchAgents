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

/// <summary>
/// Per-<see cref="TopicGroup"/> steer for the "Practical" content the model produces.
/// Concept topics want the practical anchor to be a small code/library example;
/// Exercise topics want named tools wired together with concrete numbers and a
/// real-system reference. Callers append this to the user message so the system
/// prompt itself stays group-agnostic.
/// </summary>
internal static class TopicGroupHints
{
    public static string PracticalSteer(TopicGroup group) => group switch
    {
        TopicGroup.Concept =>
            "Group hint: this is a CONCEPT topic — the practical content should anchor the idea " +
            "in code. Prioritise: a short snippet using a specific library/API, the API signature " +
            "or config key that matters, and one named real-world anti-pattern to avoid.",
        TopicGroup.Exercise =>
            "Group hint: this is an EXERCISE (worked design problem). The practical content IS the " +
            "core of this answer — go deep, and let it run significantly longer than the theory. " +
            "Cover, in order:\n" +
            "  1. Architecture sketch — every service/component named, how they wire together, " +
            "     which links are sync vs async, who owns the data. A pseudo-diagram in text or a " +
            "     numbered flow is fine.\n" +
            "  2. Concrete technology choices, each justified against the stated constraint " +
            "     (e.g. \"Kafka because we need durable replay across 3 AZs at 50k msg/s\", not " +
            "     \"a message broker\").\n" +
            "  3. Data design — schema or event payload sketches in a fenced code block, " +
            "     partition keys, indexing strategy, consistency model. Show the shape.\n" +
            "  4. Concrete numbers tied to the scale in the question: p50/p99 latency budgets in " +
            "     ms, RPS, partition counts, replication factor, retention windows, cache hit-rate " +
            "     targets. Not \"low latency\" — actual numbers.\n" +
            "  5. Failure modes & compensation — which steps can fail, how each is retried or " +
            "     compensated, what idempotency keys / dedup / outbox mechanisms protect against " +
            "     double-effects.\n" +
            "  6. One named real production system or public post-mortem that solved a similar " +
            "     problem (Stripe, Shopify, Uber Eng blog, Discord, Netflix, AWS post-mortem, " +
            "     etc.) — what they learned, not just that they did it.\n" +
            "  7. Two trade-offs you explicitly chose against, with a one-line reason each.\n" +
            "Use sub-headings (### …) so the candidate can scan it. Code/schema snippets are " +
            "encouraged; a single snippet up to ~30 lines is fine. The Practical section can " +
            "comfortably be 8-14 short paragraphs for a design exercise.",
        _ => ""
    };
}

public sealed class InterviewerAgent : IInterviewerAgent
{
    private const string SystemPrompt = """
        You are a senior engineering interviewer. Generate ONE realistic interview
        question for the given topic.

        Rules:
        - Use the corpus tool named in the user message (one of
          SystemDesignCorpus.Search or ArchitectureCorpus.Search) FIRST to ground the
          question in what the books actually cover. Do not call other corpora.
        - Style and depth should match the source: SystemDesign topics produce
          interview-style design questions; Architecture topics produce open-ended
          architectural reasoning questions about patterns, trade-offs, and decomposition.
        - The question should be open-ended enough to test depth, but specific enough
          that a candidate can begin reasoning about it within a minute.
        - Include explicit scale/constraints when the topic warrants them (e.g.
          "handle 1M requests/sec with p99 < 100ms" for system-design topics; "for a
          150-engineer e-commerce platform" for architecture topics).
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

        var corpus = InterviewTrackCorpus.CorpusPluginName(topic.Track);
        var userMessage = new ChatMessageContent(AuthorRole.User,
            $"Topic: {topic.DisplayName} (track: {topic.Track})\n" +
            $"Use {corpus}.Search to ground the question.\n\n" +
            "Generate one interview question on this topic.");

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

public sealed class HintAgent : IHintAgent
{
    private const string SystemPrompt = """
        You are a senior interviewer giving the candidate a SMALL hint to help them
        get unstuck. They have explicitly asked for a hint.

        Rules:
        - Use the corpus tool named in the user message (one of SystemDesignCorpus.Search
          or ArchitectureCorpus.Search) to ground the hint. Cite a book + page if a
          specific chunk is the source.
        - The hint must POINT at the angle to consider, NOT give the answer.
          Examples of good hints:
            "Think about what happens to in-flight writes when the leader fails over."
            "Consider the trade-off between write-through and write-behind caches
             (ByteByteGo, pp. 76-80)."
          Examples of bad hints (DO NOT do this):
            "The answer is consistent hashing because..."
            "Use leader-based replication with synchronous followers."
        - 1-2 sentences. No headings, no bullet lists.
        - Output ONLY the hint text. No commentary, no "Here's a hint:" preamble.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<HintAgent> _logger;

    public HintAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<HintAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(string Hint, AgentUsage Usage)> HintAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.Hint;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Hint", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "Hint",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(functionChoice: FunctionChoiceBehavior.Auto()))
        };

        var corpus = InterviewTrackCorpus.CorpusPluginName(session.Topic.Track);
        var prompt = $"Use {corpus}.Search to ground the hint.\n\n" +
                     ProbeAgent.BuildTranscriptPrompt(session) +
                     "\nThe candidate has asked for a hint. Give them ONE small hint.";
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

        var hint = sb.ToString().Trim();
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, hint, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (hint, usage);
    }
}

public sealed class ModelAnswerAgent : IModelAnswerAgent
{
    private const string SystemPrompt = """
        You are a senior engineer presenting the model answer to an interview question.
        The candidate has given up and wants to see how it should be answered.

        Rules:
        - Use the corpus tool named in the user message (one of SystemDesignCorpus.Search
          or ArchitectureCorpus.Search) to ground the answer in the same books that would
          grade a real attempt. Cite book + page ranges inline, e.g.
          "(ByteByteGo, pp. 76-80)" or "(Software Architecture - The Hard Parts, pp. 47-49)".
        - Structure the answer the way a strong interviewer would: state assumptions
          and rough scale first, then walk through the design decisions with their
          trade-offs, then call out edge cases / things to discuss further.
        - You MUST include a "## Practical / In code" section near the end that grounds
          the theory in something a candidate could actually build. It must contain at
          least THREE of the following, not just generic verbs:
            * Concrete tools/products by name (e.g. Kafka, Redis, Istio, BM25 + a
              cross-encoder re-ranker, pgbouncer, Envoy, OpenTelemetry, Stripe Webhooks).
              Explain *why that specific tool* fits the constraint, not just "use a
              message broker".
            * A short pseudo-code, config, or schema snippet in a fenced code block
              (SQL DDL, a JSON/YAML config fragment, a 5-15 line function, an API
              signature, an event payload — whatever fits the question).
            * A real-world reference: "how Stripe handles idempotency keys", "Shopify's
              cell-based architecture", "Netflix's Hystrix → resilience4j migration",
              "Discord's switch from Cassandra to ScyllaDB", or a named public incident
              / post-mortem. Be specific; vague "large companies do X" does not count.
            * Concrete numbers tied to the constraints: actual p99 budgets in ms,
              partition counts, replication factors, batch sizes, TTLs, retry windows,
              cache hit-rate targets. Not "low latency" — "p99 < 80ms, achieved by
              warming the L1 cache to >95% hit rate".
        - Theory without the Practical section is incomplete. Let the question's depth
          set the lengths — Concept topics typically need a compact Practical section,
          while Exercise (design) topics need it to be the longest part of the answer.
          Do not pad either section. Hard caps: theory ≤ 8 short paragraphs, Practical
          section ≤ 14 short paragraphs, any single code/schema snippet ≤ 30 lines.
        - This is the candidate's learning artifact — be specific and dense.
        - Output ONLY the answer text. No "Here's the model answer:" preamble.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<ModelAnswerAgent> _logger;

    public ModelAnswerAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<ModelAnswerAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(string Answer, AgentUsage Usage)> AnswerAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var agentId = AgentId.ModelAnswer;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Model Answer", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "ModelAnswer",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(functionChoice: FunctionChoiceBehavior.Auto()))
        };

        // The interviewer's original question is the first turn in the transcript.
        var openingQuestion = session.Transcript.FirstOrDefault(t => t.Speaker == Speaker.Interviewer)?.Text
                              ?? $"(unknown question on topic: {session.Topic.DisplayName})";

        var corpus = InterviewTrackCorpus.CorpusPluginName(session.Topic.Track);
        var prompt = $"Topic: {session.Topic.DisplayName} (track: {session.Topic.Track}, group: {session.Topic.Group})\n" +
                     $"Use {corpus}.Search to ground the answer.\n\n" +
                     $"{TopicGroupHints.PracticalSteer(session.Topic.Group)}\n\n" +
                     $"Interview question:\n{openingQuestion}\n\n" +
                     "Produce the model answer.";
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

        var answer = sb.ToString().Trim();
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, answer, usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (answer, usage);
    }
}

public sealed class QuickCheckAgent : IQuickCheckAgent
{
    private const string SystemPrompt = """
        You generate a BATCH of multiple-choice questions on the given topic for a quick
        concept check.

        Rules:
        - The N questions must each cover a DISTINCT angle of the topic. Do not repeat the
          same concept across questions.
        - Use the corpus tool named in the user message (one of SystemDesignCorpus.Search
          or ArchitectureCorpus.Search) to ground each question in what the books actually
          cover. Different questions can cite different chunks. Do not call other corpora.
        - 4 options per question. Mix concrete facts with plausible-but-wrong distractors.
        - Correct count per question is 1 OR more — pick what's appropriate. Some questions
          are clean single-answer ("Which is the PRIMARY purpose of consistent hashing?"),
          others are "select all that apply".
        - Each Explanation must have TWO parts:
            1. "Why" — justify the correct answer(s) AND dismiss the wrong ones,
               citing book + page range.
            2. "In practice" — show what this looks like in real code or in a real
               system. Name a concrete tool, library, or willcompany example (e.g.
               "Redis SETNX for the lock", "Kafka's acks=all + min.insync.replicas=2",
               "how Stripe issues idempotency keys"), optionally with a short fenced
               code/config snippet. Vague advice like "use a database" does not count
               — name the product and say why it fits.
          Let the question's depth set the length; do not pad. Hard cap: ~8 sentences
          per part, and code snippets ~20 lines and maximum ~40 line.
        - Output JSON only:
          {
            "questions": [
              {
                "question": "<question text, include '(Select all that apply.)' if multi-correct>",
                "options": [
                  {"id": "a", "text": "<option text>", "correct": true|false},
                  {"id": "b", "text": "<option text>", "correct": true|false},
                  {"id": "c", "text": "<option text>", "correct": true|false},
                  {"id": "d", "text": "<option text>", "correct": true|false}
                ],
                "explanation": "Why: <2-3 sentences justifying correct + dismissing wrong, with citation>\n\nIn practice: <2-4 sentences with a concrete tool/library/company example, optionally a short fenced code snippet>",
                "citations": ["<Book Name, pp. X-Y>"]
              },
              ... (N total)
            ]
          }
        - At least one option per question must be correct. No commentary outside the JSON.
        """;

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;
    private readonly IKernelFactory _kernelFactory;
    private readonly IUsageCalculator _usageCalculator;
    private readonly string _model;
    private readonly ILogger<QuickCheckAgent> _logger;

    public QuickCheckAgent(
        IAgentEventBus bus,
        AgentRunContext runContext,
        IKernelFactory kernelFactory,
        IUsageCalculator usageCalculator,
        IOptions<AgentScopeOptions> options,
        ILogger<QuickCheckAgent> logger)
    {
        _bus = bus;
        _runContext = runContext;
        _kernelFactory = kernelFactory;
        _usageCalculator = usageCalculator;
        _model = options.Value.OpenAi.Model;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<MultipleChoiceQuestion> Questions, AgentUsage Usage)> GenerateBatchAsync(
        InterviewTopic topic, int count, RunId runId, CancellationToken ct = default)
    {
        if (count < 1) count = 1;
        if (count > 10) count = 10;

        var agentId = AgentId.QuickCheck;
        using var _ = _runContext.Push(runId, agentId);

        await _bus.PublishAsync(new AgentStartedEvent(runId, agentId, "Quick Check", DateTime.UtcNow), ct);

        var kernel = _kernelFactory.Create();
        var agent = new ChatCompletionAgent
        {
            Name = "QuickCheck",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(AgentSettingsBuilder.Build(
                responseFormat: "json_object",
                functionChoice: FunctionChoiceBehavior.Auto()))
        };

        var corpus = InterviewTrackCorpus.CorpusPluginName(topic.Track);
        var userMessage = new ChatMessageContent(AuthorRole.User,
            $"Topic: {topic.DisplayName} (track: {topic.Track}, group: {topic.Group})\n" +
            $"Use {corpus}.Search to ground each question.\n\n" +
            $"{TopicGroupHints.PracticalSteer(topic.Group)}\n" +
            "(Apply the group hint to each question's \"In practice\" section.)\n\n" +
            $"Generate {count} distinct MCQs on this topic. Make sure each covers a different angle.");

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
        var questions = ParseBatch(json);
        var usage = UsageExtractor.TryExtractWithCost(lastMetadata, _model, _usageCalculator)
                    ?? new AgentUsage(0, 0, null);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, agentId, $"{questions.Count} questions generated",
            usage.TokensIn, usage.TokensOut, usage.CostUsd, DateTime.UtcNow), ct);

        return (questions, usage);
    }

    internal static IReadOnlyList<MultipleChoiceQuestion> ParseBatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("questions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<MultipleChoiceQuestion>();

            var list = new List<MultipleChoiceQuestion>(arr.GetArrayLength());
            foreach (var qEl in arr.EnumerateArray())
            {
                if (qEl.ValueKind != JsonValueKind.Object) continue;
                var parsed = ParseQuestion(qEl);
                if (parsed.Options.Count > 0) list.Add(parsed);
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<MultipleChoiceQuestion>();
        }
    }

    private static MultipleChoiceQuestion ParseQuestion(JsonElement root)
    {
        var question = root.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? ""
            : "(question text missing)";

        var options = new List<MultipleChoiceOption>();
        if (root.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? ""
                    : Guid.NewGuid().ToString("N")[..1];
                var text = item.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? ""
                    : "";
                var correct = item.TryGetProperty("correct", out var cEl) && cEl.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrWhiteSpace(text))
                    options.Add(new MultipleChoiceOption(id, text, correct));
            }
        }

        var explanation = root.TryGetProperty("explanation", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";

        var citations = ReadStringArray(root, "citations");

        if (options.Count > 0 && options.All(o => !o.IsCorrect))
        {
            options[0] = options[0] with { IsCorrect = true };
        }

        return new MultipleChoiceQuestion(question, options, explanation, citations);
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

public sealed class GraderAgent : IGraderAgent
{
    private const string SystemPrompt = """
        You are grading a candidate's interview transcript on a 1-5 scale. Use the corpus
        tool named in the user message (one of SystemDesignCorpus.Search or
        ArchitectureCorpus.Search) to look up what canonical answers cover, so your gaps
        are grounded in specific book content (cite the book + page when possible).

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

        var corpus = InterviewTrackCorpus.CorpusPluginName(session.Topic.Track);
        var prompt = $"Use {corpus}.Search to ground gaps.\n\n" +
                     ProbeAgent.BuildTranscriptPrompt(session) +
                     "\nGrade the candidate's performance.";
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
