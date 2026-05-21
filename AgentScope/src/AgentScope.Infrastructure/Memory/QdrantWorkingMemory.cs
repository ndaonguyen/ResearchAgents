using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Configuration;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AgentScope.Infrastructure.Memory;

/// <summary>
/// Qdrant-backed working memory. Embeds text via the OpenAI REST API
/// (text-embedding-3-small → 1536 dims by default) and stores vectors in a single
/// collection. Per-run isolation is enforced by a <c>run_id</c> payload filter on every read.
///
/// The collection is created lazily on first write. Other runs' points are never returned
/// because every <see cref="SearchAsync"/> query is filtered by <c>run_id</c>.
/// </summary>
public sealed class QdrantWorkingMemory : IWorkingMemory, IDisposable
{
    private const ulong VectorDimension = 1536; // text-embedding-3-small default
    private const string RunIdPayloadKey = "run_id";
    private const string AgentIdPayloadKey = "agent_id";
    private const string TextPayloadKey = "text";

    private readonly QdrantOptions _qdrantOptions;
    private readonly OpenAiOptions _openAiOptions;
    private readonly QdrantClient _client;
    private readonly HttpClient _embeddingsHttp;
    private readonly ILogger<QdrantWorkingMemory> _logger;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _collectionInitialized;

    public QdrantWorkingMemory(
        IOptions<AgentScopeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<QdrantWorkingMemory> logger)
    {
        var o = options.Value;
        _qdrantOptions = o.Qdrant;
        _openAiOptions = o.OpenAi;
        _logger = logger;

        _client = new QdrantClient(
            host: _qdrantOptions.Host,
            port: _qdrantOptions.Port,
            https: _qdrantOptions.UseHttps,
            apiKey: _qdrantOptions.ApiKey);

        _embeddingsHttp = httpClientFactory.CreateClient(nameof(QdrantWorkingMemory));
        _embeddingsHttp.BaseAddress ??= new Uri("https://api.openai.com/v1/");
        if (_embeddingsHttp.DefaultRequestHeaders.Authorization is null)
        {
            _embeddingsHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAiOptions.ApiKey);
        }
    }

    public async Task SaveAsync(
        RunId runId,
        AgentId agentId,
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        await EnsureCollectionAsync(ct);

        var vector = await EmbedAsync(text, ct);

        var point = new PointStruct
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString("N") },
            Vectors = vector,
        };
        point.Payload[RunIdPayloadKey] = new Value { StringValue = runId.Value };
        point.Payload[AgentIdPayloadKey] = new Value { StringValue = agentId.Value };
        point.Payload[TextPayloadKey] = new Value { StringValue = text };

        if (metadata is not null)
        {
            foreach (var kv in metadata)
            {
                if (kv.Key is RunIdPayloadKey or AgentIdPayloadKey or TextPayloadKey) continue;
                point.Payload[kv.Key] = new Value { StringValue = kv.Value };
            }
        }

        await _client.UpsertAsync(_qdrantOptions.Collection, new[] { point }, cancellationToken: ct);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        RunId runId,
        string query,
        int k = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MemoryHit>();

        await EnsureCollectionAsync(ct);

        var vector = await EmbedAsync(query, ct);

        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = RunIdPayloadKey,
                        Match = new Match { Keyword = runId.Value }
                    }
                }
            }
        };

        var results = await _client.SearchAsync(
            collectionName: _qdrantOptions.Collection,
            vector: vector,
            filter: filter,
            limit: (ulong)k,
            payloadSelector: true,
            cancellationToken: ct);

        var hits = new List<MemoryHit>(results.Count);
        foreach (var scored in results)
        {
            var text = ReadString(scored.Payload, TextPayloadKey) ?? string.Empty;
            var meta = new Dictionary<string, string>();
            foreach (var kv in scored.Payload)
            {
                if (kv.Key == TextPayloadKey) continue;
                if (kv.Value.KindCase == Value.KindOneofCase.StringValue)
                    meta[kv.Key] = kv.Value.StringValue;
            }
            hits.Add(new MemoryHit(text, scored.Score, meta));
        }
        return hits;
    }

    private static string? ReadString(MapField<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var v) && v.KindCase == Value.KindOneofCase.StringValue
            ? v.StringValue
            : null;

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_collectionInitialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_collectionInitialized) return;

            var exists = await _client.CollectionExistsAsync(_qdrantOptions.Collection, cancellationToken: ct);
            if (!exists)
            {
                _logger.LogInformation("Creating Qdrant collection {Collection}", _qdrantOptions.Collection);
                await _client.CreateCollectionAsync(
                    collectionName: _qdrantOptions.Collection,
                    vectorsConfig: new VectorParams { Size = VectorDimension, Distance = Distance.Cosine },
                    cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    collectionName: _qdrantOptions.Collection,
                    fieldName: RunIdPayloadKey,
                    schemaType: PayloadSchemaType.Keyword,
                    cancellationToken: ct);
            }
            _collectionInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
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

    public void Dispose()
    {
        _initLock.Dispose();
        _client.Dispose();
    }

    private sealed record EmbeddingsRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("data")] EmbeddingItem[]? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
