using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using AgentScope.Indexer;
using AgentScope.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using UglyToad.PdfPig;

// Indexer entry point.
//
// Usage:
//   dotnet run --project tools/AgentScope.Indexer
//
// Reads AgentScope:Corpora[] from config. Each entry has:
//   - Name, Collection: Qdrant collection name
//   - BooksDirectory: folder containing the PDFs
//   - Books[]: filenames to index
//   - Enabled: skip the corpus when false
//
// Drops and recreates each enabled corpus's collection on every run, so the index
// is deterministic. Embedding cost is well under $0.05 per ~5 typical books.

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Development
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services.AddOptions<AgentScopeOptions>()
    .Bind(builder.Configuration.GetSection(AgentScopeOptions.SectionName));

using var host = builder.Build();
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var logger = loggerFactory.CreateLogger("Indexer");
var options = host.Services.GetRequiredService<IOptions<AgentScopeOptions>>().Value;

// Preflight checks — fail fast with actionable messages.
if (string.IsNullOrWhiteSpace(options.OpenAi.ApiKey) || options.OpenAi.ApiKey == "set-via-user-secrets-or-environment")
{
    Console.Error.WriteLine("ERROR: AgentScope:OpenAi:ApiKey is not configured. Set via user-secrets or env var.");
    return 1;
}

var enabledCorpora = options.Corpora.Where(c => c.Enabled).ToList();
if (enabledCorpora.Count == 0)
{
    Console.Error.WriteLine("ERROR: No enabled corpora in AgentScope:Corpora[].");
    return 1;
}

// --- Qdrant + embedder (shared across corpora) ---
using var qdrant = new QdrantClient(
    host: options.Qdrant.Host,
    port: options.Qdrant.Port,
    https: options.Qdrant.UseHttps,
    apiKey: options.Qdrant.ApiKey);

using var http = new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAi.ApiKey);
var embedder = new OpenAiEmbedder(http, options.OpenAi.EmbeddingModel, loggerFactory.CreateLogger<OpenAiEmbedder>());

// --- Index each enabled corpus ---
var grandTotalChunks = 0;
var totalDuration = Stopwatch.StartNew();

foreach (var corpus in enabledCorpora)
{
    logger.LogInformation("=== Corpus '{Name}' → collection {Collection} ({Books} book(s)) ===",
        corpus.Name, corpus.Collection, corpus.Books.Length);

    if (string.IsNullOrWhiteSpace(corpus.BooksDirectory) || !Directory.Exists(corpus.BooksDirectory))
    {
        logger.LogWarning("Skipping corpus {Name}: BooksDirectory missing or not found ({Dir})",
            corpus.Name, corpus.BooksDirectory);
        continue;
    }
    if (corpus.Books.Length == 0)
    {
        logger.LogWarning("Skipping corpus {Name}: Books[] is empty", corpus.Name);
        continue;
    }

    // Drop + recreate this corpus's collection for a deterministic index.
    if (await qdrant.CollectionExistsAsync(corpus.Collection))
    {
        logger.LogInformation("Dropping existing collection {Collection}", corpus.Collection);
        await qdrant.DeleteCollectionAsync(corpus.Collection);
    }

    await qdrant.CreateCollectionAsync(
        collectionName: corpus.Collection,
        vectorsConfig: new VectorParams { Size = 1536, Distance = Distance.Cosine });

    await qdrant.CreatePayloadIndexAsync(
        collectionName: corpus.Collection,
        fieldName: "source_book",
        schemaType: PayloadSchemaType.Keyword);

    var corpusChunks = 0;

    foreach (var bookFile in corpus.Books)
    {
        var path = Path.Combine(corpus.BooksDirectory, bookFile);
        if (!File.Exists(path))
        {
            logger.LogWarning("Skipping missing book: {Path}", path);
            continue;
        }

        var sw = Stopwatch.StartNew();
        logger.LogInformation("Reading {Book}", bookFile);

        var pages = ExtractPages(path);
        if (pages.Count == 0)
        {
            logger.LogWarning("No text extracted from {Book} (image-only PDF?). Skipping.", bookFile);
            continue;
        }

        var chunks = TextChunker.Chunk(bookFile, pages, targetChars: 2800, overlapChars: 400);
        logger.LogInformation("  {Pages} pages → {Chunks} chunks", pages.Count, chunks.Count);

        const int embedBatchSize = 64;
        var batchIndex = 0;
        foreach (var batch in Batch(chunks, embedBatchSize))
        {
            var vectors = await embedder.EmbedBatchAsync(batch.Select(c => c.Text).ToArray());
            var points = new List<PointStruct>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var c = batch[i];
                var point = new PointStruct
                {
                    Id = new PointId { Uuid = Guid.NewGuid().ToString("N") },
                    Vectors = vectors[i],
                };
                point.Payload["text"] = new Value { StringValue = c.Text };
                point.Payload["source_book"] = new Value { StringValue = c.SourceBook };
                point.Payload["page_start"] = new Value { IntegerValue = c.PageStart };
                point.Payload["page_end"] = new Value { IntegerValue = c.PageEnd };
                point.Payload["chunk_index"] = new Value { IntegerValue = c.ChunkIndex };
                points.Add(point);
            }

            await qdrant.UpsertAsync(corpus.Collection, points);
            batchIndex++;
            logger.LogInformation("  upserted batch {Batch} ({Count} chunks)", batchIndex, batch.Count);
        }

        corpusChunks += chunks.Count;
        sw.Stop();
        logger.LogInformation("  done in {Seconds:F1}s", sw.Elapsed.TotalSeconds);
    }

    logger.LogInformation("Corpus '{Name}' total: {Chunks} chunks", corpus.Name, corpusChunks);
    grandTotalChunks += corpusChunks;
}

totalDuration.Stop();
logger.LogInformation("Indexed {Total} chunks across {Corpora} corpus(es) in {Seconds:F1}s",
    grandTotalChunks, enabledCorpora.Count, totalDuration.Elapsed.TotalSeconds);
return 0;


// --- Helpers ---

static IReadOnlyList<PageText> ExtractPages(string path)
{
    var pages = new List<PageText>();
    using var doc = PdfDocument.Open(path);
    foreach (var page in doc.GetPages())
    {
        var text = page.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            pages.Add(new PageText(page.Number, text));
    }
    return pages;
}

static IEnumerable<List<T>> Batch<T>(IReadOnlyList<T> source, int size)
{
    for (var i = 0; i < source.Count; i += size)
        yield return source.Skip(i).Take(size).ToList();
}

// Records & helpers
namespace AgentScope.Indexer
{
    public sealed record PageText(int PageNumber, string Text);

    public sealed record Chunk(
        string SourceBook,
        int ChunkIndex,
        int PageStart,
        int PageEnd,
        string Text);

    /// <summary>
    /// Sliding-window chunker over a list of page texts. Tracks the original page span
    /// for each chunk so citations can include a page range, not just a chunk number.
    ///
    /// Tokens are approximated as chars/4 (English heuristic) — this saves pulling in a
    /// tokenizer dependency, and embedding quality isn't sensitive to chunk size within
    /// reasonable bounds. Good enough for v1.
    /// </summary>
    public static class TextChunker
    {
        public static IReadOnlyList<Chunk> Chunk(
            string sourceBook,
            IReadOnlyList<PageText> pages,
            int targetChars,
            int overlapChars)
        {
            // Flatten pages into a single character stream with per-char page tracking,
            // so we can recover (page_start, page_end) for each chunk regardless of where
            // the sliding window happens to fall.
            var sb = new StringBuilder();
            var charToPage = new List<int>();
            for (var i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                foreach (var ch in p.Text)
                {
                    sb.Append(ch);
                    charToPage.Add(p.PageNumber);
                }
                // Separator between pages — keeps the flow readable for embeddings.
                if (i < pages.Count - 1)
                {
                    sb.Append("\n\n");
                    charToPage.Add(p.PageNumber);
                    charToPage.Add(p.PageNumber);
                }
            }

            var full = sb.ToString();
            var result = new List<Chunk>();
            var step = Math.Max(1, targetChars - overlapChars);
            var chunkIndex = 0;

            for (var start = 0; start < full.Length; start += step)
            {
                var end = Math.Min(start + targetChars, full.Length);
                var text = full.Substring(start, end - start).Trim();
                if (text.Length < 100) continue; // skip tiny tails

                var pageStart = charToPage[start];
                var pageEnd = charToPage[Math.Min(end - 1, charToPage.Count - 1)];

                result.Add(new Chunk(sourceBook, chunkIndex++, pageStart, pageEnd, text));

                if (end >= full.Length) break;
            }

            return result;
        }
    }

    public sealed class OpenAiEmbedder
    {
        private readonly HttpClient _http;
        private readonly string _model;
        private readonly ILogger<OpenAiEmbedder> _logger;

        public OpenAiEmbedder(HttpClient http, string model, ILogger<OpenAiEmbedder> logger)
        {
            _http = http;
            _model = model;
            _logger = logger;
        }

        public async Task<float[][]> EmbedBatchAsync(string[] inputs)
        {
            var response = await _http.PostAsJsonAsync("embeddings", new
            {
                model = _model,
                input = inputs
            });
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>();
            if (payload?.Data is null || payload.Data.Length != inputs.Length)
                throw new InvalidOperationException("OpenAI embeddings response size did not match input.");

            return payload.Data.Select(d => d.Embedding ?? Array.Empty<float>()).ToArray();
        }

        private sealed record EmbeddingsResponse(
            [property: JsonPropertyName("data")] EmbeddingItem[]? Data);

        private sealed record EmbeddingItem(
            [property: JsonPropertyName("embedding")] float[]? Embedding,
            [property: JsonPropertyName("index")] int Index);
    }
}
