# Interview practice mode

A second orchestrator on top of the same multi-agent infrastructure, repurposed as an AI interviewer that grounds questions and feedback in the system-design book corpus. Lives at `/interview` in the web app.

## Two modes

Pick the mode on the start screen:

| Mode | What it is | Best for |
|---|---|---|
| **Discussion** | Long-form, multi-turn interview. Interviewer asks an open question, you answer, probe agent presses on weaknesses, grader scores 1-5 with citations, coach gives feedback. Hints + Show answer available. | Practising depth, trade-off reasoning, verbal walk-throughs. |
| **Quick check** | One multiple-choice question (single OR multi-select, the agent decides per question). Submit picks → instant grade + RAG-grounded explanation. No probes, no coach. | Concept recall, fast review, drilling specific topics. |

Both modes share the topic picker, the RAG corpus, the persistence layer, and the Past Runs page.

## The flow

```
   topic (you pick)
        ↓
   Interviewer  ── grounds the question via SystemDesignCorpus.Search
        ↓
   You answer  (optionally request a hint, max 2 per session)
        ↓                            ↑
        |                            |
        |     ┌──────────────────────┘
        |     │ Hint  ── corpus-grounded nudge, NOT the answer
        ↓
   Probe        ── reads transcript, asks 0–1 focused follow-up
        ↓
   You answer (if probed; loop max 2 probes)
        ↓
   Grader       ── scores 1–5; sees hints in the transcript
        ↓
   Coach        ── writes feedback summary + study suggestions
        ↓
   Session persisted to Past Runs (Variant = "interview")
```

Five agents (`Interviewer`, `Probe`, `Hint`, `Grader`, `Coach`) share the same shape as the research pipeline agents: a system prompt, a `ChatCompletionAgent`, streaming tokens published to the event bus.

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

## Hints

When you're stuck, click **💡 Hint** to get a small nudge.

- Hints are **RAG-grounded** — the hint agent calls `SystemDesignCorpus.Search` with your transcript-so-far as context, so the nudge comes from the same books that will grade you.
- The prompt explicitly forbids giving the answer. Hints point at the *angle* to consider (e.g. *"Think about what happens to in-flight writes when the leader fails over"* or *"Consider write-through vs write-behind, ByteByteGo pp. 76-80"*) rather than handing you the design.
- **Capped at 2 per session.** The button shows `(N left)` once you've used at least one and disables when you're out.
- Hints appear as their own purple turn in the transcript. The grader sees them, so over-asking will likely cost you score points — a soft penalty that mirrors a real interview where asking for help is allowed but noted.
- TTS speaks hints aloud too if voice is enabled.

## Quick-check (MCQ) mode

Pick **Quick check** on the start screen to drill a topic with multiple-choice questions.

- The `QuickCheckAgent` generates one MCQ at a time, grounded in the system-design corpus — 4 options, with 1 OR multiple marked correct (the agent decides what's appropriate per question). Single-correct renders as radios; multi-correct as checkboxes.
- Submit → grading is **deterministic** (F1 score over picked vs correct option ids, mapped to 1-5). No LLM call for grading, so this mode is cheap and instant.
- Results panel reveals which options were correct (green ✓), which of your picks were wrong (red ✗), the explanation, and the book citations.
- **Drill on the same topic** with the **Next question →** button — generates a fresh MCQ on the same topic without leaving the session. The header shows your question number and running average score (e.g. *"Question 4 · avg 4.2/5"*) once you have more than one question in the session.
- Hit **End session** when you're done to go back to the topic picker.
- Discussion-only features (hints, probes, show-answer button) don't apply in quick-check — the question is short and the answer is multiple-choice; either you know it or you don't.
- Persistence: each MCQ becomes its own row in `ui-{yyyyMMdd}.jsonl` with `Variant = "interview-quickcheck"`. Multi-question sessions get unique `QuestionId`s of the shape `{sessionId}-q{n}` so the Past Runs viewer doesn't collapse them. Group rows by the leading `sessionId` to reconstruct a single drilling session.

**When to use quick-check vs discussion:** drill quick-check on a topic until your running average stabilises at 4-5 — that's your concept recall locked in. Then run a discussion session on the same topic to test whether you can apply it under pressure. The MCQ is the warmup; the discussion is the rep.

## Show answer (give up)

When even the hints aren't enough, click **🏳️ Show answer** to see the model answer for the current question.

- A confirmation prompt appears first — clicking it is irreversible for this session.
- The model-answer agent generates the canonical answer using `SystemDesignCorpus.Search`, structured like a strong interviewer's reasoning (assumptions → key decisions with trade-offs → edge cases), with book + page citations.
- **Score is forced to 0** ("🏳️ Gave up" in the UI, distinct from a real 1-5 score). The session ends immediately — the grader is skipped (no point grading what wasn't attempted), but the **coach still runs** so you get study suggestions based on the gap between your transcript and the model answer.
- The model answer appears as an amber turn in the transcript. It's part of the persisted row in Past Runs, so you can revisit your worked answers.
- Pairs well with the workflow: try a question → use a hint if stuck → if still stuck, show the answer to study it, then come back another day to retry that topic.

## Voice (TTS)

The interviewer's question and any probe follow-ups are read aloud automatically using the browser's built-in `SpeechSynthesis` API — no API key, no cost, runs entirely client-side.

- A **🔊 Voice on / 🔇 Voice off** toggle in the page header lets you mute at any time. State applies to the current tab session.
- Speech triggers when a new interviewer or probe turn is added to the transcript — never on your own answers, never on the grade/coach feedback.
- Voice quality depends on the browser + OS combination. Chrome on macOS and Edge on Windows have the most natural defaults; older Firefox or Linux setups may sound robotic.
- If the browser doesn't expose `SpeechSynthesis` (rare), the toggle hides and the page falls back to text-only silently.
- Autoplay note: browsers gate audio behind user interaction. The Start button counts as that interaction, so all subsequent turns within the session play without prompting.

To upgrade to OpenAI's TTS (better voices, costs cents per session), swap `wwwroot/js/interview-tts.js` for a call to a server endpoint that returns audio bytes from OpenAI's `/v1/audio/speech` API. Out of scope for v1.

## Voice answers (speech-to-text)

Instead of typing, you can speak your answer using the browser's built-in `SpeechRecognition` API — no API key, no cost, transcription happens client-side (with one caveat below).

- A **🎤 Speak** button appears next to **Submit answer** when the browser supports speech recognition.
- Click it once → the button turns red and pulses (**🛑 Stop listening**), and the textarea fills with your words as you talk. Interim guesses appear in a grey preview line below the textarea while the engine is still deciding; the textarea only updates with text the engine has committed.
- Click 🛑 to stop. The transcript stays in the textarea so you can edit it (fix mishearings, add punctuation, clean up filler words) before clicking Submit.
- Submitting auto-stops recording if you forgot.

**Browser support:**
- Chrome / Edge — works. ⚠️ **Privacy note:** Chrome's implementation streams your microphone audio to Google's servers for transcription, even though the API call is client-side. If you don't want that, mute your mic and use the textarea.
- Safari — works (Apple's on-device STT).
- Firefox — experimental and often unavailable; the mic button hides automatically.

**Common error messages:**
- `Microphone permission denied` — the browser blocked mic access. Allow it in the URL bar's permissions menu and try again.
- `No microphone found` — audio device not detected. Check OS mic settings.
- `No speech detected` — silence too long; the engine gave up. Click 🎤 again to restart.

**Upgrade to OpenAI Whisper** for better accuracy (especially on technical jargon) and no Google data path: record audio in the browser → POST to a server endpoint → Whisper API → return transcript. ~1 hour of work; out of scope for v1.

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
