# Interview practice mode

A second orchestrator on top of the same multi-agent infrastructure, repurposed as an AI interviewer that grounds questions and feedback in the system-design book corpus. Lives at `/interview` in the web app.

## The flow

```
   topic (you pick)
        ↓
   Interviewer  ── grounds the question via SystemDesignCorpus.Search
        ↓
   You answer
        ↓
   Probe        ── reads transcript, asks 0–1 focused follow-up
        ↓
   You answer (if probed; loop max 2 probes)
        ↓
   Grader       ── scores 1–5; uses SystemDesignCorpus.Search to cite gaps
        ↓
   Coach        ── writes feedback summary + study suggestions
        ↓
   Session persisted to Past Runs (Variant = "interview")
```

Four agents (`Interviewer`, `Probe`, `Grader`, `Coach`) share the same shape as the research pipeline agents: a system prompt, a `ChatCompletionAgent`, streaming tokens published to the event bus.

## Prerequisites

1. **System-design corpus indexed** — the Grader and (for stronger questions) the Interviewer call `SystemDesignCorpus.Search`. Without it, questions are still generated but lose the "grounded in the books" advantage.
   ```powershell
   dotnet run --project tools/AgentScope.Indexer
   ```

2. **Corpora enabled in the web app's `appsettings.Development.json`** — same `Corpora[]` block used by research mode. The interview agents share the researcher's kernel, so any enabled corpus is available to them.

## Using it

1. Run the web app from the repo root:
   ```powershell
   dotnet run --project src/AgentScope.Web
   ```

2. Open `https://localhost:7100/interview`.

3. Pick a topic from the dropdown:
   - **Concepts** — caching, sharding, replication, load balancing, queues, rate limiting, consistent hashing, capacity estimation
   - **Design exercises** — URL shortener, chat system, news feed, notification system, rate-limiter service

4. Click **Start interview**. The interviewer streams a question grounded by the corpus.

5. Type your answer in the textarea, click **Submit answer**.

6. **If the probe agent decides you missed something**, you'll see one follow-up question — answer it. The session caps at 2 probes maximum.

7. **When the probe declines (or after 2 probes)**, the grader scores 1–5 with strengths + gaps (gaps include book citations), and the coach writes feedback with suggested reading.

8. Click **Start a new interview** to pick another topic.

## Voice (TTS)

The interviewer's question and any probe follow-ups are read aloud automatically using the browser's built-in `SpeechSynthesis` API — no API key, no cost, runs entirely client-side.

- A **🔊 Voice on / 🔇 Voice off** toggle in the page header lets you mute at any time. State applies to the current tab session.
- Speech triggers when a new interviewer or probe turn is added to the transcript — never on your own answers, never on the grade/coach feedback.
- Voice quality depends on the browser + OS combination. Chrome on macOS and Edge on Windows have the most natural defaults; older Firefox or Linux setups may sound robotic.
- If the browser doesn't expose `SpeechSynthesis` (rare), the toggle hides and the page falls back to text-only silently.
- Autoplay note: browsers gate audio behind user interaction. The Start button counts as that interaction, so all subsequent turns within the session play without prompting.

To upgrade to OpenAI's TTS (better voices, costs cents per session), swap `wwwroot/js/interview-tts.js` for a call to a server endpoint that returns audio bytes from OpenAI's `/v1/audio/speech` API. Out of scope for v1.

## What gets persisted

Each completed session writes one row to the same `results/ui-{yyyyMMdd}.jsonl` the UI runs go to, with:

| Field | Value |
|---|---|
| `Variant` | `"interview"` |
| `Question` | `[Topic name] Interviewer's opening question` |
| `Answer` | Full transcript (interviewer / probe / your answers, in order) |
| `JudgeScore` | Grader's 1–5 score |
| `JudgeReasoning` | Coach's summary + suggested reading |
| `RunId` | Stable ID for cross-referencing with logs |

View them in `/past-runs` alongside research runs and eval runs — filter by clicking the `ui-*.jsonl` file in the top table.

## Behaviour notes

- **The probe agent is conservative.** Strong first answers get zero probes — that's intentional. If you find it never probes, your answers are likely covering the angles; if it always probes, the probe prompt may need tightening.
- **Topic coverage depends on the corpus.** A topic the books don't cover deeply will produce shallow questions and ungrounded gaps. The 13 topics in the dropdown were chosen because they're well-covered in ByteByteGo + Alex Xu.
- **Each agent shows up as its own stage in the event log** (visible in the future if we add an interview-mode event panel). For now, the UI shows only the transcript turns + a "typing…" bubble while an agent is streaming.

## Known limitations

1. **No session resume.** Reloading the page or losing the SignalR circuit mid-session drops the in-memory state. Acceptable for v1; persisting partial sessions would mean storing the transcript per-turn.
2. **No topic-history tracking.** Past Runs shows individual sessions but doesn't aggregate "your average score on caching" or similar trends. Easy follow-on.
3. **Probe limit is hard-coded to 2.** Edit `InterviewSessionUseCase.MaxProbes` to change.
4. **Cost not reported per session.** Token totals from individual agents are captured but not aggregated into the persisted row (the UI's `RunPersister` row currently zeros them for interview rows). Easy fix when needed.
5. **No way to skip the probe stage.** If you want a "quick grade" mode (no probes), it's a future flag on the use case.
6. **Topic list is static.** Adding/removing topics requires editing `InterviewTopics.All`. Configurable via appsettings is a small follow-on.

## Troubleshooting

**Interviewer's question is generic / doesn't cite the corpus**
The interviewer falls back to general LLM knowledge if `SystemDesignCorpus.Search` doesn't find relevant chunks. Verify:
1. The `system-design` corpus is enabled in `appsettings.Development.json`.
2. The indexer populated `agentscope-system-design-corpus` (check the Qdrant dashboard at `http://localhost:6333/dashboard`).
3. The topic actually maps to content in the books — niche topics may return empty.

**Grader scores everything 3/5**
Either the grader's JSON output is malformed (defaults to 3) or your answers are genuinely average. Check the event log for the grader's raw output and the grader prompt if the parsing is failing repeatedly.

**Probe never fires**
The probe agent is conservative by design. If you suspect it should be probing, look at the event log for the probe agent's reasoning. Tighten the probe prompt if it's consistently passing on weak answers.

**Page state lost on browser refresh**
Expected — sessions are in-memory only. Treat each browser tab as one interview.
