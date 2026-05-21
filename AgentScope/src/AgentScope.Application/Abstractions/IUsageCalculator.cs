namespace AgentScope.Application.Abstractions;

/// <summary>
/// Port: turns token counts into an estimated USD cost using the per-model pricing
/// table. Returning <c>null</c> signals "unknown model" — callers should treat that
/// as "cost unavailable" rather than "free".
///
/// Implementation: <c>ModelPricingCalculator</c> (Infrastructure).
/// </summary>
public interface IUsageCalculator
{
    decimal? EstimateCostUsd(string model, int tokensIn, int tokensOut);
}
