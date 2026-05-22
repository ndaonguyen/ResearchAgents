using System.Text.Json;

namespace AgentScope.Evals;

/// <summary>
/// Append-only JSONL writer. One row per question. Crash-safe by design — flushes
/// after every line so a ctrl-C mid-run still leaves a usable file.
/// </summary>
public sealed class ResultsWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public ResultsWriter(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
        FilePath = filePath;
    }

    public string FilePath { get; }

    public async Task AppendAsync(EvalResult result, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(result, JsonOptions);
        await _gate.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        _gate.Dispose();
    }
}
