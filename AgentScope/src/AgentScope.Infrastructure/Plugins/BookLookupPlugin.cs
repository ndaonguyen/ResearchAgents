using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AgentScope.Infrastructure.Plugins;

/// <summary>
/// SK plugin: looks up books on Open Library (free, no key) for structured metadata —
/// especially the table of contents, which general web search struggles to surface.
///
/// The LLM should call this when the user asks about a specific book by name
/// (chapter list, summary, publication info). For general web research, use WebSearch.
/// </summary>
public sealed class BookLookupPlugin
{
    private const string SearchUrl = "https://openlibrary.org/search.json?q={0}&limit=1";
    private const string WorkUrl = "https://openlibrary.org{0}.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger<BookLookupPlugin> _logger;

    public BookLookupPlugin(HttpClient http, ILogger<BookLookupPlugin> logger)
    {
        _http = http;
        _logger = logger;
    }

    [KernelFunction("GetBookMetadata")]
    [Description("Look up a book by title (and optionally author) and return its " +
                 "table of contents, description, publication year, and Open Library URL. " +
                 "Use this when a question mentions a specific book by name — especially " +
                 "if the question asks about chapters, contents, structure, or summary " +
                 "of a known book. Not a substitute for general web search.")]
    public async Task<string> GetBookMetadataAsync(
        [Description("The book title, as accurate as possible.")] string title,
        [Description("The author name, optional but improves accuracy.")] string? author = null,
        CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(author) ? title : $"{title} {author}";
        var url = string.Format(SearchUrl, Uri.EscapeDataString(query));

        try
        {
            var search = await _http.GetFromJsonAsync<SearchResponse>(url, JsonOpts, ct);
            var doc = search?.Docs?.FirstOrDefault();
            if (doc is null || string.IsNullOrEmpty(doc.Key))
                return $"No Open Library entry found for \"{query}\".";

            var workUrl = string.Format(WorkUrl, doc.Key);
            var work = await _http.GetFromJsonAsync<WorkResponse>(workUrl, JsonOpts, ct);

            return FormatResult(doc, work);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Open Library lookup failed for {Title}", title);
            return $"Open Library lookup failed: {ex.Message}";
        }
    }

    private static string FormatResult(SearchDoc doc, WorkResponse? work)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {doc.Title}");
        if (doc.AuthorName is { Length: > 0 } authors)
            sb.AppendLine($"Author(s): {string.Join(", ", authors)}");
        if (doc.FirstPublishYear is int year)
            sb.AppendLine($"First published: {year}");
        sb.AppendLine($"Open Library URL: https://openlibrary.org{doc.Key}");

        var description = ExtractDescription(work?.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(description);
        }

        if (work?.TableOfContents is { Length: > 0 } toc)
        {
            sb.AppendLine();
            sb.AppendLine("Table of contents:");
            for (var i = 0; i < toc.Length; i++)
            {
                var entry = toc[i];
                var label = !string.IsNullOrWhiteSpace(entry.Label) ? entry.Label : "";
                var title = !string.IsNullOrWhiteSpace(entry.Title) ? entry.Title : "";
                var line = string.IsNullOrEmpty(label) ? title : $"{label}. {title}".Trim('.', ' ');
                sb.AppendLine($"  {i + 1}. {line}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Table of contents: not available on Open Library for this work.");
        }

        return sb.ToString();
    }

    private static string? ExtractDescription(JsonElement? element)
    {
        if (element is null) return null;
        var el = element.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Object when el.TryGetProperty("value", out var v) => v.GetString(),
            _ => null
        };
    }

    private sealed record SearchResponse(SearchDoc[]? Docs);
    private sealed record SearchDoc(
        string? Key,
        string? Title,
        string[]? AuthorName,
        int? FirstPublishYear);
    private sealed record WorkResponse(JsonElement? Description, TocEntry[]? TableOfContents);
    private sealed record TocEntry(string? Label, string? Title);
}
