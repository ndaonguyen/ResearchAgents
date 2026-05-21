using AgentScope.Application.Abstractions;
using AgentScope.Application.Runs;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.EventBus;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentScope.Application.Tests.Runs;

public class StartRunUseCaseTests
{
    [Fact]
    public async Task Start_returns_events_published_by_the_orchestrator()
    {
        var bus = new ChannelAgentEventBus();
        var fakeOrchestrator = new FakeOrchestrator(bus, async (req, ct) =>
        {
            await bus.PublishAsync(new AgentStartedEvent(req.RunId, new AgentId("r"), "R", DateTime.UtcNow), ct);
            await bus.PublishAsync(new AgentTokenEvent(req.RunId, new AgentId("r"), "hi", DateTime.UtcNow), ct);
            await bus.PublishAsync(new AgentFinishedEvent(req.RunId, AgentId.System, "hi", 0, 0, null, DateTime.UtcNow), ct);
        });

        var useCase = new StartRunUseCase(fakeOrchestrator, bus, NullLogger<StartRunUseCase>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (runId, events) = useCase.Start("hello", cts.Token);

        var collected = new List<AgentEvent>();
        await foreach (var evt in events)
            collected.Add(evt);

        collected.Should().HaveCount(3);
        collected[0].Should().BeOfType<AgentStartedEvent>();
        collected[1].Should().BeOfType<AgentTokenEvent>();
        collected[2].Should().BeOfType<AgentFinishedEvent>();
        collected.Should().OnlyContain(e => e.RunId == runId);
    }

    [Fact]
    public async Task Start_publishes_system_error_event_when_orchestrator_throws()
    {
        var bus = new ChannelAgentEventBus();
        var fakeOrchestrator = new FakeOrchestrator(bus, (_, _) => throw new InvalidOperationException("kaboom"));

        var useCase = new StartRunUseCase(fakeOrchestrator, bus, NullLogger<StartRunUseCase>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (_, events) = useCase.Start("hello", cts.Token);

        var collected = new List<AgentEvent>();
        await foreach (var evt in events)
            collected.Add(evt);

        collected.OfType<AgentErrorEvent>().Should().ContainSingle(err => err.AgentId == AgentId.System);
    }

    private sealed class FakeOrchestrator : IOrchestrator
    {
        private readonly IAgentEventBus _bus;
        private readonly Func<AgentRunRequest, CancellationToken, Task> _behaviour;

        public FakeOrchestrator(IAgentEventBus bus, Func<AgentRunRequest, CancellationToken, Task> behaviour)
        {
            _bus = bus;
            _behaviour = behaviour;
        }

        public Task RunAsync(AgentRunRequest request, CancellationToken ct = default)
            => _behaviour(request, ct);
    }
}
