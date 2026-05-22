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

    private readonly IInterviewerAgent _interviewer;
    private readonly IProbeAgent _probe;
    private readonly IGraderAgent _grader;
    private readonly ICoachAgent _coach;
    private readonly IAgentEventBus _bus;
    private readonly ILogger<InterviewSessionUseCase> _logger;

    public InterviewSessionUseCase(
        IInterviewerAgent interviewer,
        IProbeAgent probe,
        IGraderAgent grader,
        ICoachAgent coach,
        IAgentEventBus bus,
        ILogger<InterviewSessionUseCase> logger)
    {
        _interviewer = interviewer;
        _probe = probe;
        _grader = grader;
        _coach = coach;
        _bus = bus;
        _logger = logger;
    }

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
