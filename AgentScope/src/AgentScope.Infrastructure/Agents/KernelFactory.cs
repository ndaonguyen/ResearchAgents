using AgentScope.Infrastructure.Configuration;
using AgentScope.Infrastructure.Agents.Filters;
using AgentScope.Infrastructure.Plugins;
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

    public KernelFactory(
        IOptions<AgentScopeOptions> options,
        EventPublishingFunctionFilter filter,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _filter = filter;
        _loggerFactory = loggerFactory;
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

        // Capture every function invocation onto the event bus.
        kernel.FunctionInvocationFilters.Add(_filter);

        return kernel;
    }
}
