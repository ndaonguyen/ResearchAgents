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
    public void Empty_has_unknown_cost_not_zero()
    {
        // Null (not zero) is the only correct identity for cost aggregation. Starting
        // from zero would mean Empty.Add(unknown) returns zero, collapsing the
        // "unknown" / "known to be free" distinction the docstring promises.
        AgentUsage.Empty.Should().Be(new AgentUsage(0, 0, null));
    }

    [Fact]
    public void Aggregating_from_Empty_with_all_unknown_costs_stays_unknown()
    {
        // Regression: before this guarantee held, the orchestrator's totalUsage started
        // at zero and Add's (var a, null) => a rule preserved zero through every
        // unknown-cost agent — surfacing $0.0000 to the UI when every per-agent cost
        // was actually unknown (e.g. unrecognised model snapshot).
        var u1 = new AgentUsage(100, 50, null);
        var u2 = new AgentUsage(200, 80, null);

        var total = AgentUsage.Empty.Add(u1).Add(u2);

        total.CostUsd.Should().BeNull();
        total.TokensIn.Should().Be(300);
        total.TokensOut.Should().Be(130);
    }
}
