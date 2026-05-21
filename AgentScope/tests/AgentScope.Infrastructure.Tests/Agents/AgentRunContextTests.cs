using AgentScope.Domain.Agents;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Agents;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.SemanticKernel;

public class AgentRunContextTests
{
    [Fact]
    public void Push_sets_current_run_and_agent_ids()
    {
        var ctx = new AgentRunContext();
        var runId = new RunId("r1");
        var agentId = new AgentId("planner");

        using (ctx.Push(runId, agentId))
        {
            ctx.RunId.Should().Be(runId);
            ctx.AgentId.Should().Be(agentId);
        }

        ctx.RunId.Should().BeNull();
        ctx.AgentId.Should().BeNull();
    }

    [Fact]
    public async Task Context_is_isolated_across_concurrent_tasks()
    {
        var ctx = new AgentRunContext();
        var observed = new List<(RunId?, AgentId?)>();
        var lockObj = new object();

        async Task RunWithContext(string suffix)
        {
            using var _ = ctx.Push(new RunId($"r-{suffix}"), new AgentId($"a-{suffix}"));
            // Yield so other tasks can interleave
            await Task.Delay(10);
            lock (lockObj)
            {
                observed.Add((ctx.RunId, ctx.AgentId));
            }
        }

        await Task.WhenAll(
            RunWithContext("1"),
            RunWithContext("2"),
            RunWithContext("3"));

        // Each task should see its own context — no cross-contamination
        observed.Should().HaveCount(3);
        foreach (var (run, agent) in observed)
        {
            run.Should().NotBeNull();
            agent.Should().NotBeNull();
            var runValue = run!.Value.Value;
            var agentValue = agent!.Value.Value;
            runValue.Should().StartWith("r-");
            agentValue.Should().StartWith("a-");
            runValue.Substring(2).Should().Be(agentValue.Substring(2));
        }
    }
}
