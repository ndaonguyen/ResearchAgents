using AgentScope.Application.Abstractions;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.Agents;
using AgentScope.Infrastructure.EventBus;
using AgentScope.Infrastructure.Tests.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Agents;

public class OrchestratorRetryTests
{
    [Fact]
    public void TryDeriveRetryQuestion_returns_false_when_critique_is_ok()
    {
        var critique = new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Orchestrator.TryDeriveRetryQuestion(critique, out var q).Should().BeFalse();
        q.Should().BeEmpty();
    }

    [Fact]
    public void TryDeriveRetryQuestion_prefers_shape_mismatch_over_weak_claim()
    {
        var critique = new Critique(
            false,
            Array.Empty<string>(),
            new[] { "weak claim about X" },
            new[] { "asked to list chapters but got prose" });

        Orchestrator.TryDeriveRetryQuestion(critique, out var q).Should().BeTrue();
        q.Should().Contain("list chapters", "shape_mismatch is the dominant signal");
    }

    [Fact]
    public void TryDeriveRetryQuestion_falls_back_to_weak_claim_when_no_shape_mismatch()
    {
        var critique = new Critique(
            false,
            Array.Empty<string>(),
            new[] { "'fast' lacks numbers" },
            Array.Empty<string>());

        Orchestrator.TryDeriveRetryQuestion(critique, out var q).Should().BeTrue();
        q.Should().Contain("'fast' lacks numbers");
    }

    [Fact]
    public void TryDeriveRetryQuestion_returns_false_when_no_actionable_signal()
    {
        // ok=false but no actionable claims/shape — nothing to retry on.
        var critique = new Critique(
            false,
            new[] { "missing topic Z" },
            Array.Empty<string>(),
            Array.Empty<string>());

        Orchestrator.TryDeriveRetryQuestion(critique, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Critic_not_ok_triggers_exactly_one_retry_researcher_call()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("retry-1");

        var planner = new FakePlanner(new[] { "q1", "q2" });
        var researcher = new FakeResearcher();
        var critic = new FakeCritic(new Critique(
            false,
            Array.Empty<string>(),
            new[] { "fastness lacks numbers" },
            Array.Empty<string>()));
        var synthesizer = new FakeSynthesizer("FINAL");

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

        researcher.Calls.Should().HaveCount(3, "2 initial researchers + 1 critic-driven retry");
        researcher.Calls.Last().SubQuestion.Should().Contain("fastness lacks numbers");

        // Only the retry call asks the researcher to read prior work; parallel calls don't.
        researcher.Calls.Take(2).Should().OnlyContain(c => c.SearchedMemory == false);
        researcher.Calls.Last().SearchedMemory.Should().BeTrue();

        // The retry summary is included in the synthesizer's research list.
        synthesizer.ReceivedResearch!.Should().HaveCount(3);
    }

    [Fact]
    public async Task Critic_ok_does_not_trigger_retry()
    {
        var bus = new ChannelAgentEventBus();
        var runId = new RunId("retry-2");

        var planner = new FakePlanner(new[] { "q1", "q2" });
        var researcher = new FakeResearcher();
        var critic = new FakeCritic(new Critique(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
        var synthesizer = new FakeSynthesizer("FINAL");

        var orchestrator = new Orchestrator(
            new FakeKernelFactory(),
            planner, researcher, critic, synthesizer,
            bus,
            NullLogger<Orchestrator>.Instance);

        await orchestrator.RunAsync(new AgentRunRequest(runId, "q"), OrchestratorConfig.Default);

        researcher.Calls.Should().HaveCount(2, "no retry when critique is ok");
    }

    // -- fakes (mirroring OrchestratorWiringTests) --

    private sealed class FakeKernelFactory : IKernelFactory
    {
        public Kernel Create(string? modelOverride = null, bool includePlugins = true) => Kernel.CreateBuilder().Build();
    }

    private sealed class FakePlanner : IPlannerAgent
    {
        private readonly IReadOnlyList<string> _subQuestions;
        public FakePlanner(IReadOnlyList<string> subQuestions) => _subQuestions = subQuestions;

        public Task<(IReadOnlyList<string> SubQuestions, AgentUsage Usage)> PlanAsync(
            string question, Kernel kernel, RunId runId, CancellationToken ct = default)
            => Task.FromResult((_subQuestions, AgentUsage.Empty));
    }

    private sealed class FakeResearcher : IResearcherAgent
    {
        public List<(string SubQuestion, int Index, bool SearchedMemory)> Calls { get; } = new();

        public Task<(ResearchSummary Summary, AgentUsage Usage)> ResearchAsync(
            string subQuestion, int index, Kernel kernel, RunId runId,
            bool searchMemoryFirst = false, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((subQuestion, index, searchMemoryFirst));
            return Task.FromResult((new ResearchSummary(subQuestion, $"body-{index}"), AgentUsage.Empty));
        }
    }

    private sealed class FakeCritic : ICriticAgent
    {
        private readonly Critique _critique;
        public FakeCritic(Critique critique) => _critique = critique;

        public Task<(Critique Critique, AgentUsage Usage)> CritiqueAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Kernel kernel, RunId runId, CancellationToken ct = default)
            => Task.FromResult((_critique, AgentUsage.Empty));
    }

    private sealed class FakeSynthesizer : ISynthesizerAgent
    {
        private readonly string _finalAnswer;
        public IReadOnlyList<ResearchSummary>? ReceivedResearch { get; private set; }

        public FakeSynthesizer(string finalAnswer) => _finalAnswer = finalAnswer;

        public Task<(string FinalText, AgentUsage Usage)> SynthesizeAsync(
            string originalQuestion, IReadOnlyList<ResearchSummary> research,
            Critique critique, Kernel kernel, RunId runId, CancellationToken ct = default)
        {
            ReceivedResearch = research;
            return Task.FromResult((_finalAnswer, AgentUsage.Empty));
        }
    }
}
