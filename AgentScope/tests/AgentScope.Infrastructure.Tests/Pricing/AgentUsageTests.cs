using AgentScope.Application.Abstractions;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Pricing;

public class AgentUsageTests
{
    [Fact]
    public void Add_sums_tokens_and_costs()
    {
        var a = new AgentUsage(100, 50, 0.001m);
        var b = new AgentUsage(200, 80, 0.002m);
        a.Add(b).Should().Be(new AgentUsage(300, 130, 0.003m));
    }

    [Fact]
    public void Add_null_cost_on_both_sides_stays_null()
    {
        var a = new AgentUsage(100, 50, null);
        var b = new AgentUsage(200, 80, null);
        a.Add(b).CostUsd.Should().BeNull();
    }

    [Fact]
    public void Add_null_cost_on_one_side_uses_other()
    {
        var a = new AgentUsage(100, 50, null);
        var b = new AgentUsage(200, 80, 0.002m);
        a.Add(b).CostUsd.Should().Be(0.002m);
        b.Add(a).CostUsd.Should().Be(0.002m);
    }

    [Fact]
    public void Empty_is_all_zeros_with_zero_cost()
    {
        AgentUsage.Empty.Should().Be(new AgentUsage(0, 0, 0m));
    }
}
