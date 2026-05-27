using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AgentScope.Web.Services;

public sealed record EnqueueEvalRequest(
    string Variant,
    OrchestratorConfig Config,
    string QuestionSetPath);

/// <summary>
/// In-process queue of eval jobs, drained by <see cref="EvalWorker"/>. Singleton.
///
/// Retention: the last <see cref="MaxRetainedJobs"/> completed jobs stay in
/// <see cref="Snapshot"/> so the UI can show recently finished runs alongside
/// active ones. Older completions are evicted (the JSONL files themselves are
/// durable — eviction is purely about in-memory state).
/// </summary>
public sealed class EvalQueue
{
    private const int MaxRetainedJobs = 50;

    private readonly Channel<EvalJobState> _channel = Channel.CreateUnbounded<EvalJobState>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, EvalJobState> _jobs = new();
    private readonly ConcurrentQueue<string> _completionOrder = new();
    private readonly AgentScopeOptions _options;

    public EvalQueue(IOptions<AgentScopeOptions> options)
    {
        _options = options.Value;
    }

    internal ChannelReader<EvalJobState> Reader => _channel.Reader;

    public EvalJobState Enqueue(EnqueueEvalRequest request)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{Sanitize(request.Variant)}-{stamp}.jsonl";
        var outputDir = RepoPath.Resolve(_options.Evals.ResultsDirectory);
        var outputPath = Path.Combine(outputDir, fileName);

        var state = new EvalJobState
        {
            Id = id,
            Variant = request.Variant,
            Config = request.Config,
            QuestionSetPath = request.QuestionSetPath,
            OutputFileName = fileName,
            OutputPath = outputPath,
            EnqueuedAt = DateTime.UtcNow
        };

        _jobs[id] = state;
        // Channel is unbounded so TryWrite always succeeds.
        _channel.Writer.TryWrite(state);
        return state;
    }

    public EvalJobState? TryGet(string id) => _jobs.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Cancels a queued or running job. Returns true if the job was found and a cancel
    /// was signalled; false if the job is unknown or already terminal.
    /// </summary>
    public bool TryCancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var state)) return false;
        if (state.Status is EvalJobStatus.Completed or EvalJobStatus.Cancelled or EvalJobStatus.Failed)
            return false;

        try
        {
            state.Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Worker already finished cleanup — fine, race is harmless.
        }

        // If the job hadn't started yet, the worker won't see a cancel via the CTS
        // (there isn't one yet). Mark it cancelled here so the worker skips it on dequeue.
        if (state.Status == EvalJobStatus.Queued)
        {
            state.Status = EvalJobStatus.Cancelled;
            state.CompletedAt = DateTime.UtcNow;
            MarkCompleted(state);
        }

        return true;
    }

    public IReadOnlyList<EvalJobSnapshot> Snapshot() =>
        _jobs.Values
            .OrderByDescending(j => j.EnqueuedAt)
            .Select(EvalJobSnapshot.From)
            .ToList();

    /// <summary>
    /// Look up the job (if any) currently producing a file at <paramref name="outputPath"/>.
    /// Used by the Past Runs viewer to render a "Running…" badge.
    /// </summary>
    public EvalJobSnapshot? FindByOutputPath(string outputPath)
    {
        // Compare normalized paths to avoid case/separator drift on Windows.
        var normalized = Path.GetFullPath(outputPath);
        var match = _jobs.Values.FirstOrDefault(j =>
            string.Equals(Path.GetFullPath(j.OutputPath), normalized, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : EvalJobSnapshot.From(match);
    }

    /// <summary>
    /// Called by the worker when a job reaches a terminal state. Evicts the oldest
    /// completed job once we exceed <see cref="MaxRetainedJobs"/>.
    /// </summary>
    internal void MarkCompleted(EvalJobState state)
    {
        _completionOrder.Enqueue(state.Id);
        while (_completionOrder.Count > MaxRetainedJobs && _completionOrder.TryDequeue(out var evictId))
        {
            _jobs.TryRemove(evictId, out _);
        }
    }

    private static string Sanitize(string raw)
    {
        var safe = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "eval" : safe;
    }
}
