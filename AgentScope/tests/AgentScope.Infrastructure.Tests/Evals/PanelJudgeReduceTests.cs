using AgentScope.Application.Abstractions;
using AgentScope.Application.Evals;
using AgentScope.Infrastructure.Evals;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Evals;

/// <summary>
/// Covers the n-of-k reduction math in <see cref="PanelJudge.Reduce"/> in isolation — no kernel,
/// no model calls. The fan-out itself is a thin Task.WhenAll; the value (and the risk) is here.
/// </summary>
public class PanelJudgeReduceTests
{
    private static JudgeVerdict Sample(int? score, string reasoning = "r", int tin = 10, int tout = 5, decimal? cost = 0.001m)
        => new(score, reasoning, new AgentUsage(tin, tout, cost));

    [Fact]
    public void Single_sample_passes_score_through_with_no_dispersion()
    {
        var result = PanelJudge.Reduce(new[] { Sample(4) });

        result.Score.Should().Be(4);
        result.Scores.Should().Equal(4);
        result.ScoreStdDev.Should().BeNull("one sample has no spread to report");
    }

    [Fact]
    public void Odd_count_takes_the_middle_value_as_median()
    {
        var result = PanelJudge.Reduce(new[] { Sample(5), Sample(3), Sample(4) });

        result.Score.Should().Be(4);
        result.Scores.Should().Equal(3, 4, 5);  // raw votes are kept sorted as the audit artifact
    }

    [Fact]
    public void A_single_outlier_does_not_drag_the_median()
    {
        // Mean would be 3.0; median holds at 4 — the whole point of using median on an ordinal scale.
        var result = PanelJudge.Reduce(new[] { Sample(4), Sample(4), Sample(4), Sample(4), Sample(1) });

        result.Score.Should().Be(4);
    }

    [Fact]
    public void Even_count_rounds_the_two_middle_values_away_from_zero()
    {
        // Sorted [3,4] -> midpoint 3.5 -> rounds to 4.
        var result = PanelJudge.Reduce(new[] { Sample(4), Sample(3) });

        result.Score.Should().Be(4);
    }

    [Fact]
    public void Std_dev_is_population_form_over_the_votes()
    {
        // Votes 2 and 4: mean 3, population variance = ((2-3)^2 + (4-3)^2)/2 = 1, std-dev = 1.
        var result = PanelJudge.Reduce(new[] { Sample(2), Sample(4) });

        result.ScoreStdDev.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Identical_votes_report_zero_dispersion()
    {
        var result = PanelJudge.Reduce(new[] { Sample(4), Sample(4), Sample(4) });

        result.ScoreStdDev.Should().Be(0.0);
    }

    [Fact]
    public void Null_scored_samples_are_dropped_from_the_aggregate()
    {
        var result = PanelJudge.Reduce(new[] { Sample(4), Sample(null), Sample(4) });

        result.Score.Should().Be(4);
        result.Scores.Should().Equal(4, 4);  // the unscored sample is excluded from the votes
        result.ScoreStdDev.Should().Be(0.0);
    }

    [Fact]
    public void All_null_scores_yield_a_null_headline_but_still_sum_cost()
    {
        var result = PanelJudge.Reduce(new[] { Sample(null), Sample(null) });

        result.Score.Should().BeNull();
        result.Scores.Should().BeEmpty();
        result.ScoreStdDev.Should().BeNull();
        result.Usage.CostUsd.Should().Be(0.002m, "failed draws still cost money");
    }

    [Fact]
    public void Usage_is_summed_across_every_sample_including_dropped_ones()
    {
        var result = PanelJudge.Reduce(new[]
        {
            Sample(4, tin: 10, tout: 5, cost: 0.001m),
            Sample(null, tin: 7, tout: 2, cost: 0.0005m),
            Sample(5, tin: 8, tout: 3, cost: 0.0007m),
        });

        result.Usage.TokensIn.Should().Be(25);
        result.Usage.TokensOut.Should().Be(10);
        result.Usage.CostUsd.Should().Be(0.0022m);
    }

    [Fact]
    public void Reasoning_comes_from_the_sample_nearest_the_median()
    {
        var result = PanelJudge.Reduce(new[]
        {
            Sample(1, reasoning: "low"),
            Sample(4, reasoning: "mid"),
            Sample(5, reasoning: "high"),
        });

        result.Score.Should().Be(4);
        result.Reasoning.Should().Be("mid");
    }
}
