using AgentScope.Application.Abstractions;
using AgentScope.Application.Evals;
using AgentScope.Application.Runs;
using AgentScope.Domain.Agents;
using AgentScope.Domain.Events;
using AgentScope.Domain.Runs;
using AgentScope.Infrastructure.EventBus;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentScope.Application.Tests.Evals;

public class EvalRunnerTests
{
    [Fact]
    public async Task RunVariantAsync_writes_one_row_per_question_and_invokes_judge_per_successful_answer()
    {
        var bus = new ChannelAgentEventBus();
        var orchestrator = new FakeOrchestrator(bus, async (req, _, ct) =>
        {
            await bus.PublishAsync(
                new AgentFinishedEvent(req.RunId, AgentId.System, $"answer for {req.Question}", 100, 50, 0.01m, DateTime.UtcNow),
                ct);
        });
        var startRun = new StartRunUseCase(orchestrator, bus, NullLogger<StartRunUseCase>.Instance);

        var judge = new FakeJudge();
        var runner = new EvalRunner(startRun, judge, NullLogger<EvalRunner>.Instance);

        var path = Path.Combine(Path.GetTempPath(), $"eval-runner-test-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new ResultsWriter(path))
        {
            var questions = new[]
            {
                new EvalQuestion("q1", "What is X?"),
                new EvalQuestion("q2", "What is Y?")
            };

            var progress = new List<EvalProgress>();
            await runner.RunVariantAsync(
                new EvalVariant("baseline", OrchestratorConfig.Default),
                questions,
                writer,
                onProgress: p => progress.Add(p),
                ct: CancellationToken.None);

            judge.Calls.Should().HaveCount(2);
            judge.Calls.Select(c => c.Question.Id).Should().Equal("q1", "q2");

            // Two calls per question: a "starting" one (Result null) and a "finished" one.
            progress.Should().HaveCount(4);
            progress.Where(p => p.Result is null).Should().HaveCount(2);
            progress.Where(p => p.Result is not null).Select(p => p.Result!.QuestionId)
                .Should().Equal("q1", "q2");
        }

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().HaveCount(2);

        File.Delete(path);
    }

    [Fact]
    public async Task RunVariantAsync_records_error_row_and_skips_judge_when_orchestrator_emits_system_error()
    {
        var bus = new ChannelAgentEventBus();
        var orchestrator = new FakeOrchestrator(bus, async (req, _, ct) =>
        {
            await bus.PublishAsync(
                new AgentErrorEvent(req.RunId, AgentId.System, "orchestrator failed", DateTime.UtcNow),
                ct);
        });
        var startRun = new StartRunUseCase(orchestrator, bus, NullLogger<StartRunUseCase>.Instance);

        var judge = new FakeJudge();
        var runner = new EvalRunner(startRun, judge, NullLogger<EvalRunner>.Instance);

        var path = Path.Combine(Path.GetTempPath(), $"eval-runner-error-{Guid.NewGuid():N}.jsonl");
        await using (var writer = new ResultsWriter(path))
        {
            await runner.RunVariantAsync(
                new EvalVariant("baseline", OrchestratorConfig.Default),
                new[] { new EvalQuestion("q-err", "Will this fail?") },
                writer,
                ct: CancellationToken.None);
        }

        judge.Calls.Should().BeEmpty();

        var lines = await File.ReadAllLinesAsync(path);
        lines.Should().ContainSingle();
        lines[0].Should().Contain("\"Errored\":true");

        File.Delete(path);
    }

    private sealed class FakeJudge : IAnswerJudge
    {
        public List<(EvalQuestion Question, string Answer)> Calls { get; } = new();

        public Task<JudgeVerdict> ScoreAsync(EvalQuestion question, string answer, CancellationToken ct = default)
        {
            Calls.Add((question, answer));
            return Task.FromResult(new JudgeVerdict(4, "stub", new AgentUsage(10, 5, 0.001m)));
        }
    }

    private sealed class FakeOrchestrator : IOrchestrator
    {
        private readonly IAgentEventBus _bus;
        private readonly Func<AgentRunRequest, OrchestratorConfig, CancellationToken, Task> _behaviour;

        public FakeOrchestrator(IAgentEventBus bus, Func<AgentRunRequest, OrchestratorConfig, CancellationToken, Task> behaviour)
        {
            _bus = bus;
            _behaviour = behaviour;
        }

        public Task RunAsync(AgentRunRequest request, OrchestratorConfig config, CancellationToken ct = default)
            => _behaviour(request, config, ct);
    }
}
