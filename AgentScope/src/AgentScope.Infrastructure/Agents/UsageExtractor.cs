using AgentScope.Application.Abstractions;

namespace AgentScope.Infrastructure.Agents;

/// <summary>
/// Extracts <see cref="AgentUsage"/> token counts from a Semantic Kernel streaming
/// message's <c>Metadata</c> dictionary.
///
/// SK's OpenAI connector populates usage on the final streaming chunk when
/// <c>stream_options.include_usage = true</c> is set. The exact key/property names
/// vary across SK + OpenAI SDK versions (e.g. <c>InputTokenCount</c> vs <c>PromptTokens</c>),
/// so this helper probes a list of known names rather than binding to a specific type.
///
/// Returns <c>null</c> when usage isn't present — caller treats null as "unknown",
/// which is distinct from "zero tokens".
/// </summary>
public static class UsageExtractor
{
    private static readonly string[] UsageContainerKeys = { "Usage", "usage" };

    private static readonly string[] PromptTokenKeys =
        { "InputTokenCount", "InputTokens", "PromptTokens", "PromptTokenCount", "prompt_tokens" };

    private static readonly string[] CompletionTokenKeys =
        { "OutputTokenCount", "OutputTokens", "CompletionTokens", "CompletionTokenCount", "completion_tokens" };

    public static (int TokensIn, int TokensOut)? TryExtract(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;

        // Layer 1: tokens at the top level of the dictionary (rare, but cheap to check).
        if (TryReadFromDict(metadata, PromptTokenKeys, out var inTop) &&
            TryReadFromDict(metadata, CompletionTokenKeys, out var outTop))
        {
            return (inTop, outTop);
        }

        // Layer 2: nested "Usage" object — reflect on its properties.
        foreach (var key in UsageContainerKeys)
        {
            if (!metadata.TryGetValue(key, out var container) || container is null) continue;

            // The container itself might be a dictionary (e.g. parsed JSON) or a CLR object.
            if (container is IReadOnlyDictionary<string, object?> dict)
            {
                if (TryReadFromDict(dict, PromptTokenKeys, out var inNestedDict) &&
                    TryReadFromDict(dict, CompletionTokenKeys, out var outNestedDict))
                {
                    return (inNestedDict, outNestedDict);
                }
            }

            if (TryReadFromObject(container, PromptTokenKeys, out var inNested) &&
                TryReadFromObject(container, CompletionTokenKeys, out var outNested))
            {
                return (inNested, outNested);
            }
        }

        return null;
    }

    /// <summary>Convenience: extract and compose into <see cref="AgentUsage"/> with a computed cost.</summary>
    public static AgentUsage? TryExtractWithCost(
        IReadOnlyDictionary<string, object?>? metadata,
        string model,
        IUsageCalculator calculator)
    {
        var tokens = TryExtract(metadata);
        if (tokens is null) return null;

        var (tIn, tOut) = tokens.Value;
        var cost = calculator.EstimateCostUsd(model, tIn, tOut);
        return new AgentUsage(tIn, tOut, cost);
    }

    private static bool TryReadFromDict(IReadOnlyDictionary<string, object?> dict, string[] keys, out int value)
    {
        foreach (var key in keys)
        {
            if (dict.TryGetValue(key, out var raw) && TryToInt(raw, out value)) return true;
        }
        value = 0;
        return false;
    }

    private static bool TryReadFromObject(object obj, string[] propertyNames, out int value)
    {
        var type = obj.GetType();
        foreach (var name in propertyNames)
        {
            var prop = type.GetProperty(name);
            if (prop is null) continue;

            object? raw;
            try { raw = prop.GetValue(obj); }
            catch { continue; }

            if (TryToInt(raw, out value)) return true;
        }
        value = 0;
        return false;
    }

    private static bool TryToInt(object? raw, out int value)
    {
        switch (raw)
        {
            case int i:    value = i;       return true;
            case long l:   value = (int)l;  return true;
            case short s:  value = s;       return true;
            case byte b:   value = b;       return true;
            case uint ui:  value = (int)ui; return true;
            case ulong ul: value = (int)ul; return true;
            case string s when int.TryParse(s, out var parsed): value = parsed; return true;
            default:       value = 0;       return false;
        }
    }
}
