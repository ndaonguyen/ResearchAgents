using AgentScope.Application.Interview;
using AgentScope.Application.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace AgentScope.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<StartRunUseCase>();
        services.AddScoped<InterviewSessionUseCase>();
        return services;
    }
}
