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

3. **Optional — turn on n-of-k judging.** See [Judge sampling](#judge-sampling-n-of-k) below.

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
  "JudgeScore": 4,               // headline = median of JudgeScores
  "JudgeReasoning": "Covers the definition and gives three use cases…",
  "JudgeTokensIn": 612, "JudgeTokensOut": 47,
  "JudgeCostUsd": 0.0001,        // judge-side separately, summed across samples
  "CompletedAt": "2026-05-22T06:06:04.95Z",
  "JudgeScores": [4, 4, 5],      // raw per-sample votes (n-of-k); [s] for single-sample
  "JudgeScoreStdDev": 0.47,      // spread of the votes; null when <2 samples
  "SchemaVersion": 2             // absent on pre-n-of-k rows
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

## Judge sampling (n-of-k)

By default the judge makes **one** call per answer (`Samples = 1`) at temperature 0 — a point estimate with no error bar. Turn on n-of-k self-consistency to measure (and reduce) judge noise. Config lives under `AgentScope:Judge`:

| Key | Default | Notes |
|---|---|---|
| `Samples` | `1` | Independent judge calls per answer. Headline `JudgeScore` is their **median**; the raw votes are kept in `JudgeScores`. |
| `Temperature` | `0.0` | Keep at 0 for a single sample. Raise to ~0.5–0.7 when `Samples > 1`, or every draw is identical and the spread is meaningless. |
| `SeedBase` | `null` | When set, sample `i` uses `SeedBase + i`. Makes the whole panel reproducible across re-runs (best-effort — OpenAI seeds hold only while the backend `system_fingerprint` is stable). |

Example (`appsettings.json` or user secrets):
```json
"Judge": { "Model": "gpt-4o-mini-2024-07-18", "Samples": 5, "Temperature": 0.6, "SeedBase": 1000 }
```

The five calls fan out concurrently, so wall-clock ≈ one call; cost is 5×, but the judge model is cheap relative to the agent run. The implementation is `PanelJudge` wrapping `LlmJudge` — `PanelJudge.Reduce` does the median/std-dev aggregation (unit-tested in `PanelJudgeReduceTests`). Median over mean is deliberate: on an ordinal 1–5 scale one rogue draw shouldn't drag the headline.

**Why bother:** with `JudgeScoreStdDev` recorded per row (and `MeanJudgeDispersion` aggregated per file in the Past Runs viewer), you can finally tell whether a variant's score delta is real or just judge noise — a 4.2-vs-4.0 gap means nothing if the judge's own spread is ±0.6.

## Calibration discipline

Before trusting the leaderboard, hand-grade ~20 outputs and compare with `JudgeScore`. If the judge disagrees with you wildly, tighten the rubric in `LlmJudge.cs` before running larger evals.

n-of-k cuts *variance* but not *bias*: five samples of the same model that's systematically too generous are confidently too generous five times. The next step up — a heterogeneous panel of different judge models — is a one-line change to make `Samples` a list of models instead, but isn't wired yet.

## Troubleshooting

**`ERROR: AgentScope:OpenAi:ApiKey is not configured for the eval CLI`**
You skipped step 1 of setup, or `Host.CreateApplicationBuilder` is reading from the wrong directory. The CLI pins its content root to `AppContext.BaseDirectory` and loads user secrets when `DOTNET_ENVIRONMENT=Development` (the default). Set secrets on the `tests/AgentScope.Evals` project, not on `AgentScope.Web`.

**Every row in the JSONL has `Errored: true` with the same message**
Systemic failure (bad key, Qdrant unreachable, etc.). The runner doesn't bail on per-question errors — it dutifully writes a tombstone row each time. Delete the file, fix the root cause, re-run.

**Per-question timeout**
Hard-coded to 3 minutes. If your orchestrator is genuinely that slow, edit `EvalRunner.PerQuestionTimeout`.
