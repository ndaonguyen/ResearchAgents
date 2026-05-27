using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Events;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentScope.Infrastructure.Plugins;

/// <summary>
/// Generic RAG search plugin parameterised by a <see cref="CorpusOptions"/>. One instance
/// per enabled corpus is created in <c>KernelFactory</c> and registered with a per-corpus
/// description (set via <see cref="Microsoft.SemanticKernel.KernelFunctionFactory"/>), so the
/// researcher LLM sees each corpus as a distinct tool with its own selection signal.
///
/// Self-contained (own Qdrant client + embedding HTTP). No class-level
/// <c>[KernelFunction]</c> attribute — the function is registered explicitly by the factory
/// so the description can come from config rather than a compile-time string.
/// </summary>
public sealed class CorpusSearchPlugin : IDisposable
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 10;

    private readonly CorpusOptions _corpus;
    private readonly OpenAiOptions _openAiOptions;
    private readonly QdrantClient _qdrant;
    private readonly HttpClient _embeddingsHttp;
    private readonly IAgentEventBus? _bus;
    private readonly AgentRunContext? _runContext;
    private readonly ILogger<CorpusSearchPlugin> _logger;

    public CorpusSearchPlugin(
        CorpusOptions corpus,
        QdrantOptions qdrant,
        OpenAiOptions openAi,
        IHttpClientFactory httpClientFactory,
        ILogger<CorpusSearchPlugin> logger,
        IAgentEventBus? bus = null,
        AgentRunContext? runContext = null)
    {
        _corpus = corpus;
        _openAiOptions = openAi;
        _bus = bus;
        _runContext = runContext;
        _logger = logger;

        _qdrant = new QdrantClient(
            host: qdrant.Host,
            port: qdrant.Port,
            https: qdrant.UseHttps,
            apiKey: qdrant.ApiKey);

        _embeddingsHttp = httpClientFactory.CreateClient($"corpus-{corpus.Name}");
        _embeddingsHttp.BaseAddress ??= new Uri("https://api.openai.com/v1/");
        if (_embeddingsHttp.DefaultRequestHeaders.Authorization is null)
        {
            _embeddingsHttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _openAiOptions.ApiKey);
        }
    }

    public async Task<string> SearchAsync(
        [Description("The search query — phrase it as a natural-language question or topic, not keywords.")] string query,
        [Description("Number of chunks to retrieve. Default 5, max 10.")] int topK = DefaultTopK,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Empty query.";

        topK = Math.Clamp(topK, 1, MaxTopK);

        try
        {
            var vector = await EmbedAsync(query, ct);

            var results = await _qdrant.SearchAsync(
                collectionName: _corpus.Collection,
                vector: vector,
                limit: (ulong)topK,
                payloadSelector: true,
                cancellationToken: ct);

            await PublishChunksRetrievedAsync(query, results, ct);

            if (results.Count == 0)
                return $"No matches in the {_corpus.Name} corpus.";

            return FormatResults(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Corpus search failed for {Corpus}: {Query}", _corpus.Name, query);
            return $"{_corpus.PluginName} search failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Pushes a structured <see cref="CorpusChunksRetrievedEvent"/> so the UI can render a
    /// per-agent "Sources used" panel. Best-effort: if there's no active run context (e.g.
    /// the plugin was called outside a tracked agent run, or the bus isn't wired), we
    /// silently skip — the LLM-facing string return is unaffected.
    /// </summary>
    private async Task PublishChunksRetrievedAsync(
        string query, IReadOnlyList<ScoredPoint> results, CancellationToken ct)
    {
        if (_bus is null || _runContext is null) return;
        if (_runContext.RunId is not { } runId || _runContext.AgentId is not { } agentId) return;

        var chunks = new List<CorpusChunk>(results.Count);
        foreach (var p in results)
        {
            var book = ReadString(p.Payload, "source_book") ?? "unknown";
            var pageStart = (int)ReadLong(p.Payload, "page_start");
            var pageEnd = (int)ReadLong(p.Payload, "page_end");
            var text = ReadString(p.Payload, "text") ?? "";
            chunks.Add(new CorpusChunk(book, pageStart, pageEnd, p.Score, text));
        }

        try
        {
            await _bus.PublishAsync(new CorpusChunksRetrievedEvent(
                runId, agentId, _corpus.PluginName ?? _corpus.Name, query, chunks, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            // Never let a UI-publication failure break the retrieval. Log and continue.
            _logger.LogDebug(ex, "Failed to publish CorpusChunksRetrievedEvent for {Corpus}", _corpus.Name);
        }
    }

    private static string FormatResults(IReadOnlyList<ScoredPoint> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} chunks:");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var p = results[i];
            var book = ReadString(p.Payload, "source_book") ?? "unknown";
            var pageStart = ReadLong(p.Payload, "page_start");
            var pageEnd = ReadLong(p.Payload, "page_end");
            var text = ReadString(p.Payload, "text") ?? "";

            var pageRange = pageStart == pageEnd ? $"p. {pageStart}" : $"pp. {pageStart}-{pageEnd}";
            sb.AppendLine($"--- [{i + 1}] {book} ({pageRange}, score {p.Score:F3}) ---");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var response = await _embeddingsHttp.PostAsJsonAsync(
            "embeddings",
            new EmbeddingsRequest(_openAiOptions.EmbeddingModel, text),
            ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken: ct);
        var embedding = payload?.Data?.FirstOrDefault()?.Embedding;
        if (embedding is null || embedding.Length == 0)
            throw new InvalidOperationException("OpenAI embeddings response was empty.");
        return embedding;
    }

    private static string? ReadString(Google.Protobuf.Collections.MapField<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.KindCase == Value.KindOneofCase.StringValue
            ? v.StringValue
            : null;

    private static long ReadLong(Google.Protobuf.Collections.MapField<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.KindCase == Value.KindOneofCase.IntegerValue
            ? v.IntegerValue
            : 0;

    public void Dispose() => _qdrant.Dispose();

    private sealed record EmbeddingsRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("data")] EmbeddingItem[]? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
