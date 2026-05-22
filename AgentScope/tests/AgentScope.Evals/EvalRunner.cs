using System.Diagnostics;
using AgentScope.Application.Runs;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using Microsoft.Extensions.Logging;

namespace AgentScope.Evals;

/// <summary>
/// Runs a question set through the orchestrator once per variant. Sequential by design —
/// parallelism here would (a) trip OpenAI/Tavily rate limits since the orchestrator already
/// fans out researchers internally, and (b) make per-question attribution noisier. Add a
/// concurrency knob later only if a single-threaded run is too slow.
/// </summary>
public sealed class EvalRunner
{
    private static readonly TimeSpan PerQuestionTimeout = TimeSpan.FromMinutes(3);

    private readonly StartRunUseCase _startRun;
    private readonly LlmJudge _judge;
    private readonly ResultsWriter _writer;
    private readonly ILogger<EvalRunner> _logger;

    public EvalRunner(StartRunUseCase startRun, LlmJudge judge, ResultsWriter writer, ILogger<EvalRunner> logger)
    {
        _startRun = startRun;
        _judge = judge;
        _writer = writer;
        _logger = logger;
    }

    public async Task RunVariantAsync(
        EvalVariant variant,
        IReadOnlyList<EvalQuestion> questions,
        CancellationToken ct)
    {
        _logger.LogInformation("Running variant '{Variant}' over {Count} questions", variant.Label, questions.Count);

        for (var i = 0; i < questions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = questions[i];
            Console.WriteLine($"[{variant.Label}] {i + 1}/{questions.Count}  {q.Id}: {Truncate(q.Question, 80)}");

            var result = await RunSingleAsync(variant, q, ct);
            await _writer.AppendAsync(result, ct);

            var scoreText = result.JudgeScore?.ToString() ?? "-";
            var costText = result.CostUsd is { } c ? $"${c:F4}" : "?";
            var status = result.Errored ? "ERR" : "OK";
            Console.WriteLine($"          {status}  score={scoreText}  cost={costText}  duration={result.DurationMs}ms");
        }
    }

    private async Task<EvalResult> RunSingleAsync(EvalVariant variant, EvalQuestion question, CancellationToken outerCt)
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
            CompletedAt: DateTime.UtcNow);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
