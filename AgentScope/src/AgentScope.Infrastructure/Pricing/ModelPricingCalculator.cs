using AgentScope.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentScope.Infrastructure.Pricing;

/// <summary>
/// Static pricing table keyed by OpenAI model id. Rates are USD per 1M tokens —
/// hardcoded snapshots, not fetched. Update the table when OpenAI changes pricing.
///
/// Unknown models return <c>null</c> (with a warning) so the UI can render "cost: —"
/// instead of $0.00, which would be misleading.
/// </summary>
public sealed class ModelPricingCalculator : IUsageCalculator
{
    private sealed record Rates(decimal InputPerMillion, decimal OutputPerMillion);

    // Pricing is keyed by the model id OpenAI returns in response metadata. OpenAI's API
    // resolves aliases (e.g. "gpt-4o-mini") to a dated snapshot ("gpt-4o-mini-2024-07-18")
    // before serving — and reports the snapshot, not the alias, back to the caller. We
    // need entries for BOTH because:
    //   - the alias is what config / OrchestratorConfig hold (used as the fallback model
    //     when response metadata is missing);
    //   - the snapshot is what the response metadata actually carries.
    private static readonly IReadOnlyDictionary<string, Rates> Pricing =
        new Dictionary<string, Rates>(StringComparer.OrdinalIgnoreCase)
        {
            // gpt-4o-mini — alias + snapshots
            ["gpt-4o-mini"]             = new(0.150m, 0.600m),
            ["gpt-4o-mini-2024-07-18"]  = new(0.150m, 0.600m),

            // gpt-4o — alias + snapshots
            ["gpt-4o"]                  = new(2.500m, 10.000m),
            ["gpt-4o-2024-08-06"]       = new(2.500m, 10.000m),
            ["gpt-4o-2024-11-20"]       = new(2.500m, 10.000m),

            // gpt-4.1 family — alias + snapshots
            ["gpt-4.1-mini"]            = new(0.400m, 1.600m),
            ["gpt-4.1-mini-2025-04-14"] = new(0.400m, 1.600m),
            ["gpt-4.1"]                 = new(2.000m, 8.000m),
            ["gpt-4.1-2025-04-14"]      = new(2.000m, 8.000m),

            // Embedding models — output is unused, charged on input only.
            ["text-embedding-3-small"]  = new(0.020m, 0m),
            ["text-embedding-3-large"]  = new(0.130m, 0m),
        };

    private readonly ILogger<ModelPricingCalculator> _logger;
    private readonly HashSet<string> _warnedUnknown = new(StringComparer.OrdinalIgnoreCase);

    public ModelPricingCalculator(ILogger<ModelPricingCalculator> logger)
    {
        _logger = logger;
    }

    public decimal? EstimateCostUsd(string model, int tokensIn, int tokensOut)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        if (!Pricing.TryGetValue(model, out var rates))
        {
            if (_warnedUnknown.Add(model))
                _logger.LogWarning("No pricing entry for model {Model}; cost will be reported as unknown", model);
            return null;
        }

        var inputCost = rates.InputPerMillion * tokensIn / 1_000_000m;
        var outputCost = rates.OutputPerMillion * tokensOut / 1_000_000m;
        return inputCost + outputCost;
    }
}
