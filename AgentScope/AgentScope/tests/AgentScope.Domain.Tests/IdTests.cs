using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;
using FluentAssertions;
using Xunit;

namespace AgentScope.Domain.Tests;

public class IdTests
{
    [Fact]
    public void RunId_New_produces_unique_values()
    {
        var a = RunId.New();
        var b = RunId.New();

        a.Should().NotBe(b);
        a.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AgentId_equality_is_by_value()
    {
        var a = new AgentId("researcher");
        var b = new AgentId("researcher");

        a.Should().Be(b);
        AgentId.System.Should().Be(new AgentId("system"));
    }
}
