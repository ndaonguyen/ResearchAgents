using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Centralises construction of <see cref="OpenAIPromptExecutionSettings"/> so every
/// agent enables OpenAI's <c>stream_options.include_usage</c> the same way.
///
/// SK 1.71 doesn't expose a typed <c>StreamOptions</c> property, so we route the
/// flag through <see cref="OpenAIPromptExecutionSettings.ExtensionData"/> which the
/// connector forwards verbatim onto the chat completion request body. If a future
/// SK release adds a typed surface, this is the only place to update.
/// </summary>
internal static class AgentSettingsBuilder
{
    public static OpenAIPromptExecutionSettings Build(
        string? responseFormat = null,
        FunctionChoiceBehavior? functionChoice = null)
    {
        var settings = new OpenAIPromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true }
            }
        };

        if (responseFormat is not null) settings.ResponseFormat = responseFormat;
        if (functionChoice is not null) settings.FunctionChoiceBehavior = functionChoice;

        return settings;
    }
}
