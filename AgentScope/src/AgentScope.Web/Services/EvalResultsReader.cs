using System.Text.Json;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AgentScope.Web.Services;

/// <summary>
/// Reads eval-harness JSONL output files from disk for the Past Runs viewer.
/// Opens with <see cref="FileShare.ReadWrite"/> so an in-flight eval (which is appending
/// to the file with AutoFlush) doesn't get locked out.
/// </summary>
public sealed class EvalResultsReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AgentScopeOptions _options;
    private readonly ILogger<EvalResultsReader> _logger;

    public EvalResultsReader(IOptions<AgentScopeOptions> options, ILogger<EvalResultsReader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string ResultsDirectory => Path.GetFullPath(_options.Evals.ResultsDirectory);

    /// <summary>
    /// Returns every readable JSONL file in the results directory, newest first.
    /// Files that fail to parse are skipped with a warning rather than aborting the whole read.
    /// </summary>
    public IReadOnlyList<EvalResultFile> ReadAll()
    {
        var dir = ResultsDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<EvalResultFile>();

        var files = new List<EvalResultFile>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            try
            {
                var file = ReadFile(path);
                if (file is not null) files.Add(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read eval result file {Path}", path);
            }
        }

        return files.OrderByDescending(f => f.LastModifiedUtc).ToList();
    }

    public EvalResultFile? ReadByName(string fileName)
    {
        var path = Path.Combine(ResultsDirectory, fileName);
        if (!File.Exists(path)) return null;

        try
        {
            return ReadFile(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read eval result file {Path}", path);
            return null;
        }
    }

    private static EvalResultFile? ReadFile(string path)
    {
        var rows = new List<EvalRow>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonSerializer.Deserialize<EvalRow>(line, JsonOptions);
            if (row is not null) rows.Add(row);
        }

        if (rows.Count == 0) return null;

        var fi = new FileInfo(path);
        return new EvalResultFile(
            Name: fi.Name,
            FullPath: fi.FullName,
            LastModifiedUtc: fi.LastWriteTimeUtc,
            Variant: rows[0].Variant,
            Rows: rows);
    }
}

public sealed record EvalRow(
    string QuestionId,
    string Variant,
    string Question,
    string RunId,
    string Answer,
    int TokensIn,
    int TokensOut,
    decimal? CostUsd,
    long DurationMs,
    bool Errored,
    string? ErrorMessage,
    int? JudgeScore,
    string? JudgeReasoning,
    int JudgeTokensIn,
    int JudgeTokensOut,
    decimal? JudgeCostUsd,
    DateTime CompletedAt,
    // Optional: corpus chunks the agents retrieved during this run. Populated by the
    // Practice/QuickCheck page so the Past Runs viewer can show what grounded each answer.
    // Older JSONL rows have this as null — readers must treat null as "no sources captured".
    IReadOnlyList<SourceChunk>? Sources = null);

/// <summary>
/// Persisted form of a corpus chunk that was retrieved during a run. Keyed by the
/// AgentId that retrieved it so the viewer can group sources per agent — same shape
/// as the live <see cref="AgentScope.Domain.Events.CorpusChunk"/> with one extra
/// attribution field.
/// </summary>
public sealed record SourceChunk(
    string AgentId,
    string Corpus,
    string Book,
    int PageStart,
    int PageEnd,
    double Score,
    string Text);

public sealed record EvalResultFile(
    string Name,
    string FullPath,
    DateTime LastModifiedUtc,
    string Variant,
    IReadOnlyList<EvalRow> Rows)
{
    public int Count => Rows.Count;
    public int OkCount => Rows.Count(r => !r.Errored);
    public int ErroredCount => Rows.Count(r => r.Errored);

    /// <summary>Mean judge score across rows that have one. Null when no judged rows.</summary>
    public double? MeanScore
    {
        get
        {
            var scored = Rows.Where(r => r.JudgeScore is not null).Select(r => (double)r.JudgeScore!.Value).ToList();
            return scored.Count == 0 ? null : scored.Average();
        }
    }

    /// <summary>Sum of agent-side costs (excludes judge cost). Null when no rows had a known cost.</summary>
    public decimal? TotalAgentCost
    {
        get
        {
            var costs = Rows.Where(r => r.CostUsd is not null).Select(r => r.CostUsd!.Value).ToList();
            return costs.Count == 0 ? null : costs.Sum();
        }
    }

    public decimal? TotalJudgeCost
    {
        get
        {
            var costs = Rows.Where(r => r.JudgeCostUsd is not null).Select(r => r.JudgeCostUsd!.Value).ToList();
            return costs.Count == 0 ? null : costs.Sum();
        }
    }
}
