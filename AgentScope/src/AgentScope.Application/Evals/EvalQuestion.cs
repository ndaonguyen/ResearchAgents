namespace AgentScope.Application.Evals;

/// <summary>
/// One entry in an eval question set. <see cref="Rubric"/> is optional — when null,
/// the judge falls back to its baked-in default rubric. <see cref="ExpectedShape"/>
/// is a free-form list of structural requirements (e.g. "lists each chapter") fed
/// into the judge prompt so it can score shape compliance specifically.
/// </summary>
public sealed record EvalQuestion(
    string Id,
    string Question,
    string? ReferenceAnswer = null,
    string? Rubric = null,
    string[]? ExpectedShape = null);
