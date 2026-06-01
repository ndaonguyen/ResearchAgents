using System.Diagnostics;
using AgentScope.Application.Runs;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using Microsoft.Extensions.Logging;

namespace AgentScope.Application.Evals;

/// <summary>
/// Runs a question set through the orchestrator once per variant. Sequential by design —
/// parallelism here would (a) trip OpenAI/Tavily rate limits since the orchestrator already
/// fans out researchers internally, and (b) make per-question attribution noisier.
///
/// Progress is reported via an <see cref="EvalProgress"/> callback so the runner can be
/// driven from a CLI (writes to console) or a background worker (fans out to SignalR)
/// without changing the runner itself.
/// </summary>
public sealed class EvalRunner
{
    private static readonly TimeSpan PerQuestionTimeout = TimeSpan.FromMinutes(3);

    private readonly StartRunUseCase _startRun;
    private readonly IAnswerJudge _judge;
    private readonly IOrchestratorFingerprintProvider _fingerprintProvider;
    private readonly ILogger<EvalRunner> _logger;

    public EvalRunner(
        StartRunUseCase startRun,
        IAnswerJudge judge,
        IOrchestratorFingerprintProvider fingerprintProvider,
        ILogger<EvalRunner> logger)
    {
        _startRun = startRun;
        _judge = judge;
        _fingerprintProvider = fingerprintProvider;
        _logger = logger;
    }

    public async Task RunVariantAsync(
        EvalVariant variant,
        IReadOnlyList<EvalQuestion> questions,
        ResultsWriter writer,
        Action<EvalProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        var fingerprint = _fingerprintProvider.Capture(variant.Config);
        _logger.LogInformation(
            "Running variant '{Variant}' over {Count} questions (prompt={Hash})",
            variant.Label, questions.Count, fingerprint.PromptHash);

        for (var i = 0; i < questions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = questions[i];
            onProgress?.Invoke(new EvalProgress(i, questions.Count, q, null));

            var result = await RunSingleAsync(variant, q, fingerprint, ct);
            await writer.AppendAsync(result, ct);

            onProgress?.Invoke(new EvalProgress(i + 1, questions.Count, q, result));
        }
    }

    private async Task<EvalResult> RunSingleAsync(EvalVariant variant, EvalQuestion question, OrchestratorFingerprint fingerprint, CancellationToken outerCt)
    {
        using var perQuestionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        perQuestionCts.CancelAfter(PerQuestionTimeout);

        var sw = Stopwatch.StartNew();
        var (runId, events) = _startRun.Start(question.Question, variant.Config, perQuestionCts.Token);

        string? finalAnswer = null;
        var tokensIn = 0;
        var tokensOut = 0;
        decimal? costUsd = null;
        var errored = false;
        string? errorMessage = null;

        try
        {
            await foreach (var evt in events.WithCancellation(perQuestionCts.Token))
            {
                switch (evt)
                {
                    case AgentFinishedEvent f when f.AgentId == AgentId.System:
                        finalAnswer = f.FinalText;
                        tokensIn = f.TokensIn;
                        tokensOut = f.TokensOut;
                        costUsd = f.EstimatedCostUsd;
                        break;

                    case AgentErrorEvent err when err.AgentId == AgentId.System:
                        errored = true;
                        errorMessage = err.Message;
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (perQuestionCts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            errored = true;
            errorMessage = $"timeout after {PerQuestionTimeout.TotalSeconds:F0}s";
        }

        sw.Stop();

        JudgeVerdict? verdict = null;
        if (!errored && !string.IsNullOrWhiteSpace(finalAnswer))
        {
            try
            {
                verdict = await _judge.ScoreAsync(question, finalAnswer, outerCt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Judge call failed for {QuestionId}", question.Id);
                verdict = new JudgeVerdict(null, $"judge error: {ex.Message}", new Application.Abstractions.AgentUsage(0, 0, null));
            }
        }

        return new EvalResult(
            QuestionId: question.Id,
            Variant: variant.Label,
            Question: question.Question,
            RunId: runId.Value,
            Answer: finalAnswer ?? "",
            TokensIn: tokensIn,
            TokensOut: tokensOut,
            CostUsd: costUsd,
            DurationMs: sw.ElapsedMilliseconds,
            Errored: errored,
            ErrorMessage: errorMessage,
            JudgeScore: verdict?.Score,
            JudgeReasoning: verdict?.Reasoning,
            JudgeTokensIn: verdict?.Usage.TokensIn ?? 0,
            JudgeTokensOut: verdict?.Usage.TokensOut ?? 0,
            JudgeCostUsd: verdict?.Usage.CostUsd,
            CompletedAt: DateTime.UtcNow,
            PromptHash: fingerprint.PromptHash,
            PlannerModel: fingerprint.PlannerModel,
            ResearcherModel: fingerprint.ResearcherModel,
            CriticModel: fingerprint.CriticModel,
            SynthesizerModel: fingerprint.SynthesizerModel,
            JudgeScores: verdict?.Scores,
            JudgeScoreStdDev: verdict?.ScoreStdDev);
    }
}

/// <summary>
/// Reported once when a question starts (Result null) and once when it finishes
/// (Result populated). <see cref="Index"/> is the zero-based index of the question
/// being / just processed; <see cref="Total"/> is the size of the question set.
/// </summary>
public sealed record EvalProgress(int Index, int Total, EvalQuestion Question, EvalResult? Result);
