using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.EventBus;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.EventBus;

public class ChannelAgentEventBusTests
{
    [Fact]
    public async Task SubscribeAsync_returns_events_for_matching_run_only()
    {
        var bus = new ChannelAgentEventBus();
        var runA = new RunId("run-a");
        var runB = new RunId("run-b");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var subscribeTask = Task.Run(async () =>
        {
            var received = new List<string>();
            await foreach (var evt in bus.SubscribeAsync(runA, cts.Token))
            {
                if (evt is AgentTokenEvent t) received.Add(t.Delta);
            }
            return received;
        });

        await Task.Delay(50); // ensure subscriber registered

        // Interleave events for both runs
        await bus.PublishAsync(new AgentTokenEvent(runA, new AgentId("a"), "A1", DateTime.UtcNow));
        await bus.PublishAsync(new AgentTokenEvent(runB, new AgentId("b"), "B1", DateTime.UtcNow));
        await bus.PublishAsync(new AgentTokenEvent(runA, new AgentId("a"), "A2", DateTime.UtcNow));
        await bus.PublishAsync(new AgentFinishedEvent(runA, AgentId.System, "done", 0, 0, DateTime.UtcNow));

        var received = await subscribeTask;
        received.Should().ContainInOrder("A1", "A2");
        received.Should().NotContain("B1");
    }

    [Fact]
    public async Task SubscribeAsync_completes_on_system_AgentFinishedEvent()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("run-1");

        var subscribeTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in bus.SubscribeAsync(runId, CancellationToken.None))
                count++;
            return count;
        });

        await Task.Delay(50);

        await bus.PublishAsync(new AgentStartedEvent(runId, new AgentId("a"), "A", DateTime.UtcNow));
        await bus.PublishAsync(new AgentFinishedEvent(runId, AgentId.System, "ok", 0, 0, DateTime.UtcNow));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(subscribeTask, Task.Delay(Timeout.Infinite, cts.Token));
        completed.Should().BeSameAs(subscribeTask, "stream should complete on system-level AgentFinishedEvent");

        (await subscribeTask).Should().Be(2);
    }

    [Fact]
    public async Task SubscribeAsync_does_not_terminate_on_subagent_AgentFinishedEvent()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("run-1");

        var subscribeTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in bus.SubscribeAsync(runId, CancellationToken.None))
                count++;
            return count;
        });

        await Task.Delay(50);

        // A sub-agent finishing must NOT end the run — only the orchestrator's
        // system-level AgentFinishedEvent is terminal.
        await bus.PublishAsync(new AgentFinishedEvent(runId, AgentId.Planner, "plan", 0, 0, DateTime.UtcNow));
        await bus.PublishAsync(new AgentFinishedEvent(runId, AgentId.Critic, "critique", 0, 0, DateTime.UtcNow));
        await bus.PublishAsync(new AgentFinishedEvent(runId, AgentId.System, "final", 0, 0, DateTime.UtcNow));

        (await subscribeTask).Should().Be(3);
    }

    [Fact]
    public async Task SubscribeAsync_completes_on_system_AgentErrorEvent()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("run-1");

        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var _ in bus.SubscribeAsync(runId, CancellationToken.None)) { }
        });

        await Task.Delay(50);

        await bus.PublishAsync(new AgentErrorEvent(runId, AgentId.System, "boom", DateTime.UtcNow));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var completed = await Task.WhenAny(subscribeTask, Task.Delay(Timeout.Infinite, cts.Token));
        completed.Should().BeSameAs(subscribeTask);
    }
}
