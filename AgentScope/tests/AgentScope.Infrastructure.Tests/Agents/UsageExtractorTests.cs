using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.Pricing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Agents;

public class UsageExtractorTests
{
    [Fact]
    public void Returns_null_when_metadata_is_null()
    {
        UsageExtractor.TryExtract(null).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_metadata_is_empty()
    {
        UsageExtractor.TryExtract(new Dictionary<string, object?>()).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_no_usage_keys_present()
    {
        var meta = new Dictionary<string, object?> { ["Other"] = "value", ["Id"] = 123 };
        UsageExtractor.TryExtract(meta).Should().BeNull();
    }

    [Fact]
    public void Extracts_from_top_level_keys_using_OpenAI_SDK_naming()
    {
        var meta = new Dictionary<string, object?>
        {
            ["InputTokenCount"] = 120,
            ["OutputTokenCount"] = 45,
        };
        UsageExtractor.TryExtract(meta).Should().Be((120, 45));
    }

    [Fact]
    public void Extracts_from_top_level_keys_using_legacy_naming()
    {
        var meta = new Dictionary<string, object?>
        {
            ["PromptTokens"] = 200,
            ["CompletionTokens"] = 80,
        };
        UsageExtractor.TryExtract(meta).Should().Be((200, 80));
    }

    [Fact]
    public void Extracts_from_nested_Usage_dictionary()
    {
        // Mirrors a JSON-deserialised shape: { "Usage": { "PromptTokens": ..., "CompletionTokens": ... } }
        var meta = new Dictionary<string, object?>
        {
            ["Usage"] = new Dictionary<string, object?>
            {
                ["PromptTokens"] = 50,
                ["CompletionTokens"] = 25,
            }
        };
        UsageExtractor.TryExtract(meta).Should().Be((50, 25));
    }

    [Fact]
    public void Extracts_from_nested_Usage_object_via_reflection()
    {
        // Mirrors SK's actual shape: a CLR object with properties.
        var meta = new Dictionary<string, object?>
        {
            ["Usage"] = new FakeUsage(InputTokenCount: 333, OutputTokenCount: 111)
        };
        UsageExtractor.TryExtract(meta).Should().Be((333, 111));
    }

    [Fact]
    public void Tolerates_string_token_values()
    {
        var meta = new Dictionary<string, object?>
        {
            ["PromptTokens"] = "150",
            ["CompletionTokens"] = "75",
        };
        UsageExtractor.TryExtract(meta).Should().Be((150, 75));
    }

    [Fact]
    public void Tolerates_long_token_values()
    {
        var meta = new Dictionary<string, object?>
        {
            ["InputTokenCount"] = 1000L,
            ["OutputTokenCount"] = 500L,
        };
        UsageExtractor.TryExtract(meta).Should().Be((1000, 500));
    }

    [Fact]
    public void Returns_null_when_only_input_present()
    {
        var meta = new Dictionary<string, object?> { ["InputTokenCount"] = 100 };
        UsageExtractor.TryExtract(meta).Should().BeNull();
    }

    [Fact]
    public void TryExtractWithCost_composes_AgentUsage()
    {
        var calc = new ModelPricingCalculator(NullLogger<ModelPricingCalculator>.Instance);
        var meta = new Dictionary<string, object?>
        {
            ["InputTokenCount"] = 1000,
            ["OutputTokenCount"] = 500,
        };

        var usage = UsageExtractor.TryExtractWithCost(meta, "gpt-4o-mini", calc);

        usage.Should().NotBeNull();
        usage!.TokensIn.Should().Be(1000);
        usage.TokensOut.Should().Be(500);
        usage.CostUsd.Should().Be(0.000450m);
    }

    [Fact]
    public void TryExtractWithCost_returns_null_when_metadata_has_no_usage()
    {
        var calc = new ModelPricingCalculator(NullLogger<ModelPricingCalculator>.Instance);
        UsageExtractor.TryExtractWithCost(null, "gpt-4o-mini", calc).Should().BeNull();
    }

    private sealed record FakeUsage(int InputTokenCount, int OutputTokenCount);
}
