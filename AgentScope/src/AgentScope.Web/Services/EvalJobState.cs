using AgentScope.Application.Abstractions;
using AgentScope.Application.Evals;

namespace AgentScope.Web.Services;

public enum EvalJobStatus
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Tracked state for one enqueued eval run. Mutable on purpose — the worker updates
/// <see cref="QuestionsDone"/> and <see cref="LatestResult"/> per question while
/// pages read them. Concurrent reads are safe because we only ever overwrite with
/// monotonically newer values (no torn reads of compound state).
/// </summary>
public sealed class EvalJobState
{
    public required string Id { get; init; }
    public required string Variant { get; init; }
    public required OrchestratorConfig Config { get; init; }
    public required string QuestionSetPath { get; init; }
    public required string OutputFileName { get; init; }
    public required string OutputPath { get; init; }
    public required DateTime EnqueuedAt { get; init; }

    public EvalJobStatus Status { get; set; } = EvalJobStatus.Queued;
    public int QuestionsTotal { get; set; }
    public int QuestionsDone { get; set; }
    public EvalResult? LatestResult { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Worker-owned cancellation source for per-job cancel. Null until the job starts.</summary>
    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>
/// Wire-friendly snapshot pushed over SignalR. Mirrors the public fields of
/// <see cref="EvalJobState"/> but stays a record so SignalR's JSON serializer
/// hands the client an immutable shape.
/// </summary>
public sealed record EvalJobSnapshot(
    string Id,
    string Variant,
    string Status,
    string OutputFileName,
    int QuestionsTotal,
    int QuestionsDone,
    DateTime EnqueuedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage)
{
    public static EvalJobSnapshot From(EvalJobState s) => new(
        s.Id,
        s.Variant,
        s.Status.ToString(),
        s.OutputFileName,
        s.QuestionsTotal,
        s.QuestionsDone,
        s.EnqueuedAt,
        s.StartedAt,
        s.CompletedAt,
        s.ErrorMessage);
}
