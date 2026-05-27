using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Configuration;
using AgentScope.Infrastructure.Agents.Filters;
using AgentScope.Infrastructure.Plugins;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Data;
using Microsoft.SemanticKernel.Plugins.Web.Tavily;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Builds a fresh <see cref="Kernel"/> per agent run.
/// We don't share Kernel instances across runs — cheap to construct, avoids any shared mutable state.
/// </summary>
public interface IKernelFactory
{
    /// <summary>
    /// Builds a kernel.
    /// <para><paramref name="modelOverride"/>: when non-null, overrides
    /// <c>AgentScopeOptions.OpenAi.Model</c>. Lets the orchestrator build a different
    /// kernel per agent role for the eval harness.</para>
    /// <para><paramref name="includePlugins"/>: when false, returns a minimal kernel with
    /// only the chat completion service — no Tavily, no BookLookup, no function filter.
    /// Used by the LLM-as-judge in the eval harness, which makes no function calls and
    /// shouldn't pollute the event bus.</para>
    /// </summary>
    Kernel Create(string? modelOverride = null, bool includePlugins = true);
}

public sealed class KernelFactory : IKernelFactory
{
    private readonly AgentScopeOptions _options;
    private readonly EventPublishingFunctionFilter _filter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _runContext;

    public KernelFactory(
        IOptions<AgentScopeOptions> options,
        EventPublishingFunctionFilter filter,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        IAgentEventBus bus,
        AgentRunContext runContext)
    {
        _options = options.Value;
        _filter = filter;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _bus = bus;
        _runContext = runContext;
    }

    public Kernel Create(string? modelOverride = null, bool includePlugins = true)
    {
        var builder = Kernel.CreateBuilder();

        var logPath = Path.Combine(AppContext.BaseDirectory, "openai-traffic.log");
        var httpClient = new HttpClient(new OpenAiTrafficLogger(logPath, new HttpClientHandler()));

        builder.AddOpenAIChatCompletion(
            modelId: modelOverride ?? _options.OpenAi.Model,
            apiKey: _options.OpenAi.ApiKey,
            httpClient: httpClient);

        var kernel = builder.Build();

        if (!includePlugins) return kernel;

        // Tavily — ITextSearch surfaced as a kernel function ("WebSearch.Search").
        var tavilySearch = new TavilyTextSearch(
            apiKey: _options.Tavily.ApiKey,
            options: new TavilyTextSearchOptions { IncludeRawContent = false });
        kernel.Plugins.Add(tavilySearch.CreateWithSearch("WebSearch"));

        // Open Library — structured book metadata + table of contents.
        var bookLookup = new BookLookupPlugin(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            _loggerFactory.CreateLogger<BookLookupPlugin>());
        kernel.Plugins.AddFromObject(bookLookup, "BookLookup");

        // RAG corpora — one plugin per enabled CorpusOptions entry. Each gets its own
        // KernelFunction with the description from config, so the LLM sees each corpus
        // as a distinct tool with its own selection signal. Skip silently when none
        // are enabled (zero-config default = no RAG, behaviour unchanged).
        foreach (var corpus in _options.Corpora)
        {
            if (!corpus.Enabled) continue;
            if (string.IsNullOrWhiteSpace(corpus.PluginName) || string.IsNullOrWhiteSpace(corpus.Collection))
                continue;

            var plugin = new CorpusSearchPlugin(
                corpus,
                _options.Qdrant,
                _options.OpenAi,
                _httpClientFactory,
                _loggerFactory.CreateLogger<CorpusSearchPlugin>(),
                _bus,
                _runContext);

            var function = KernelFunctionFactory.CreateFromMethod(
                method: plugin.SearchAsync,
                functionName: "Search",
                description: corpus.Description);

            kernel.Plugins.AddFromFunctions(corpus.PluginName, new[] { function });
        }

        // Capture every function invocation onto the event bus.
        kernel.FunctionInvocationFilters.Add(_filter);

        return kernel;
    }
}
