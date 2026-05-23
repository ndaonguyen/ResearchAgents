namespace AgentScope.Domain.Interview;

/// <summary>
/// Curated list of interview topics, split across two tracks:
/// <list type="bullet">
///   <item><see cref="InterviewTrack.SystemDesign"/> — drawn from ByteByteGo + Alex Xu's <c>System Design Interview</c>.</item>
///   <item><see cref="InterviewTrack.Architecture"/> — drawn from DDD Distilled, Hard Parts, Microservices Patterns, Evolutionary Architectures, Monolith to Microservices.</item>
/// </list>
/// Within each track, topics are grouped into Concepts (building blocks) and Exercises
/// (worked design problems) for UI organisation. The agents read <see cref="InterviewTopic.Track"/>
/// to decide which RAG corpus (<c>ArchitectureCorpus</c> vs <c>SystemDesignCorpus</c>) to ground in.
/// </summary>
public static class InterviewTopics
{
    public static IReadOnlyList<InterviewTopic> All { get; } = new[]
    {
        // -------- System Design — Concepts --------
        new InterviewTopic("caching",           "Distributed caching",                    TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("sharding",          "Database sharding & partitioning",       TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("replication",       "Replication & consistency models",       TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("load-balancing",    "Load balancing",                         TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("queues",            "Message queues & async processing",      TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("rate-limiting",     "Rate limiting",                          TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("consistent-hash",   "Consistent hashing",                     TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("capacity",          "Capacity estimation & back-of-envelope", TopicGroup.Concept,  InterviewTrack.SystemDesign),

        // -------- System Design — Design exercises --------
        new InterviewTopic("url-shortener",     "Design a URL shortener",                 TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("chat-system",       "Design a chat system",                   TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("news-feed",         "Design a news feed",                     TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("notification",      "Design a notification system",           TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("rate-limiter-svc",  "Design a rate-limiter service",          TopicGroup.Exercise, InterviewTrack.SystemDesign),

        // -------- Architecture — Concepts --------
        new InterviewTopic("ddd-bounded",       "Bounded contexts & ubiquitous language", TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("micro-vs-mono",     "Microservices vs monolith trade-offs",   TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("arch-ilities",      "Architecture characteristics (\"ilities\")", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("evolutionary",      "Evolutionary architecture & fitness functions", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("arch-styles",       "Architectural styles (layered, hexagonal, event-driven, microkernel)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("service-granularity","Service granularity & decomposition",   TopicGroup.Concept,  InterviewTrack.Architecture),

        // -------- Architecture — Design exercises --------
        new InterviewTopic("decompose-mono",    "Decompose a monolith into services",     TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("apply-ddd",         "Apply DDD to a domain (e.g. e-commerce)",TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("strangler-fig",     "Plan a strangler-fig migration",         TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("saga-design",       "Design a distributed saga (orchestration vs choreography)", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("api-gateway",       "Design an API gateway / Backend-for-frontend", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("cqrs-design",       "Apply CQRS to a high-read, low-write system", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("event-sourcing",    "Design event sourcing for an audit-heavy domain", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("context-mapping",   "Map bounded contexts and integrations across a domain", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("observability",     "Design observability for a microservices system", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("fitness-funcs",     "Define fitness functions for a quality attribute (security, scalability, …)", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("service-comms",     "Design service-to-service communication (sync vs async, contracts, versioning)", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("data-ownership",    "Choose a data ownership model (shared DB, DB-per-service, data mesh)", TopicGroup.Exercise, InterviewTrack.Architecture),
    };

    public static InterviewTopic? FindById(string id) =>
        All.FirstOrDefault(t => t.Id == id);
}

public sealed record InterviewTopic(string Id, string DisplayName, TopicGroup Group, InterviewTrack Track);

public enum TopicGroup { Concept, Exercise }

public enum InterviewTrack { SystemDesign, Architecture }

/// <summary>
/// Maps each track to its kernel-plugin name. Used by the interview agents when
/// constructing user messages so the LLM is told exactly which corpus to search.
/// Keep these in sync with the <c>PluginName</c> values in <c>appsettings:Corpora[]</c>.
/// </summary>
public static class InterviewTrackCorpus
{
    public static string CorpusPluginName(InterviewTrack track) => track switch
    {
        InterviewTrack.Architecture => "ArchitectureCorpus",
        InterviewTrack.SystemDesign => "SystemDesignCorpus",
        _ => "SystemDesignCorpus"
    };
}

/// <summary>
/// Two question formats. <see cref="Discussion"/> is the full multi-turn interview with
/// probes/hints/grader/coach. <see cref="QuickCheck"/> is a single multiple-choice
/// question — same RAG grounding, no conversation, instant pass/fail-style score.
/// </summary>
public enum InterviewMode { Discussion, QuickCheck }

/// <summary>
/// The full state of one interview session. <see cref="Mode"/> decides which fields are used:
/// <list type="bullet">
///   <item>Discussion → <see cref="Transcript"/>, <see cref="FinalGrade"/>, <see cref="FinalCoaching"/></item>
///   <item>QuickCheck → <see cref="Question"/>, <see cref="Result"/>, <see cref="FinalGrade"/> (1-5 derived from picks)</item>
/// </list>
/// </summary>
public sealed class InterviewSession
{
    public string SessionId { get; }
    public InterviewTopic Topic { get; }
    public InterviewMode Mode { get; }
    public DateTime StartedAtUtc { get; }
    public List<InterviewTurn> Transcript { get; } = new();
    public Grade? FinalGrade { get; set; }
    public Coaching? FinalCoaching { get; set; }

    // QuickCheck-only: a batch of MCQs and the user's per-question picks + grading outcomes.
    // Indexes are parallel: Questions[i] ↔ Picks[i] ↔ Grades[i] (Grades is null until BatchSubmitted).
    public IReadOnlyList<MultipleChoiceQuestion> Questions { get; set; } = Array.Empty<MultipleChoiceQuestion>();
    public List<HashSet<string>> Picks { get; } = new();
    public List<int>? Grades { get; set; }
    public bool BatchSubmitted { get; set; }

    /// <summary>How many questions per batch. Locked when the session starts; "Next batch" reuses this value.</summary>
    public int BatchSize { get; set; } = 5;

    // Running totals across the whole session — accumulated by the use case after every
    // agent call (interviewer, probe, hint, grader, coach, model-answer, quickcheck).
    // Null cost means at least one call returned an unknown model price; treat downstream
    // as "approximate."
    public int TotalTokensIn { get; set; }
    public int TotalTokensOut { get; set; }
    public decimal? TotalCostUsd { get; set; }

    // QuickCheck-only: most recent batch's usage, so the UI can attribute cost per question
    // when it persists each MCQ as its own EvalRow (cost / batch-size, evenly split).
    public int LastBatchTokensIn { get; set; }
    public int LastBatchTokensOut { get; set; }
    public decimal? LastBatchCostUsd { get; set; }

    public InterviewSession(string sessionId, InterviewTopic topic, InterviewMode mode = InterviewMode.Discussion)
    {
        SessionId = sessionId;
        Topic = topic;
        Mode = mode;
        StartedAtUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// One message in the transcript. <see cref="Speaker"/> distinguishes interviewer/probe
/// content from the user's answers, which matters for the grader prompt.
/// </summary>
public sealed record InterviewTurn(Speaker Speaker, string Text, DateTime Timestamp);

public enum Speaker { Interviewer, User, Probe, Hint, ModelAnswer }

public sealed record Grade(int Score, IReadOnlyList<string> Strengths, IReadOnlyList<string> Gaps);

public sealed record Coaching(string Summary, IReadOnlyList<string> SuggestedReading);

/// <summary>
/// One MCQ option. <see cref="Id"/> is a short stable handle (e.g. "a", "b", "c", "d")
/// so the UI can render checkboxes/radios and post back exactly which options were picked.
/// </summary>
public sealed record MultipleChoiceOption(string Id, string Text, bool IsCorrect);

/// <summary>
/// A complete MCQ with one or more correct options. When <see cref="CorrectCount"/> is 1,
/// the UI renders radio buttons; otherwise checkboxes (multi-select).
/// </summary>
public sealed record MultipleChoiceQuestion(
    string Question,
    IReadOnlyList<MultipleChoiceOption> Options,
    string Explanation,
    IReadOnlyList<string> Citations)
{
    public int CorrectCount => Options.Count(o => o.IsCorrect);
}

/// <summary>
/// The candidate's submission + grading outcome for an MCQ.
/// </summary>
public sealed record ChoiceResult(
    IReadOnlyList<string> SelectedIds,
    IReadOnlyList<string> CorrectIds,
    int Score);
