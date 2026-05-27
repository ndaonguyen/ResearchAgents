# Eval harness

Runs a question set through the orchestrator, scores each answer with an LLM-as-judge, and writes one JSONL row per question. Used to compare orchestrator variants (different per-role models, retry on/off) on a quality-vs-cost axis.

Two front-ends share the same runner (`AgentScope.Application/Evals/EvalRunner`):

- **CLI** — `tests/AgentScope.Evals/`. Best for batch runs and CI.
- **UI** — `/evals` page in the Web app. Best for ad-hoc one-off variants; shows live progress and feeds straight into `/past-runs`.

## Setup

1. **Set API keys for the eval project** (user secrets are isolated per project):
   ```powershell
   dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..." --project tests/AgentScope.Evals
   dotnet user-secrets set "AgentScope:Tavily:ApiKey" "tvly-..." --project tests/AgentScope.Evals
   ```
   Or copy `src/AgentScope.Web/appsettings.Development.json` to `tests/AgentScope.Evals/appsettings.Development.json`.

2. **Optional — pick a cheaper judge model.** Default is `gpt-4o-mini` (in `tests/AgentScope.Evals/appsettings.json` under `AgentScope:Judge:Model`). Override via user secrets or appsettings.

## Running a variant

From the **repo root** (the CLI writes `results/` relative to the current directory):

```powershell
dotnet run --project tests/AgentScope.Evals -- `
  --variant baseline `
  --questions tests/AgentScope.Evals/questions/sample.json
```

Console output:
```
[baseline] 1/5  wasm-basics: What is WebAssembly, and what are its main use cases…
          OK  score=4  cost=$0.0023  duration=18432ms
```

A JSONL appears at `results/baseline-{timestamp}.jsonl` with one row per question.

## Comparing variants

Run multiple times with different flags:

```powershell
# cheap researchers, premium synthesizer
dotnet run --project tests/AgentScope.Evals -- `
  --variant mixed `
  --researcher-model gpt-4o-mini `
  --synthesizer-model gpt-4o `
  --questions tests/AgentScope.Evals/questions/sample.json

# no critic-driven retry
dotnet run --project tests/AgentScope.Evals -- `
  --variant no-retry `
  --no-retry `
  --questions tests/AgentScope.Evals/questions/sample.json
```

View results side-by-side in the [Past Runs viewer](./persistence-and-past-runs.md) at `/past-runs`.

## All CLI flags

| Flag | Default | Notes |
|---|---|---|
| `--variant <label>` | `baseline` | Label written into every row's `Variant` field. |
| `--questions <path>` | `questions/sample.json` | JSON array of `EvalQuestion` objects. |
| `--out <path>` | `results/<variant>-<timestamp>.jsonl` | Output JSONL path. |
| `--planner-model <id>` | (use default) | Override planner model. |
| `--researcher-model <id>` | (use default) | Override researcher model. |
| `--critic-model <id>` | (use default) | Override critic model. |
| `--synthesizer-model <id>` | (use default) | Override synthesizer model. |
| `--no-retry` | retry on | Disable the critic-driven retry researcher pass. |

## Question file schema

```json
[
  {
    "id": "wasm-basics",
    "question": "What is WebAssembly?",
    "expectedShape": ["briefly defines WebAssembly"],
    "rubric": "(optional) custom rubric for this question",
    "referenceAnswer": "(optional) reference answer for the judge"
  }
]
```

- `id` — stable identifier; appears in result rows so you can correlate across variant runs.
- `expectedShape` — bullet list of structural requirements fed into the judge prompt.
- `rubric` — optional per-question rubric; falls back to the judge's default rubric (1-5 scale on accuracy, completeness, shape, citations).
- `referenceAnswer` — optional gold answer; if present, the judge sees it as a comparison.

## Result row schema

Each line is an `EvalResult`:
```jsonc
{
  "QuestionId": "wasm-basics",
  "Variant": "baseline",
  "Question": "...",
  "RunId": "7e8d970e...",        // for cross-referencing with logs
  "Answer": "WebAssembly is...",
  "TokensIn": 1842, "TokensOut": 489,
  "CostUsd": 0.0023,             // agent-side only
  "DurationMs": 18432,
  "Errored": false, "ErrorMessage": null,
  "JudgeScore": 4,
  "JudgeReasoning": "Covers the definition and gives three use cases…",
  "JudgeTokensIn": 612, "JudgeTokensOut": 47,
  "JudgeCostUsd": 0.0001,        // judge-side separately
  "CompletedAt": "2026-05-22T06:06:04.95Z"
}
```

## Running evals from the UI

1. Open `/evals` in the Web app.
2. Pick a variant label, a question set (auto-listed from `AgentScope:Evals:QuestionsDirectory`, defaults to `tests/AgentScope.Evals/questions`), per-role model overrides, and the retry toggle.
3. Click **Enqueue eval**. The page shows the job's progress live; multiple jobs queue and execute sequentially (same rate-limit reasoning as the CLI).
4. Cancel an in-flight job with the row's **Cancel** button. The worker drops it on the next question boundary; partial JSONL is preserved.
5. The `/past-runs` page renders a pulsing **Running…** badge next to any file an active job is currently writing — no refresh needed.

The UI writes to the same JSONL files the CLI does, named `{variant}-{yyyyMMdd-HHmmss}.jsonl`. Configuration lives under `AgentScope:Evals`:

```json
"Evals": {
  "ResultsDirectory": "results",
  "QuestionsDirectory": "tests/AgentScope.Evals/questions"
}
```

## Calibration discipline

Before trusting the leaderboard, hand-grade ~20 outputs and compare with `JudgeScore`. If the judge disagrees with you wildly, tighten the rubric in `LlmJudge.cs` before running larger evals.

## Troubleshooting

**`ERROR: AgentScope:OpenAi:ApiKey is not configured for the eval CLI`**
You skipped step 1 of setup, or `Host.CreateApplicationBuilder` is reading from the wrong directory. The CLI pins its content root to `AppContext.BaseDirectory` and loads user secrets when `DOTNET_ENVIRONMENT=Development` (the default). Set secrets on the `tests/AgentScope.Evals` project, not on `AgentScope.Web`.

**Every row in the JSONL has `Errored: true` with the same message**
Systemic failure (bad key, Qdrant unreachable, etc.). The runner doesn't bail on per-question errors — it dutifully writes a tombstone row each time. Delete the file, fix the root cause, re-run.

**Per-question timeout**
Hard-coded to 3 minutes. If your orchestrator is genuinely that slow, edit `EvalRunner.PerQuestionTimeout`.
