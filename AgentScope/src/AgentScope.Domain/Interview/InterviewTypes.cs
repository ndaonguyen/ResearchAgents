namespace AgentScope.Domain.Interview;

/// <summary>
/// Curated list of system-design interview topics, derived from chapter coverage in
/// ByteByteGo and Alex Xu's <c>System Design Interview</c>. Topics are split into
/// "Concepts" (canonical building blocks) and "Design exercises" (worked end-to-end
/// system designs) so the UI can group them.
/// </summary>
public static class InterviewTopics
{
    public static IReadOnlyList<InterviewTopic> All { get; } = new[]
    {
        // Concepts
        new InterviewTopic("caching",           "Distributed caching",                  TopicGroup.Concept),
        new InterviewTopic("sharding",          "Database sharding & partitioning",     TopicGroup.Concept),
        new InterviewTopic("replication",       "Replication & consistency models",     TopicGroup.Concept),
        new InterviewTopic("load-balancing",    "Load balancing",                       TopicGroup.Concept),
        new InterviewTopic("queues",            "Message queues & async processing",    TopicGroup.Concept),
        new InterviewTopic("rate-limiting",     "Rate limiting",                        TopicGroup.Concept),
        new InterviewTopic("consistent-hash",   "Consistent hashing",                   TopicGroup.Concept),
        new InterviewTopic("capacity",          "Capacity estimation & back-of-envelope", TopicGroup.Concept),

        // Design exercises
        new InterviewTopic("url-shortener",     "Design a URL shortener",               TopicGroup.Exercise),
        new InterviewTopic("chat-system",       "Design a chat system",                 TopicGroup.Exercise),
        new InterviewTopic("news-feed",         "Design a news feed",                   TopicGroup.Exercise),
        new InterviewTopic("notification",      "Design a notification system",         TopicGroup.Exercise),
        new InterviewTopic("rate-limiter-svc",  "Design a rate-limiter service",        TopicGroup.Exercise),
    };

    public static InterviewTopic? FindById(string id) =>
        All.FirstOrDefault(t => t.Id == id);
}

public sealed record InterviewTopic(string Id, string DisplayName, TopicGroup Group);

public enum TopicGroup { Concept, Exercise }

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

    // QuickCheck-only.
    public MultipleChoiceQuestion? Question { get; set; }
    public ChoiceResult? Result { get; set; }

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
