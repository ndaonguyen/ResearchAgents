using System.Diagnostics;
using System.Text.Json;
using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace AgentScope.Infrastructure.Agents.Filters;

/// <summary>
/// SK middleware: intercepts every plugin function (tool) invocation and publishes
/// <see cref="ToolCalledEvent"/> / <see cref="ToolResultEvent"/> on the event bus.
///
/// Registered as a singleton; reads the current run/agent from <see cref="AgentRunContext"/>
/// (AsyncLocal), so it works correctly under concurrency.
/// </summary>
public sealed class EventPublishingFunctionFilter : IFunctionInvocationFilter
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        WriteIndented = false,
        MaxDepth = 8
    };

    private readonly IAgentEventBus _bus;
    private readonly AgentRunContext _context;
    private readonly ILogger<EventPublishingFunctionFilter> _logger;

    public EventPublishingFunctionFilter(
        IAgentEventBus bus,
        AgentRunContext context,
        ILogger<EventPublishingFunctionFilter> logger)
    {
        _bus = bus;
        _context = context;
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        if (_context.RunId is not { } currentRunId ||
            _context.AgentId is not { } currentAgentId)
        {
            // No active context — this happens if a tool fires outside of a tracked agent run.
            // We let the call proceed but don't publish events.
            _logger.LogDebug("Function {Function} invoked without an active AgentRunContext", context.Function.Name);
            await next(context);
            return;
        }

        var toolName = $"{context.Function.PluginName}.{context.Function.Name}";
        var argsJson = SerializeArguments(context.Arguments);

        await _bus.PublishAsync(new ToolCalledEvent(
            currentRunId, currentAgentId, toolName, argsJson, DateTime.UtcNow));

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
        }

        var resultJson = SerializeResult(context.Result?.GetValue<object>());

        await _bus.PublishAsync(new ToolResultEvent(
            currentRunId, currentAgentId, toolName, resultJson, sw.ElapsedMilliseconds, DateTime.UtcNow));
    }

    private static string SerializeArguments(KernelArguments args)
    {
        try
        {
            return JsonSerializer.Serialize(
                args.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));
        }
        catch
        {
            return "{}";
        }
    }

    private static string SerializeResult(object? value)
    {
        if (value is null) return "null";
        try
        {
            return JsonSerializer.Serialize(value, ResultJsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(value.ToString());
        }
    }
}
