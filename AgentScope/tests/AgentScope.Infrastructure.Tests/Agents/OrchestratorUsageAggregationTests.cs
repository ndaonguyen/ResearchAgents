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

public class OrchestratorUsageAggregationTests
{
    [Fact]
    public async Task System_AgentFinishedEvent_sums_tokens_and_cost_across_all_sub_agents()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("agg-1");

        var planner = new FakePlanner(new[] { "q1", "q2" }, new AgentUsage(10, 20, 0.0001m));
        var researcher = new FakeResearcher(new AgentUsage(100, 50, 0.001m));
        var critic = new FakeCritic(
            new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            new AgentUsage(30, 40, 0.0005m));
        var synthesizer = new FakeSynthesizer("FINAL", new AgentUsage(200, 100, 0.002m));

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

        await Task.Delay(30);
        await orchestrator.RunAsync(new AgentRunRequest(runId, "the question"), OrchestratorConfig.Default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAny(subscribe, Task.Delay(Timeout.Infinite, cts.Token));

        var terminal = collected.OfType<AgentFinishedEvent>()
            .Single(e => e.AgentId == AgentId.System);

        // Planner(10/20) + 2×Researcher(100/50) + Critic(30/40) + Synth(200/100) = 440 / 260
        terminal.TokensIn.Should().Be(440);
        terminal.TokensOut.Should().Be(260);

        // 0.0001 + 2*0.001 + 0.0005 + 0.002 = 0.0046
        terminal.EstimatedCostUsd.Should().Be(0.0046m);
    }

    [Fact]
    public async Task System_event_propagates_null_cost_when_all_sub_agents_returned_null_cost()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("agg-2");

        var nullCost = new AgentUsage(10, 5, null);
        var orchestrator = new Orchestrator(
            new FakeKernelFactory(),
            new FakePlanner(new[] { "q" }, nullCost),
            new FakeResearcher(nullCost),
            new FakeCritic(new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()), nullCost),
            new FakeSynthesizer("F", nullCost),
            bus,
            NullLogger<Orchestrator>.Instance);

        var collected = new List<AgentEvent>();
        var subscribe = Task.Run(async () =>
        {
            await foreach (var evt in bus.SubscribeAsync(runId, CancellationToken.None))
                collected.Add(evt);
        });

        await Task.Delay(30);
        await orchestrator.RunAsync(new AgentRunRequest(runId, "q"), OrchestratorConfig.Default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAny(subscribe, Task.Delay(Timeout.Infinite, cts.Token));

        var terminal = collected.OfType<AgentFinishedEvent>()
            .Single(e => e.AgentId == AgentId.System);

        terminal.TokensIn.Should().Be(40);  // 10 * 4 calls
        terminal.TokensOut.Should().Be(20); // 5 * 4 calls
        // AgentUsage.Empty starts with null cost so a run of all-unknown-cost agents
        // surfaces as unknown (not the misleading $0.0000 that "seed dominates" produced
        // before AgentUsage.Empty.CostUsd was changed from 0m to null).
        terminal.EstimatedCostUsd.Should().BeNull();
    }

    // -- fakes --

    private sealed class FakeKernelFactory : IKernelFactory
    {
        public Kernel Create(string? modelOverride = null, bool includePlugins = true) => Kernel.CreateBuilder().Build();
    }

    private sealed class FakePlanner : IPlannerAgent
    {
        private readonly IReadOnlyList<string> _subQuestions;
        private readonly AgentUsage _usage;
        public FakePlanner(IReadOnlyList<string> subQuestions, AgentUsage usage)
        {
            _subQuestions = subQuestions;
            _usage = usage;
        }

        public Task<(IReadOnlyList<string> SubQuestions, AgentUsage Usage)> PlanAsync(
            string question, Kernel kernel, RunId runId, CancellationToken ct = default)
            => Task.FromResult((_subQuestions, _usage));
    }

    private sealed class FakeResearcher : IResearcherAgent
    {
        private readonly AgentUsage _usage;
        public FakeResearcher(AgentUsage usage) => _usage = usage;

        public Task<(ResearchSummary Summary, AgentUsage Usage)> ResearchAsync(
            string subQuestion, int index, Kernel kernel, RunId runId,
            bool searchMemoryFirst = false, CancellationToken ct = default)
            => Task.FromResult((new ResearchSummary(subQuestion, $"body-{index}"), _usage));
    }

    private sealed class FakeCritic : ICriticAgent
    {
        private readonly Critique _critique;
        private readonly AgentUsage _usage;
        public FakeCritic(Critique critique, AgentUsage usage)
        {
            _critique = critique;
            _usage = usage;
        }

        public Task<(Critique Critique, AgentUsage Usage)> CritiqueAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Kernel kernel, RunId runId, CancellationToken ct = default)
            => Task.FromResult((_critique, _usage));
    }

    private sealed class FakeSynthesizer : ISynthesizerAgent
    {
        private readonly string _finalAnswer;
        private readonly AgentUsage _usage;
        public FakeSynthesizer(string finalAnswer, AgentUsage usage)
        {
            _finalAnswer = finalAnswer;
            _usage = usage;
        }

        public Task<(string FinalText, AgentUsage Usage)> SynthesizeAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Critique critique, Kernel kernel, RunId runId, CancellationToken ct = default)
            => Task.FromResult((_finalAnswer, _usage));
    }
}
