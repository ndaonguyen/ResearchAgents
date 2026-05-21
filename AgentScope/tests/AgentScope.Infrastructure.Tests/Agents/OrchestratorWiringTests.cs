using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.EventBus;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Agents;

public class OrchestratorWiringTests
{
    [Fact]
    public async Task RunAsync_runs_planner_then_researchers_then_critic_then_synthesizer_in_order()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("run-wiring");

        var subQuestions = new[] { "Sub Q1", "Sub Q2", "Sub Q3" };
        var planner = new FakePlanner(subQuestions);
        var researcher = new FakeResearcher();
        var critic = new FakeCritic(new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
        var synthesizer = new FakeSynthesizer("FINAL ANSWER");

        var orchestrator = new Orchestrator(
            new FakeKernelFactory(),
            planner, researcher, critic, synthesizer,
            bus,
            NullLogger<Orchestrator>.Instance);

        var collected = new List<AgentEvent>();
        var subscribe = Task.Run(async () =>
        {
            await foreach (var evt in bus.SubscribeAsync(runId, CancellationToken.None))
                collected.Add(evt);
        });

        await Task.Delay(30); // ensure subscription registered

        await orchestrator.RunAsync(new AgentRunRequest(runId, "the question"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAny(subscribe, Task.Delay(Timeout.Infinite, cts.Token));

        // Sub-agents were called in the right order.
        planner.Called.Should().BeTrue();
        researcher.Calls.Should().HaveCount(3, "one researcher per sub-question");
        researcher.Calls.Select(c => c.SubQuestion).Should().Equal(subQuestions);
        critic.Called.Should().BeTrue();
        synthesizer.Called.Should().BeTrue();

        // The synthesizer saw the critic's verdict.
        synthesizer.ReceivedCritique!.Ok.Should().BeTrue();

        // The terminal event carries the synthesizer's final answer.
        var terminal = collected.OfType<AgentFinishedEvent>()
            .Single(e => e.AgentId == AgentId.System);
        terminal.FinalText.Should().Be("FINAL ANSWER");
    }

    [Fact]
    public async Task RunAsync_publishes_system_error_when_a_sub_agent_throws()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("run-err");

        var orchestrator = new Orchestrator(
            new FakeKernelFactory(),
            new FakePlanner(new[] { "q1" }),
            new ThrowingResearcher(),
            new FakeCritic(new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())),
            new FakeSynthesizer("unused"),
            bus,
            NullLogger<Orchestrator>.Instance);

        var collected = new List<AgentEvent>();
        var subscribe = Task.Run(async () =>
        {
            await foreach (var evt in bus.SubscribeAsync(runId, CancellationToken.None))
                collected.Add(evt);
        });

        await Task.Delay(30);
        await orchestrator.RunAsync(new AgentRunRequest(runId, "q"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAny(subscribe, Task.Delay(Timeout.Infinite, cts.Token));

        collected.OfType<AgentErrorEvent>()
            .Should().ContainSingle(e => e.AgentId == AgentId.System);
    }

    // -- fakes --

    private sealed class FakeKernelFactory : IKernelFactory
    {
        public Kernel Create() => Kernel.CreateBuilder().Build();
    }

    private sealed class FakePlanner : IPlannerAgent
    {
        private readonly IReadOnlyList<string> _subQuestions;
        public bool Called { get; private set; }

        public FakePlanner(IReadOnlyList<string> subQuestions) => _subQuestions = subQuestions;

        public Task<IReadOnlyList<string>> PlanAsync(
            string question, Kernel kernel, RunId runId, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(_subQuestions);
        }
    }

    private sealed class FakeResearcher : IResearcherAgent
    {
        public List<(string SubQuestion, int Index)> Calls { get; } = new();

        public Task<ResearchSummary> ResearchAsync(
            string subQuestion, int index, Kernel kernel, RunId runId, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((subQuestion, index));
            return Task.FromResult(new ResearchSummary(subQuestion, $"body-{index}"));
        }
    }

    private sealed class ThrowingResearcher : IResearcherAgent
    {
        public Task<ResearchSummary> ResearchAsync(
            string subQuestion, int index, Kernel kernel, RunId runId, CancellationToken ct = default)
            => throw new InvalidOperationException("kaboom");
    }

    private sealed class FakeCritic : ICriticAgent
    {
        private readonly Critique _critique;
        public bool Called { get; private set; }

        public FakeCritic(Critique critique) => _critique = critique;

        public Task<Critique> CritiqueAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Kernel kernel, RunId runId, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(_critique);
        }
    }

    private sealed class FakeSynthesizer : ISynthesizerAgent
    {
        private readonly string _finalAnswer;
        public bool Called { get; private set; }
        public Critique? ReceivedCritique { get; private set; }

        public FakeSynthesizer(string finalAnswer) => _finalAnswer = finalAnswer;

        public Task<string> SynthesizeAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Critique critique, Kernel kernel, RunId runId, CancellationToken ct = default)
        {
            Called = true;
            ReceivedCritique = critique;
            return Task.FromResult(_finalAnswer);
        }
    }
}
