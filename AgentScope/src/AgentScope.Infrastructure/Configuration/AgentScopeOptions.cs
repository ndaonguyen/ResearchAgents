using System.ComponentModel.DataAnnotations;

namespace AgentScope.Infrastructure.Configuration;

/// <summary>
/// Configuration for the Semantic Kernel infrastructure.
/// Bound from the "AgentScope" section of appsettings/user-secrets.
/// </summary>
public sealed class AgentScopeOptions
{
    public const string SectionName = "AgentScope";

    public OpenAiOptions OpenAi { get; init; } = new();
    public TavilyOptions Tavily { get; init; } = new();
}

public sealed class OpenAiOptions
{
    [Required]
    public string ApiKey { get; init; } = "";

    public string Model { get; init; } = "gpt-4o-mini";
}

public sealed class TavilyOptions
{
    [Required]
    public string ApiKey { get; init; } = "";
}
