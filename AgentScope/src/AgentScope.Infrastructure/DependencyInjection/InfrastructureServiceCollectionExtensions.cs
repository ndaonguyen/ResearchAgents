using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Configuration;
using AgentScope.Infrastructure.EventBus;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Agents.Filters;
using AgentScope.Infrastructure.Memory;
using AgentScope.Infrastructure.Pricing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        services.AddHttpClient();

        // The event bus is a singleton — all runs route through it.
        services.AddSingleton<IAgentEventBus, ChannelAgentEventBus>();

        // AsyncLocal-backed context — singleton because the storage is static per logical flow.
        services.AddSingleton<AgentRunContext>();

        // The function filter is attached to each Kernel by KernelFactory — it doesn't need
        // a separate IFunctionInvocationFilter registration since SK reads filters from the
        // Kernel's FunctionInvocationFilters collection, not from DI.
        services.AddSingleton<EventPublishingFunctionFilter>();

        services.AddSingleton<IKernelFactory, KernelFactory>();

        // Pricing for per-agent + per-run cost estimation.
        services.AddSingleton<IUsageCalculator, ModelPricingCalculator>();

        // Working memory — Null by default; Qdrant when explicitly enabled in config.
        // Per-run isolation is handled inside the implementation (Qdrant filters by run_id).
        var qdrantEnabled = configuration
            .GetSection(AgentScopeOptions.SectionName)
            .GetSection(nameof(AgentScopeOptions.Qdrant))
            .GetValue<bool>(nameof(QdrantOptions.Enabled));

        if (qdrantEnabled)
        {
            services.AddSingleton<IWorkingMemory, QdrantWorkingMemory>();
        }
        else
        {
            services.AddSingleton<IWorkingMemory, NullWorkingMemory>();
        }

        // RAG corpus plugins are constructed per-corpus inside KernelFactory (one instance
        // per enabled CorpusOptions entry), so no DI registration here.

        // Research sub-agents — scoped per request.
        services.AddScoped<IPlannerAgent, PlannerAgent>();
        services.AddScoped<IResearcherAgent, ResearcherAgent>();
        services.AddScoped<ICriticAgent, CriticAgent>();
        services.AddScoped<ISynthesizerAgent, SynthesizerAgent>();

        // Interview sub-agents — same lifetime as research agents.
        services.AddScoped<IInterviewerAgent, InterviewerAgent>();
        services.AddScoped<IProbeAgent, ProbeAgent>();
        services.AddScoped<IHintAgent, HintAgent>();
        services.AddScoped<IModelAnswerAgent, ModelAnswerAgent>();
        services.AddScoped<IQuickCheckAgent, QuickCheckAgent>();
        services.AddScoped<IGraderAgent, GraderAgent>();
        services.AddScoped<ICoachAgent, CoachAgent>();

        // Orchestrator — scoped so each request gets its own logger scope.
        services.AddScoped<IOrchestrator, Orchestrator>();

        return services;
    }
}
