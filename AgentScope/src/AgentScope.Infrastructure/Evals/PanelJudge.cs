using AgentScope.Application.Abstractions;
using AgentScope.Application.Evals;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentScope.Infrastructure.Evals;

/// <summary>
/// n-of-k self-consistency judge. Fans out <c>Judge:Samples</c> independent <see cref="LlmJudge"/>
/// calls per answer, then reduces them to a single verdict: the headline score is the median of
/// the samples, and their spread is reported as the dispersion (population std-dev).
///
/// Why median, not mean: the scale is an ordinal 1-5 rubric, so the median is the robust central
/// estimate — one rogue draw (a 1 among 4/4/5) can't drag the headline the way a mean would.
///
/// Why keep the raw votes: a leaderboard with no error bar is "confidently wrong" (see
/// <see cref="LlmJudge"/> calibration note). Persisting every vote + the spread lets the viewer
/// answer "is a variant delta real, or judge noise?" instead of trusting a bare point estimate.
///
/// Seeds: sample <c>i</c> gets <c>SeedBase + i</c> when a base seed is configured. Distinct seeds
/// at <c>Temperature &gt; 0</c> give genuinely different draws (so the spread means something)
/// while keeping the whole panel reproducible across re-runs — diversity and determinism at once.
///
/// Reduces to a single call when <c>Samples &lt;= 1</c>, so the default config is behaviourally
/// identical to the old single-judge path.
/// </summary>
public sealed class PanelJudge : IAnswerJudge
{
    private readonly LlmJudge _judge;
    private readonly int _samples;
    private readonly long? _seedBase;
    private readonly ILogger<PanelJudge> _logger;

    public PanelJudge(LlmJudge judge, IOptions<AgentScopeOptions> options, ILogger<PanelJudge> logger)
    {
        _judge = judge;
        _samples = Math.Max(1, options.Value.Judge.Samples);
        _seedBase = options.Value.Judge.SeedBase;
        _logger = logger;
    }

    public async Task<JudgeVerdict> ScoreAsync(EvalQuestion question, string answer, CancellationToken ct = default)
    {
        // The k calls are independent, so fan them out concurrently — wall-clock ≈ one call.
        // This is orthogonal to EvalRunner's deliberately-sequential per-question loop (which is
        // sequential to stay under rate limits since the orchestrator already fans out internally).
        var tasks = new Task<JudgeVerdict>[_samples];
        for (var i = 0; i < _samples; i++)
        {
            var seed = _seedBase is { } b ? b + i : (long?)null;
            tasks[i] = _judge.ScoreOnceAsync(question, answer, seed, ct);
        }

        var verdicts = await Task.WhenAll(tasks);
        var reduced = Reduce(verdicts);

        if (_samples > 1)
        {
            _logger.LogInformation(
                "Judge panel n={Samples}: votes=[{Votes}] median={Median} stddev={StdDev:F2}",
                _samples,
                string.Join(",", reduced.Scores ?? Array.Empty<int>()),
                reduced.Score?.ToString() ?? "-",
                reduced.ScoreStdDev ?? 0.0);
        }

        return reduced;
    }

    /// <summary>
    /// Pure reduction of k samples to one verdict: median headline, population std-dev spread,
    /// usage summed across all samples, reasoning taken from the sample nearest the median.
    /// Drops samples with a null score (parse failure / out-of-range) but still counts their cost.
    /// Extracted from the fan-out so the aggregation math is unit-testable without a kernel.
    /// </summary>
    internal static JudgeVerdict Reduce(IReadOnlyList<JudgeVerdict> verdicts)
    {
        // Token/cost is summed across every sample — including the ones whose score we drop below,
        // because they still cost money.
        var usage = SumUsage(verdicts);

        // Drop samples that failed to produce a usable score (parse failure / out-of-range, already
        // nulled + logged by LlmJudge). One bad draw shouldn't sink an otherwise-clean panel.
        var scores = verdicts
            .Where(v => v.Score is not null)
            .Select(v => v.Score!.Value)
            .OrderBy(s => s)
            .ToList();

        if (scores.Count == 0)
        {
            return new JudgeVerdict(null, verdicts.FirstOrDefault()?.Reasoning, usage, Array.Empty<int>(), null);
        }

        var medianValue = MedianValue(scores);
        var headline = (int)Math.Round(medianValue, MidpointRounding.AwayFromZero);
        var stdDev = scores.Count > 1 ? StdDev(scores) : (double?)null;

        // Attach the reasoning from whichever sample landed closest to the median, so the stored
        // rationale is coherent with the headline score rather than picked arbitrarily.
        var reasoning = verdicts
            .Where(v => v.Score is not null)
            .OrderBy(v => Math.Abs(v.Score!.Value - medianValue))
            .First()
            .Reasoning;

        return new JudgeVerdict(headline, reasoning, usage, scores, stdDev);
    }

    private static double MedianValue(IReadOnlyList<int> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    // Population std-dev (÷n): a descriptive spread of the votes we actually drew, not an
    // inference about a larger population, so the population form is the honest one here.
    private static double StdDev(IReadOnlyList<int> values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return Math.Sqrt(variance);
    }

    private static AgentUsage SumUsage(IEnumerable<JudgeVerdict> verdicts)
    {
        var tokensIn = 0;
        var tokensOut = 0;
        decimal? cost = null;
        foreach (var v in verdicts)
        {
            tokensIn += v.Usage.TokensIn;
            tokensOut += v.Usage.TokensOut;
            if (v.Usage.CostUsd is { } c) cost = (cost ?? 0m) + c;
        }
        return new AgentUsage(tokensIn, tokensOut, cost);
    }
}
