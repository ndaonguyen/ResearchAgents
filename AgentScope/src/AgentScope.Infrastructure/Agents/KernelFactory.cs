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
    Kernel Create();
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

    public Kernel Create()
    {
        var builder = Kernel.CreateBuilder();

        var logPath = Path.Combine(AppContext.BaseDirectory, "openai-traffic.log");
        var httpClient = new HttpClient(new OpenAiTrafficLogger(logPath, new HttpClientHandler()));

        builder.AddOpenAIChatCompletion(
            modelId: _options.OpenAi.Model,
            apiKey: _options.OpenAi.ApiKey,
            httpClient: httpClient);

        var kernel = builder.Build();

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
