using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Interview;
using AgentScope.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace AgentScope.Application.Interview;

/// <summary>
/// Drives an interview session across multiple user turns. The web app owns the
/// <see cref="InterviewSession"/> state in its component scope and calls methods on
/// this use case to advance the session:
///
///   1. <see cref="StartAsync"/> — generate question, append to transcript
///   2. (user provides answer; web app calls <see cref="SubmitAnswerAsync"/>)
///      → 0-2 probe rounds (probe agent decides each time)
///   3. <see cref="FinalizeAsync"/> — grade + coach, persist
///
/// Each call publishes the same agent events the research orchestrator does, so the
/// Web UI's existing SignalR/event-log machinery just works.
/// </summary>
public sealed class InterviewSessionUseCase
{
    private const int MaxProbes = 2;
    public const int MaxHints = 2;
    public const int DefaultQuickCheckBatchSize = 5;

    private readonly IInterviewerAgent _interviewer;
    private readonly IProbeAgent _probe;
    private readonly IHintAgent _hint;
    private readonly IModelAnswerAgent _modelAnswer;
    private readonly IQuickCheckAgent _quickCheck;
    private readonly IGraderAgent _grader;
    private readonly ICoachAgent _coach;
    private readonly IAgentEventBus _bus;
    private readonly ILogger<InterviewSessionUseCase> _logger;

    public InterviewSessionUseCase(
        IInterviewerAgent interviewer,
        IProbeAgent probe,
        IHintAgent hint,
        IModelAnswerAgent modelAnswer,
        IQuickCheckAgent quickCheck,
        IGraderAgent grader,
        ICoachAgent coach,
        IAgentEventBus bus,
        ILogger<InterviewSessionUseCase> logger)
    {
        _interviewer = interviewer;
        _probe = probe;
        _hint = hint;
        _modelAnswer = modelAnswer;
        _quickCheck = quickCheck;
        _grader = grader;
        _coach = coach;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>Counts hints already asked in the session. Used by the UI to disable the hint button at the cap.</summary>
    public static int HintsUsed(InterviewSession session) =>
        session.Transcript.Count(t => t.Speaker == Speaker.Hint);

    /// <summary>
    /// Creates a new session and generates the opening question. Returns the session
    /// + the RunId used for event-bus subscription so the caller can stream events.
    /// </summary>
    public async Task<InterviewSession> StartAsync(
        InterviewTopic topic, RunId runId, CancellationToken ct = default)
    {
        var session = new InterviewSession(Guid.NewGuid().ToString("N"), topic);

        var (question, _) = await _interviewer.AskAsync(topic, runId, ct);
        session.Transcript.Add(new InterviewTurn(Speaker.Interviewer, question, DateTime.UtcNow));

        return session;
    }

    /// <summary>
    /// Appends the user's answer and consults the probe agent. Returns the probe text
    /// when one was asked (caller should display it and call this method again with
    /// the next answer), or null when no probe is needed (caller advances to <see cref="FinalizeAsync"/>).
    /// Hard-capped at <see cref="MaxProbes"/> probes per session — once reached, returns null
    /// regardless of what the probe agent says.
    /// </summary>
    public async Task<string?> SubmitAnswerAsync(
        InterviewSession session, string answer, RunId runId, CancellationToken ct = default)
    {
        session.Transcript.Add(new InterviewTurn(Speaker.User, answer, DateTime.UtcNow));

        var probesAskedSoFar = session.Transcript.Count(t => t.Speaker == Speaker.Probe);
        if (probesAskedSoFar >= MaxProbes) return null;

        var (probe, _) = await _probe.ConsiderProbeAsync(session, runId, ct);
        if (string.IsNullOrWhiteSpace(probe)) return null;

        session.Transcript.Add(new InterviewTurn(Speaker.Probe, probe, DateTime.UtcNow));
        return probe;
    }

    /// <summary>
    /// Requests a hint mid-session. Returns the hint text and appends a <see cref="Speaker.Hint"/>
    /// turn to the transcript so the grader sees the candidate needed help. Returns null
    /// when the hint cap (<see cref="MaxHints"/>) has been reached.
    /// </summary>
    public async Task<string?> RequestHintAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        if (HintsUsed(session) >= MaxHints) return null;

        var (hint, _) = await _hint.HintAsync(session, runId, ct);
        if (string.IsNullOrWhiteSpace(hint)) return null;

        session.Transcript.Add(new InterviewTurn(Speaker.Hint, hint, DateTime.UtcNow));
        return hint;
    }

    /// <summary>
    /// Starts a QuickCheck (MCQ) session and generates the first batch of
    /// <paramref name="batchSize"/> questions. The size is stored on the session, so
    /// <see cref="NextQuickCheckBatchAsync"/> reuses it without the caller passing it again.
    /// </summary>
    public async Task<InterviewSession> StartQuickCheckAsync(
        InterviewTopic topic, RunId runId, int batchSize = DefaultQuickCheckBatchSize, CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 10);
        var session = new InterviewSession(Guid.NewGuid().ToString("N"), topic, InterviewMode.QuickCheck);
        session.BatchSize = batchSize;
        var (questions, _) = await _quickCheck.GenerateBatchAsync(topic, batchSize, runId, ct);
        session.Questions = questions;
        ResetPicksForBatch(session);
        return session;
    }

    /// <summary>
    /// Generates the NEXT batch of MCQs on the same topic, reusing the batch size stored
    /// on the session. Resets per-question picks and grades; keeps the session ID so
    /// multi-batch drills stay correlated in Past Runs.
    /// </summary>
    public async Task NextQuickCheckBatchAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        if (session.Mode != InterviewMode.QuickCheck) return;

        var (questions, _) = await _quickCheck.GenerateBatchAsync(session.Topic, session.BatchSize, runId, ct);
        session.Questions = questions;
        session.Grades = null;
        session.BatchSubmitted = false;
        session.FinalGrade = null;
        session.FinalCoaching = null;
        ResetPicksForBatch(session);
    }

    /// <summary>
    /// Grades a whole batch of MCQ answers. <paramref name="picksPerQuestion"/> must be
    /// parallel-indexed to <see cref="InterviewSession.Questions"/>; missing entries are
    /// treated as no-pick (score 1 / wrong). Each question gets a 1-5 score (F1 mapped);
    /// the batch's <see cref="InterviewSession.FinalGrade"/> is the rounded mean.
    /// </summary>
    public async Task SubmitQuickCheckBatchAsync(
        InterviewSession session,
        IReadOnlyList<IReadOnlyList<string>> picksPerQuestion,
        RunId runId,
        CancellationToken ct = default)
    {
        if (session.Questions.Count == 0) return;

        var grades = new List<int>(session.Questions.Count);
        var aggregatedGaps = new List<string>();
        var aggregatedStrengths = new List<string>();

        for (var i = 0; i < session.Questions.Count; i++)
        {
            var q = session.Questions[i];
            var picks = i < picksPerQuestion.Count ? picksPerQuestion[i].ToHashSet() : new HashSet<string>();
            var correct = q.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToHashSet();

            var score = ScoreOne(picks, correct);
            grades.Add(score);

            if (score == 5)
                aggregatedStrengths.Add($"Q{i + 1}: correct ({string.Join(", ", correct.OrderBy(c => c).Select(c => c.ToUpperInvariant()))}).");
            else
                aggregatedGaps.AddRange(BuildGaps(q, correct, picks).Select(g => $"Q{i + 1}: {g}"));
        }

        session.Grades = grades;
        session.BatchSubmitted = true;

        var averageScore = (int)Math.Round(grades.Average());
        session.FinalGrade = new Grade(averageScore, aggregatedStrengths, aggregatedGaps);
        session.FinalCoaching = new Coaching(
            Summary: $"Batch average: {grades.Average():F1}/5 across {grades.Count} questions.",
            SuggestedReading: session.Questions.SelectMany(q => q.Citations).Distinct().ToList());

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, AgentId.System, session.FinalCoaching.Summary,
            TokensIn: 0, TokensOut: 0, EstimatedCostUsd: null,
            DateTime.UtcNow), ct);
    }

    private static void ResetPicksForBatch(InterviewSession session)
    {
        session.Picks.Clear();
        for (var i = 0; i < session.Questions.Count; i++)
            session.Picks.Add(new HashSet<string>());
    }

    private static int ScoreOne(HashSet<string> picked, HashSet<string> correct)
    {
        var tp = picked.Intersect(correct).Count();
        var fp = picked.Except(correct).Count();
        var fn = correct.Except(picked).Count();

        var precision = (tp + fp) == 0 ? 0.0 : (double)tp / (tp + fp);
        var recall = (tp + fn) == 0 ? 0.0 : (double)tp / (tp + fn);
        var f1 = (precision + recall) == 0 ? 0.0 : 2 * precision * recall / (precision + recall);

        return f1 switch
        {
            >= 0.95 => 5,
            >= 0.70 => 4,
            >= 0.50 => 3,
            >= 0.30 => 2,
            _       => 1
        };
    }

    private static IReadOnlyList<string> BuildGaps(
        MultipleChoiceQuestion q,
        IReadOnlySet<string> correct,
        IReadOnlySet<string> picked)
    {
        var gaps = new List<string>();
        var missed = correct.Except(picked).ToList();
        var wrong = picked.Except(correct).ToList();
        foreach (var id in missed)
        {
            var opt = q.Options.FirstOrDefault(o => o.Id == id);
            if (opt is not null) gaps.Add($"missed ({id.ToUpperInvariant()}): {opt.Text}");
        }
        foreach (var id in wrong)
        {
            var opt = q.Options.FirstOrDefault(o => o.Id == id);
            if (opt is not null) gaps.Add($"picked wrong ({id.ToUpperInvariant()}): {opt.Text}");
        }
        return gaps;
    }

    /// <summary>
    /// Candidate gave up. Produces the canonical model answer (RAG-grounded) so they
    /// can compare/study, then finalises the session with a forced score of 0. Skips
    /// the grader call — the score is fixed, no point spending tokens grading what was
    /// effectively skipped — but still runs the coach so the candidate gets study
    /// suggestions for next time.
    /// </summary>
    public async Task ShowAnswerAndFinalizeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var (modelAnswer, modelUsage) = await _modelAnswer.AnswerAsync(session, runId, ct);
        session.Transcript.Add(new InterviewTurn(Speaker.ModelAnswer, modelAnswer, DateTime.UtcNow));

        // Force a "gave up" grade. The coach reads this + the transcript and produces
        // study suggestions that target the gap.
        var gaveUpGrade = new Grade(
            Score: 0,
            Strengths: Array.Empty<string>(),
            Gaps: new[] { "Candidate requested the model answer rather than working through the question." });
        session.FinalGrade = gaveUpGrade;

        var (coaching, coachUsage) = await _coach.CoachAsync(session, gaveUpGrade, runId, ct);
        session.FinalCoaching = coaching;

        var totalUsage = modelUsage.Add(coachUsage);

        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, AgentId.System, coaching.Summary,
            totalUsage.TokensIn, totalUsage.TokensOut, totalUsage.CostUsd,
            DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Runs the grader + coach and finalises the session. Publishes a system-level
    /// <see cref="AgentFinishedEvent"/> terminating the event stream, so the Web app
    /// can clean up its subscription the same way it does for research runs.
    /// </summary>
    public async Task FinalizeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default)
    {
        var (grade, gradeUsage) = await _grader.GradeAsync(session, runId, ct);
        session.FinalGrade = grade;

        var (coaching, coachUsage) = await _coach.CoachAsync(session, grade, runId, ct);
        session.FinalCoaching = coaching;

        var totalUsage = gradeUsage.Add(coachUsage);

        // Terminal event — closes the event stream subscription on the caller's side.
        // FinalText carries the coach summary; the full session payload is held by the
        // caller and persisted there.
        await _bus.PublishAsync(new AgentFinishedEvent(
            runId, AgentId.System, coaching.Summary,
            totalUsage.TokensIn, totalUsage.TokensOut, totalUsage.CostUsd,
            DateTime.UtcNow), ct);
    }
}
