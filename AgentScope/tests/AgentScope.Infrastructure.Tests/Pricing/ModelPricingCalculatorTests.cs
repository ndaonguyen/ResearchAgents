using AgentScope.Infrastructure.Pricing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Pricing;

public class ModelPricingCalculatorTests
{
    private static ModelPricingCalculator Sut() =>
        new(NullLogger<ModelPricingCalculator>.Instance);

    [Fact]
    public void Returns_cost_for_known_chat_model()
    {
        // gpt-4o-mini: $0.150/M input, $0.600/M output
        // 1000 in, 500 out = 0.000150 + 0.000300 = 0.000450
        var cost = Sut().EstimateCostUsd("gpt-4o-mini", 1000, 500);
        cost.Should().Be(0.000450m);
    }

    [Fact]
    public void Returns_cost_for_known_embedding_model_using_input_only()
    {
        // text-embedding-3-small: $0.020/M input, $0/M output
        var cost = Sut().EstimateCostUsd("text-embedding-3-small", 10_000, 0);
        cost.Should().Be(0.000200m);
    }

    [Fact]
    public void Model_lookup_is_case_insensitive()
    {
        Sut().EstimateCostUsd("GPT-4O-MINI", 1_000_000, 0).Should().Be(0.150m);
    }

    [Fact]
    public void Returns_null_for_unknown_model()
    {
        Sut().EstimateCostUsd("not-a-real-model", 1000, 500).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_blank_model(string? model)
    {
        Sut().EstimateCostUsd(model!, 1000, 500).Should().BeNull();
    }

    [Fact]
    public void Zero_tokens_returns_zero_cost_not_null()
    {
        Sut().EstimateCostUsd("gpt-4o-mini", 0, 0).Should().Be(0m);
    }
}
