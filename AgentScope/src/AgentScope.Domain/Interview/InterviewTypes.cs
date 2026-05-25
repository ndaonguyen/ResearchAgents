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
        new InterviewTopic("transactions-isolation", "Transactions & isolation levels",  TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("two-phase-commit",  "Distributed transactions & two-phase commit", TopicGroup.Concept, InterviewTrack.SystemDesign),
        new InterviewTopic("consensus",         "Consensus (Raft, Paxos basics, ZAB)",    TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("stream-processing", "Stream processing & materialized views", TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("storage-engines",   "Storage engines (B-trees vs LSM-trees)", TopicGroup.Concept,  InterviewTrack.SystemDesign),
        new InterviewTopic("cdc",               "Change Data Capture (CDC)",              TopicGroup.Concept,  InterviewTrack.SystemDesign),

        // -------- System Design — Design exercises --------
        new InterviewTopic("url-shortener",     "Design a URL shortener",                 TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("chat-system",       "Design a chat system",                   TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("news-feed",         "Design a news feed",                     TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("notification",      "Design a notification system",           TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("rate-limiter-svc",  "Design a rate-limiter service",          TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("design-stream-pipeline", "Design a stream-processing pipeline (fraud detection / analytics)", TopicGroup.Exercise, InterviewTrack.SystemDesign),
        new InterviewTopic("isolation-strategy", "Choose isolation level & lock strategy for a high-contention system", TopicGroup.Exercise, InterviewTrack.SystemDesign),

        // -------- Architecture — Concepts --------
        new InterviewTopic("ddd-bounded",       "Bounded contexts & ubiquitous language", TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("micro-vs-mono",     "Microservices vs monolith trade-offs",   TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("arch-ilities",      "Architecture characteristics (\"ilities\")", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("evolutionary",      "Evolutionary architecture & fitness functions", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("arch-styles",       "Architectural styles (layered, hexagonal, event-driven, microkernel)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("service-granularity","Service granularity & decomposition",   TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("aggregates",        "Aggregates & transactional boundaries (DDD)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("domain-events",     "Domain events & event modeling",         TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("coupling-cohesion", "Coupling & cohesion (static vs dynamic)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("resilience",        "Resilience patterns (circuit breaker, retry, bulkhead, timeout)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("api-design",        "API design fundamentals (REST vs GraphQL vs gRPC trade-offs)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("arch-quanta",       "Architectural quanta & deployability boundaries", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("adrs",              "Architecture Decision Records (ADRs)",   TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("event-contracts",   "Event contracts & schema evolution",     TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("ddd-strategic-tactical", "Strategic vs Tactical DDD",         TopicGroup.Concept,  InterviewTrack.Architecture),
        new InterviewTopic("subdomain-types",   "Subdomain types: core / supporting / generic", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("biz-logic-patterns","Business-logic patterns (transaction script, active record, domain model)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("acl-integration",   "Anti-Corruption Layer & integration patterns (Conformist, Customer-Supplier, Open-Host)", TopicGroup.Concept, InterviewTrack.Architecture),
        new InterviewTopic("coupling-microservices", "Information hiding & coupling types in microservices (afferent/efferent, common, content)", TopicGroup.Concept, InterviewTrack.Architecture),

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
        new InterviewTopic("subdomain-classify","Apply subdomain classification to a domain (core/supporting/generic, build-vs-buy)", TopicGroup.Exercise, InterviewTrack.Architecture),
        new InterviewTopic("design-acl",        "Design an Anti-Corruption Layer for a legacy integration", TopicGroup.Exercise, InterviewTrack.Architecture),

        // -------- AI Engineering — Concepts (Huyen) --------
        new InterviewTopic("prompt-eng",        "Prompt engineering fundamentals (zero-shot, few-shot, CoT)", TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("rag-design",        "RAG system design (chunking, retrieval, re-ranking)", TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("llm-eval",          "LLM evaluation strategies (golden sets, LLM-as-judge, A/B)", TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("embeddings",        "Embeddings & vector databases",            TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("inference-cost",    "Inference cost & latency optimisation",    TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("ft-vs-prompt",      "Fine-tuning vs prompting vs RAG — trade-offs", TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("hallucination",     "Hallucination causes & mitigation",        TopicGroup.Concept, InterviewTrack.AiEngineering),
        new InterviewTopic("guardrails",        "Guardrails, safety, and content filtering",TopicGroup.Concept, InterviewTrack.AiEngineering),

        // -------- AI Engineering — Design exercises --------
        new InterviewTopic("design-copilot",    "Design an AI customer-support copilot",    TopicGroup.Exercise, InterviewTrack.AiEngineering),
        new InterviewTopic("design-rag",        "Design a RAG system over internal docs",   TopicGroup.Exercise, InterviewTrack.AiEngineering),
        new InterviewTopic("data-flywheel",     "Build a feedback / data flywheel for an LLM product", TopicGroup.Exercise, InterviewTrack.AiEngineering),
        new InterviewTopic("eval-framework",    "Design an LLM eval framework for production", TopicGroup.Exercise, InterviewTrack.AiEngineering),
        new InterviewTopic("cost-optimize",     "Cost-optimise an LLM-heavy system (caching, routing, batching)", TopicGroup.Exercise, InterviewTrack.AiEngineering),

        // -------- Testing — Concepts (Khorikov) --------
        new InterviewTopic("test-pyramid",      "Test pyramid & boundaries (unit / integration / e2e)", TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("test-doubles",      "Test doubles (mock, stub, spy, fake) — when to use which", TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("test-schools",      "London vs Detroit (Chicago) school of TDD", TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("four-pillars",      "What makes a test valuable (Khorikov's 4 pillars)", TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("testability",       "Designing for testability",                TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("test-smells",       "Test smells (brittle, slow, opaque, flaky)", TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("mutation-testing",  "Mutation testing",                         TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("property-based",    "Property-based testing",                   TopicGroup.Concept, InterviewTrack.Testing),
        new InterviewTopic("contract-testing",  "Contract testing & consumer-driven contracts (Pact)", TopicGroup.Concept, InterviewTrack.Testing),

        // -------- Testing — Design exercises --------
        new InterviewTopic("make-testable",     "Make a hard-to-test class testable",       TopicGroup.Exercise, InterviewTrack.Testing),
        new InterviewTopic("mocking-strategy",  "Choose a mocking strategy for a service-with-many-deps", TopicGroup.Exercise, InterviewTrack.Testing),
        new InterviewTopic("test-plan",         "Design a test plan for a new feature",     TopicGroup.Exercise, InterviewTrack.Testing),
        new InterviewTopic("integration-strategy","Plan an integration-test strategy for a microservices system", TopicGroup.Exercise, InterviewTrack.Testing),
        new InterviewTopic("design-contract-tests", "Set up consumer-driven contracts between two services", TopicGroup.Exercise, InterviewTrack.Testing),

        // -------- Code Craft — Concepts (Good Code/Bad Code + FP in C#) --------
        new InterviewTopic("module-api",        "Module / API design at code level",        TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("encapsulation",     "Encapsulation & abstraction levels",       TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("error-handling",    "Error handling strategies (exceptions, results, monads)", TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("naming",            "Naming & code clarity",                    TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("defensive-decl",    "Defensive vs declarative programming",     TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("immutability",      "Immutability & pure functions",            TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("composition",       "Composition over inheritance",             TopicGroup.Concept, InterviewTrack.CodeCraft),
        new InterviewTopic("code-smells",       "Code smells",                              TopicGroup.Concept, InterviewTrack.CodeCraft),

        // -------- Code Craft — Design exercises --------
        new InterviewTopic("refactor-coupled",  "Refactor a deeply-coupled class",          TopicGroup.Exercise, InterviewTrack.CodeCraft),
        new InterviewTopic("error-strategy",    "Design an error-handling strategy for a public API", TopicGroup.Exercise, InterviewTrack.CodeCraft),
        new InterviewTopic("functional-pipeline","Refactor imperative code into a functional pipeline", TopicGroup.Exercise, InterviewTrack.CodeCraft),
        new InterviewTopic("value-objects",     "Design a value-object library for a domain", TopicGroup.Exercise, InterviewTrack.CodeCraft),
    };

    public static InterviewTopic? FindById(string id) =>
        All.FirstOrDefault(t => t.Id == id);
}

public sealed record InterviewTopic(string Id, string DisplayName, TopicGroup Group, InterviewTrack Track);

public enum TopicGroup { Concept, Exercise }

public enum InterviewTrack { SystemDesign, Architecture, AiEngineering, Testing, CodeCraft }

/// <summary>
/// Maps each track to its kernel-plugin name. Used by the interview agents when
/// constructing user messages so the LLM is told exactly which corpus to search.
/// Keep these in sync with the <c>PluginName</c> values in <c>appsettings:Corpora[]</c>.
/// </summary>
public static class InterviewTrackCorpus
{
    public static string CorpusPluginName(InterviewTrack track) => track switch
    {
        InterviewTrack.Architecture   => "ArchitectureCorpus",
        InterviewTrack.SystemDesign   => "SystemDesignCorpus",
        InterviewTrack.AiEngineering  => "AiEngineeringCorpus",
        InterviewTrack.Testing        => "TestingCorpus",
        InterviewTrack.CodeCraft      => "CodeCraftCorpus",
        _ => "SystemDesignCorpus"
    };

    public static string DisplayName(InterviewTrack track) => track switch
    {
        InterviewTrack.SystemDesign   => "System Design",
        InterviewTrack.Architecture   => "Architecture",
        InterviewTrack.AiEngineering  => "AI Engineering",
        InterviewTrack.Testing        => "Testing",
        InterviewTrack.CodeCraft      => "Code Craft",
        _ => track.ToString()
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
