using System.Text.Json;
using AgentScope.Application.Evals;
using AgentScope.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentScope.Web.Services;

/// <summary>
/// Drains <see cref="EvalQueue"/> one job at a time. Sequential by design — same reason
/// the CLI is sequential: parallel jobs would trip rate limits and muddle per-question
/// attribution, since each job already fans researchers out internally.
///
/// Per-job lifecycle: creates a DI scope, resolves <see cref="EvalRunner"/>, builds a
/// <see cref="ResultsWriter"/> against the job's output path, and pushes progress to two
/// SignalR groups via <see cref="IHubContext{AgentHub}"/>:
///   * <c>eval-{jobId}</c> — focused subscribers (the /evals page when watching a job).
///   * <c>eval-all</c> — global subscribers (the Past Runs page for badge updates).
/// </summary>
public sealed class EvalWorker : BackgroundService
{
    private static readonly JsonSerializerOptions QuestionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly EvalQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<AgentHub> _hub;
    private readonly ILogger<EvalWorker> _logger;

    public EvalWorker(
        EvalQueue queue,
        IServiceScopeFactory scopeFactory,
        IHubContext<AgentHub> hub,
        ILogger<EvalWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Cancelled before we even picked it up — skip without claiming a slot.
            if (job.Status == EvalJobStatus.Cancelled)
            {
                await FanOutStatusAsync(job, stoppingToken);
                continue;
            }

            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            job.Cts = jobCts;
            job.Status = EvalJobStatus.Running;
            job.StartedAt = DateTime.UtcNow;

            try
            {
                await FanOutStatusAsync(job, stoppingToken);
                await RunJobAsync(job, jobCts.Token);

                // If the CTS fired but the runner completed before noticing (race),
                // still treat it as cancelled.
                job.Status = jobCts.IsCancellationRequested ? EvalJobStatus.Cancelled : EvalJobStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                job.Status = EvalJobStatus.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Eval job {JobId} ({Variant}) failed", job.Id, job.Variant);
                job.Status = EvalJobStatus.Failed;
                job.ErrorMessage = ex.Message;
            }
            finally
            {
                job.CompletedAt = DateTime.UtcNow;
                job.Cts = null;
                _queue.MarkCompleted(job);
                await FanOutStatusAsync(job, CancellationToken.None);
            }
        }
    }

    private async Task RunJobAsync(EvalJobState job, CancellationToken ct)
    {
        var questions = LoadQuestions(job.QuestionSetPath);
        job.QuestionsTotal = questions.Count;

        if (questions.Count == 0)
        {
            throw new InvalidOperationException($"No questions in {job.QuestionSetPath}");
        }

        await using var writer = new ResultsWriter(job.OutputPath);

        using var scope = _scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<EvalRunner>();

        var variant = new EvalVariant(job.Variant, job.Config);

        await runner.RunVariantAsync(
            variant,
            questions,
            writer,
            onProgress: p => OnProgress(job, p),
            ct: ct);
    }

    private void OnProgress(EvalJobState job, EvalProgress progress)
    {
        // Progress fires twice per question: once at start (Result null), once at finish.
        // We push both — the start frame lets the UI show "running question N" before the
        // 3-minute orchestrator completes; the finish frame carries the row data.
        if (progress.Result is not null)
        {
            job.QuestionsDone = progress.Index;
            job.LatestResult = progress.Result;
        }

        // Fire-and-forget — progress fan-out must not block the runner. SignalR has its
        // own internal queue; if a slow client falls behind, that's their problem.
        _ = _hub.Clients
            .Groups($"eval-{job.Id}", "eval-all")
            .SendAsync("eval.progress", new
            {
                jobId = job.Id,
                variant = job.Variant,
                questionsDone = job.QuestionsDone,
                questionsTotal = job.QuestionsTotal,
                currentIndex = progress.Index,
                currentQuestionId = progress.Question.Id,
                currentQuestionText = progress.Question.Question,
                latestResult = progress.Result
            });
    }

    private async Task FanOutStatusAsync(EvalJobState job, CancellationToken ct)
    {
        try
        {
            await _hub.Clients
                .Groups($"eval-{job.Id}", "eval-all")
                .SendAsync("eval.status", EvalJobSnapshot.From(job), ct);
        }
        catch (Exception ex)
        {
            // Hub fan-out must not crash the worker.
            _logger.LogWarning(ex, "Failed to broadcast eval.status for job {JobId}", job.Id);
        }
    }

    private static List<EvalQuestion> LoadQuestions(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<EvalQuestion>>(json, QuestionJsonOptions) ?? new();
    }
}
