using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentScope.Infrastructure.Plugins;

/// <summary>
/// SK plugin: searches the curated software-architecture book corpus indexed by
/// <c>tools/AgentScope.Indexer</c>. Embeds the query, runs a top-k Qdrant search,
/// and returns formatted chunks with book + page citations.
///
/// Registered on the researcher's kernel only when
/// <see cref="ArchitectureCorpusOptions.Enabled"/> is true.
///
/// Self-contained (own Qdrant client + embedding HTTP) so it has no coupling to the
/// per-run working memory infrastructure — the corpus collection and the working-memory
/// collection are different concerns and should stay independent.
/// </summary>
public sealed class ArchitectureSearchPlugin : IDisposable
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 10;

    private readonly QdrantClient _qdrant;
    private readonly HttpClient _embeddingsHttp;
    private readonly ArchitectureCorpusOptions _corpusOptions;
    private readonly OpenAiOptions _openAiOptions;
    private readonly ILogger<ArchitectureSearchPlugin> _logger;

    public ArchitectureSearchPlugin(
        IOptions<AgentScopeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ArchitectureSearchPlugin> logger)
    {
        var o = options.Value;
        _corpusOptions = o.ArchitectureCorpus;
        _openAiOptions = o.OpenAi;
        _logger = logger;

        _qdrant = new QdrantClient(
            host: o.Qdrant.Host,
            port: o.Qdrant.Port,
            https: o.Qdrant.UseHttps,
            apiKey: o.Qdrant.ApiKey);

        _embeddingsHttp = httpClientFactory.CreateClient(nameof(ArchitectureSearchPlugin));
        _embeddingsHttp.BaseAddress ??= new Uri("https://api.openai.com/v1/");
        if (_embeddingsHttp.DefaultRequestHeaders.Authorization is null)
        {
            _embeddingsHttp.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _openAiOptions.ApiKey);
        }
    }

    [KernelFunction("SearchArchitectureCorpus")]
    [Description("Search a curated corpus of software architecture books " +
                 "(Software Architecture - The Hard Parts, Microservices Patterns, DDD Distilled, " +
                 "Software Architecture Patterns, Building Evolutionary Architectures) for established " +
                 "concepts, patterns, trade-offs, and definitions. Prefer this over WebSearch for any " +
                 "established architectural concept — pattern definitions, decision frameworks, DDD " +
                 "terminology, microservices trade-offs. Use WebSearch only when you need recent " +
                 "developments, specific products, or news.")]
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
                collectionName: _corpusOptions.Collection,
                vector: vector,
                limit: (ulong)topK,
                payloadSelector: true,
                cancellationToken: ct);

            if (results.Count == 0)
                return "No matches in the architecture corpus.";

            return FormatResults(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArchitectureSearch failed for query: {Query}", query);
            return $"ArchitectureSearch failed: {ex.Message}";
        }
    }

    private static string FormatResults(IReadOnlyList<ScoredPoint> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} chunks in the architecture corpus:");
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
