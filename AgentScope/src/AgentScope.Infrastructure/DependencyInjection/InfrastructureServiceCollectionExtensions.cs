using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Configuration;
using AgentScope.Infrastructure.EventBus;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Agents.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentScope.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AgentScopeOptions>()
            .Bind(configuration.GetSection(AgentScopeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The event bus is a singleton — all runs route through it.
        services.AddSingleton<IAgentEventBus, ChannelAgentEventBus>();

        // AsyncLocal-backed context — singleton because the storage is static per logical flow.
        services.AddSingleton<AgentRunContext>();

        // The function filter is attached to each Kernel by KernelFactory — it doesn't need
        // a separate IFunctionInvocationFilter registration since SK reads filters from the
        // Kernel's FunctionInvocationFilters collection, not from DI.
        services.AddSingleton<EventPublishingFunctionFilter>();

        services.AddSingleton<IKernelFactory, KernelFactory>();

        // Sub-agents — scoped per request.
        services.AddScoped<IPlannerAgent, PlannerAgent>();
        services.AddScoped<IResearcherAgent, ResearcherAgent>();
        services.AddScoped<ICriticAgent, CriticAgent>();
        services.AddScoped<ISynthesizerAgent, SynthesizerAgent>();

        // Orchestrator — scoped so each request gets its own logger scope.
        services.AddScoped<IOrchestrator, Orchestrator>();

        return services;
    }
}
