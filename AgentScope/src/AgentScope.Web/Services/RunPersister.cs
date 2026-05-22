using System.Text.Json;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AgentScope.Web.Services;

/// <summary>
/// Persists UI-initiated runs to the same results directory the eval CLI writes to,
/// using the same JSONL schema so the Past Runs viewer sees both with no special-casing.
///
/// One file per UTC day (<c>ui-yyyyMMdd.jsonl</c>) so the file list doesn't explode after
/// a few weeks of casual use but each day's runs stay grouped.
///
/// Registered as a singleton — the shared semaphore serializes appends across concurrent
/// SignalR connections so two simultaneous runs don't interleave a single line write.
/// Best-effort: failures are logged, not thrown — a write blip must not kill a run.
/// </summary>
public sealed class RunPersister
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly AgentScopeOptions _options;
    private readonly ILogger<RunPersister> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RunPersister(IOptions<AgentScopeOptions> options, ILogger<RunPersister> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PersistAsync(EvalRow row, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetFullPath(_options.Evals.ResultsDirectory);
            Directory.CreateDirectory(dir);

            var fileName = $"ui-{DateTime.UtcNow:yyyyMMdd}.jsonl";
            var path = Path.Combine(dir, fileName);
            var line = JsonSerializer.Serialize(row, JsonOptions);

            await _gate.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist UI run {RunId}", row.RunId);
        }
    }
}
