using System.Text.Json;

namespace AgentScope.Infrastructure.Agents;

public sealed class OpenAiTrafficLogger : DelegatingHandler
{
    private readonly string _logPath;
    private static readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public OpenAiTrafficLogger(string logPath, HttpMessageHandler inner)
        : base(inner)
    {
        _logPath = logPath;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? "<no body>"
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var pretty = TryPrettyPrint(body);

        var entry =
            $"=============== {DateTime.UtcNow:O} ==============={Environment.NewLine}" +
            $"REQUEST: {request.Method} {request.RequestUri}{Environment.NewLine}" +
            $"BODY:{Environment.NewLine}{pretty}{Environment.NewLine}" +
            $"================================================{Environment.NewLine}{Environment.NewLine}";

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_logPath, entry, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string TryPrettyPrint(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
        }
        catch
        {
            return raw;
        }
    }
}
