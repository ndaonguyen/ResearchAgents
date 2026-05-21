using AgentScope.Application.Abstractions;
using AgentScope.Infrastructure.Agents;
using FluentAssertions;
using Xunit;

namespace AgentScope.Infrastructure.Tests.Agents;

public class AgentOutputParsingTests
{
    // -- PlannerAgent.ParseSubQuestions --

    [Fact]
    public void Planner_parses_well_formed_json()
    {
        const string json = """{"sub_questions": ["What is X?", "How does X work?", "Compare X to Y."]}""";
        PlannerAgent.ParseSubQuestions(json).Should().Equal("What is X?", "How does X work?", "Compare X to Y.");
    }

    [Fact]
    public void Planner_returns_empty_when_key_missing()
    {
        const string json = """{"other_key": ["foo"]}""";
        PlannerAgent.ParseSubQuestions(json).Should().BeEmpty();
    }

    [Fact]
    public void Planner_returns_empty_when_value_is_not_array()
    {
        const string json = """{"sub_questions": "not an array"}""";
        PlannerAgent.ParseSubQuestions(json).Should().BeEmpty();
    }

    [Fact]
    public void Planner_filters_out_blank_and_non_string_entries()
    {
        const string json = """{"sub_questions": ["valid", "", "  ", 42, null, "also valid"]}""";
        PlannerAgent.ParseSubQuestions(json).Should().Equal("valid", "also valid");
    }

    [Fact]
    public void Planner_returns_empty_on_invalid_json()
    {
        PlannerAgent.ParseSubQuestions("not json at all").Should().BeEmpty();
        PlannerAgent.ParseSubQuestions("").Should().BeEmpty();
    }

    // -- CriticAgent.ParseCritique --

    [Fact]
    public void Critic_parses_well_formed_json()
    {
        const string json = """{"ok": false, "missing_topics": ["security"], "weak_claims": ["'fast' is vague"], "shape_mismatch": ["asks per-chapter, got prose"]}""";
        var critique = CriticAgent.ParseCritique(json);

        critique.Ok.Should().BeFalse();
        critique.MissingTopics.Should().Equal("security");
        critique.WeakClaims.Should().Equal("'fast' is vague");
        critique.ShapeMismatch.Should().Equal("asks per-chapter, got prose");
    }

    [Fact]
    public void Critic_parses_ok_true_with_empty_arrays()
    {
        const string json = """{"ok": true, "missing_topics": [], "weak_claims": [], "shape_mismatch": []}""";
        var critique = CriticAgent.ParseCritique(json);

        critique.Ok.Should().BeTrue();
        critique.MissingTopics.Should().BeEmpty();
        critique.WeakClaims.Should().BeEmpty();
        critique.ShapeMismatch.Should().BeEmpty();
    }

    [Fact]
    public void Critic_defaults_ok_to_false_when_missing()
    {
        const string json = """{"missing_topics": ["a"], "weak_claims": ["b"]}""";
        var critique = CriticAgent.ParseCritique(json);

        critique.Ok.Should().BeFalse();
        critique.MissingTopics.Should().Equal("a");
        critique.WeakClaims.Should().Equal("b");
        critique.ShapeMismatch.Should().BeEmpty();
    }

    [Fact]
    public void Critic_handles_missing_arrays()
    {
        const string json = """{"ok": true}""";
        var critique = CriticAgent.ParseCritique(json);

        critique.Ok.Should().BeTrue();
        critique.MissingTopics.Should().BeEmpty();
        critique.WeakClaims.Should().BeEmpty();
        critique.ShapeMismatch.Should().BeEmpty();
    }

    [Fact]
    public void Critic_returns_not_ok_with_weak_claim_on_invalid_json()
    {
        var critique = CriticAgent.ParseCritique("not json");

        critique.Ok.Should().BeFalse();
        critique.WeakClaims.Should().NotBeEmpty("the parser should surface the parsing failure");
        critique.ShapeMismatch.Should().BeEmpty();
    }

    // -- ResearcherPromptBuilder.Build --

    [Fact]
    public void Researcher_prompt_with_no_prior_context_returns_subquestion_verbatim()
    {
        var prompt = ResearcherPromptBuilder.Build("What is X?", Array.Empty<MemoryHit>());
        prompt.Should().Be("What is X?");
    }

    [Fact]
    public void Researcher_prompt_with_prior_context_prepends_hits_and_gap_instruction()
    {
        var hits = new[]
        {
            new MemoryHit("WebAssembly runs in a sandbox.", 0.9f,
                new Dictionary<string, string> { ["sub_question"] = "What is WASM?" }),
            new MemoryHit("WASM has near-native performance.", 0.85f,
                new Dictionary<string, string>())
        };

        var prompt = ResearcherPromptBuilder.Build(
            "Provide specific facts about WASM security tradeoffs.", hits);

        prompt.Should().Contain("DO NOT restate", "the prompt must tell the researcher to fill gaps");
        prompt.Should().Contain("WebAssembly runs in a sandbox.");
        prompt.Should().Contain("WASM has near-native performance.");
        prompt.Should().Contain("Provide specific facts about WASM security tradeoffs.");
    }
}
