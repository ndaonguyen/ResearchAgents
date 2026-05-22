# Run persistence & Past Runs viewer

Every completed run is persisted to a JSONL file. The web app's **Past runs** page lists them and lets you inspect any past answer, judge verdict, cost, and reasoning.

UI-initiated runs (the Ask button on `/`) and CLI-initiated runs (the [eval harness](./evals.md)) share the same schema and the same `results/` directory, so they show up intermixed in the viewer.

## Where files live

Both writers default to `results/` relative to the **current working directory**. If you launch both the web app and the eval CLI from the repo root, they agree.

| Run source | Filename | Cadence |
|---|---|---|
| Web UI (Ask button) | `ui-{yyyyMMdd}.jsonl` | Rolling daily — every UI run that day appends to one file |
| Eval CLI | `{variant}-{yyyyMMdd-HHmmss}.jsonl` | One file per invocation |

Configure a different path via `AgentScope:Evals:ResultsDirectory` in appsettings (absolute or relative).

## Using the viewer

1. Run the web app (from the repo root so paths match):
   ```powershell
   dotnet run --project src/AgentScope.Web
   ```

2. Open `https://localhost:7100/past-runs`.

3. **File list (top)** — one row per JSONL file, sorted newest first. Columns: file, variant, when, row count, OK/error split, mean judge score, agent cost total, judge cost total.

4. **Click a file** — expands the **row list** below, one row per question with score badge, cost, duration, status.

5. **Click a row** — expands an inline detail panel with: the run ID, full question, full answer (or error message), and the judge's reasoning if present.

6. **Refresh button** — re-reads from disk. New rows from an in-flight eval don't auto-update — click Refresh to pick them up.

## Field differences: UI runs vs CLI runs

Same schema, different fields populated:

| Field | UI run | CLI run |
|---|---|---|
| `Variant` | `"ui"` | the `--variant` flag |
| `QuestionId` | the `RunId` (no separate ID) | from the question file |
| `JudgeScore`, `JudgeReasoning`, `JudgeTokensIn/Out`, `JudgeCostUsd` | `null` / `0` (no judge runs) | populated |
| `Errored = true` on client disconnect | `ErrorMessage = "client disconnected before run completed"` | n/a |

## Known limitations

1. **Working directory coupling.** If you launch the web app and the eval CLI from different directories, they look at different `results/` folders and won't see each other's files. Launch both from the repo root, or set an absolute path in `AgentScope:Evals:ResultsDirectory`.

2. **No live refresh.** Page reads on load + Refresh button. A `FileSystemWatcher` would be a small addition.

3. **Read-only.** No deletion, no two-variant side-by-side comparison view yet. Both are obvious follow-ons.

4. **Disk grows.** UI runs append all day to one file; eval runs make one file each. Trivial for personal use, but no rotation/cleanup job exists.

5. **Privacy.** Every UI question + answer hits disk. Obvious for local dev; worth a thought before any shared deployment.

## Troubleshooting

**Page says "No eval result files found"**
The reader's `ResultsDirectory` resolves to wherever you launched the web app from. The page shows that absolute path — verify the eval CLI actually wrote files there. If you ran the eval from a different folder, copy the JSONLs over or set `AgentScope:Evals:ResultsDirectory` to an absolute path both apps agree on.

**UI runs aren't showing up**
The `RunPersister` is best-effort and logs warnings on failure. Check the web app's console for `Failed to persist UI run {RunId}`. Most likely cause: the configured `ResultsDirectory` isn't writable.
