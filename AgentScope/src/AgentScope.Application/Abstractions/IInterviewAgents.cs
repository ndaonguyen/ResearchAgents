using AgentScope.Domain.Agents;
using AgentScope.Domain.Interview;
using AgentScope.Domain.Runs;

namespace AgentScope.Application.Abstractions;

/// <summary>
/// Generates a realistic interview question on the given topic. Implementations should
/// ground the question in the system-design corpus via tool-calling so the question
/// matches what the books actually cover.
/// </summary>
public interface IInterviewerAgent
{
    Task<(string Question, AgentUsage Usage)> AskAsync(
        InterviewTopic topic, RunId runId, CancellationToken ct = default);
}

/// <summary>
/// Reads the transcript so far and decides whether to ask one focused follow-up
/// (returning the probe text) or to pass (returning null). Designed to fire at most
/// twice per session — the orchestrator caps it.
/// </summary>
public interface IProbeAgent
{
    Task<(string? Probe, AgentUsage Usage)> ConsiderProbeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default);
}

/// <summary>
/// Scores the candidate's transcript on a 1-5 scale, producing a list of strengths and
/// gaps. Implementations should call the system-design corpus to ground gaps in
/// specific book content (e.g. "missed write-through vs write-behind, ByteByteGo p. 76").
/// </summary>
public interface IGraderAgent
{
    Task<(Grade Grade, AgentUsage Usage)> GradeAsync(
        InterviewSession session, RunId runId, CancellationToken ct = default);
}

/// <summary>
/// Writes the final coach feedback: prose summary of how the candidate did + a list of
/// suggested reading. Operates on the transcript + grade; does not call tools.
/// </summary>
public interface ICoachAgent
{
    Task<(Coaching Coaching, AgentUsage Usage)> CoachAsync(
        InterviewSession session, Grade grade, RunId runId, CancellationToken ct = default);
}
