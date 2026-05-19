using AgentScope.Infrastructure.Configuration;
using AgentScope.Infrastructure.Agents.Filters;
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

    public KernelFactory(
        IOptions<AgentScopeOptions> options,
        EventPublishingFunctionFilter filter)
    {
        _options = options.Value;
        _filter = filter;
    }

    public Kernel Create()
    {
        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: _options.OpenAi.Model,
            apiKey: _options.OpenAi.ApiKey);

        var kernel = builder.Build();

        // Tavily — ITextSearch surfaced as a kernel function ("WebSearch.Search").
        var tavilySearch = new TavilyTextSearch(
            apiKey: _options.Tavily.ApiKey,
            options: new TavilyTextSearchOptions { IncludeRawContent = false });
        kernel.Plugins.Add(tavilySearch.CreateWithSearch("WebSearch"));

        // Capture every function invocation onto the event bus.
        kernel.FunctionInvocationFilters.Add(_filter);

        return kernel;
    }
}
