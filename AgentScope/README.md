# AgentScope

A transparent multi-agent research assistant built on Semantic Kernel + Blazor Server, structured as a Clean Architecture .NET 8 solution.

The hook: most multi-agent demos are black boxes. AgentScope shows the agent graph executing live — every tool call, every token, every decision visible in real time.

## Features

### Foundations
- Clean Architecture solution (Domain → Application → Infrastructure → Web)
- Centralised package management (`Directory.Packages.props`)
- Semantic Kernel `ChatCompletionAgent` with auto function calling
- **Per-run event channels** — concurrent runs are isolated
- **AsyncLocal run context** — concurrent agents attribute their tool events correctly
- `IFunctionInvocationFilter` captures every tool call onto the event bus, with no per-tool wiring
- Live token streaming + event log UI over SignalR

### Multi-agent research orchestrator
- **Pipeline**: planner → researchers (parallel fan-out) → critic → synthesizer, behind an `IOrchestrator` port
- **Structured JSON outputs** for the planner (`sub_questions`) and critic (`ok`, `missing_topics`, `weak_claims`, `shape_mismatch`)
- **Critic-driven retry**: when the critic flags a shape mismatch or weak claim, the orchestrator runs one focused researcher pass before synthesis (capped at 1 retry)
- **Per-role model overrides + retry toggle** (`OrchestratorConfig`) — the orchestrator builds one kernel per role so each agent can run on a different model. Used by the eval harness to compare variants.
- Tests for orchestrator wiring, the critic-driven retry, output parsing.

### Practice mode (interview-style learning loop)
- Second orchestrator at `/interview` repurposes the multi-agent infrastructure as a practice coach: **Interviewer → Probe → Hint → Grader → Coach**, plus a **ModelAnswer** agent when the candidate gives up.
- **Two formats**: Discussion (multi-turn Q&A with probes/hints/grader/coach) and QuickCheck (batch of multiple-choice questions with explanations).
- **Curated topic catalogue** across five tracks (System Design, Architecture, AI Engineering, Testing, Code Craft), each split into Concept (building blocks) and Exercise (worked design problems) groups. Per-group steering produces compact code-anchored explanations for Concepts and longer architecture-walkthroughs for Exercises.
- Sessions persist to Past Runs with `Variant = "interview"` / `"interview-quickcheck"`. See [docs/interview-mode.md](docs/interview-mode.md).

### RAG over curated book corpora
- `tools/AgentScope.Indexer` extracts text from PDFs, chunks, embeds with `text-embedding-3-small`, and writes to a Qdrant collection per corpus.
- Each enabled corpus is registered on the kernel as a distinct plugin (e.g. `ArchitectureCorpus.Search`, `SystemDesignCorpus.Search`, `AiEngineeringCorpus.Search`, `TestingCorpus.Search`, `CodeCraftCorpus.Search`); the researcher / interviewer agents pick the most specific corpus per question.
- Web search via Tavily as a fallback when no corpus is relevant.
- `BookLookupPlugin` for Open Library (table-of-contents lookups web search struggles with).
- See [docs/rag-corpora.md](docs/rag-corpora.md).

### Working memory
- **`IWorkingMemory` port** with two implementations:
  - `NullWorkingMemory` (default — app runs without a vector store)
  - `QdrantWorkingMemory` (per-run isolation via a `run_id` payload filter, OpenAI embeddings)

### Cost & token tracking
- **Real token usage** extracted from OpenAI's streaming responses via `stream_options.include_usage` — `AgentFinishedEvent` carries actual `TokensIn`/`TokensOut`.
- **`IUsageCalculator` port + `ModelPricingCalculator`** — per-model USD pricing table (gpt-4o-mini, gpt-4o, gpt-4.1, embeddings) returning `null` for unknown models so the UI can show "—" instead of misleading `$0.00`.
- **Truthful per-model cost** — `UsageExtractor` reads the model name from the OpenAI response metadata (e.g. `gpt-4o-2024-08-06`) and feeds *that* to the pricing calculator. Cached agent model is fallback only — important once per-role overrides are in play.
- **Run-level aggregation** — the terminal `AgentFinishedEvent` sums tokens and cost across every sub-agent invocation (including critic-driven retries).
- **UI surface**: per-agent token chip on each `agent.finished` row, total run cost shown in the header.

### Eval harness
- `tests/AgentScope.Evals` — console CLI that runs a question set through the orchestrator, scores each answer with an LLM-as-judge, and writes JSONL results. Compare quality-vs-cost across orchestrator variants. See [docs/evals.md](docs/evals.md).
- Same harness is available from the UI at `/evals` — pick a question set + per-role models, enqueue, watch progress stream in. In-flight jobs surface as live "Running…" badges on `/past-runs`.

### Run persistence + Past Runs viewer
- Every UI run is persisted to JSONL (`ui-{yyyyMMdd}.jsonl`); the eval CLI writes its own variant-stamped files.
- `/past-runs` page lists all runs with drill-in to answer, judge score, and reasoning. See [docs/persistence-and-past-runs.md](docs/persistence-and-past-runs.md).

### On the roadmap
OpenTelemetry tracing, eval comparison view, embeddings-token tracking.

## Architecture

```
┌─────────────────┐
│   Web (Blazor)  │  composition root, SignalR hub, UI
└────────┬────────┘
         │ depends on ↓
┌────────▼────────────────────────────┐
│   Application                       │  use cases, ports (interfaces)
│   - IAgentEventBus                  │
│   - IResearchAgent                  │
│   - StartRunUseCase                 │
└────────┬─────────────────┬──────────┘
         │                 │
         │                 │
┌────────▼──────┐  ┌───────▼──────────────────────────┐
│   Domain      │  │   Infrastructure                 │
│ - RunId       │  │ - ChannelAgentEventBus (per-run) │
│ - AgentId     │  │ - SemanticKernelResearchAgent    │
│ - AgentEvent  │  │ - KernelFactory + filter         │
│ - …           │  │ - AgentRunContext (AsyncLocal)   │
└───────────────┘  └──────────────────────────────────┘
```

Dependency rule: outer layers depend inward only. The Application layer references nothing about Semantic Kernel — it talks to `IResearchAgent`. Swap SK for LangChain.NET or roll-your-own without touching the use case.

## Prerequisites

- .NET 8 SDK (`global.json` pins to 8.0.0+)
- JetBrains Rider 2024.1+ (or VS 2022 17.10+, or VS Code with C# Dev Kit)
- OpenAI API key — get one at https://platform.openai.com
- Tavily API key — free tier is generous, get one at https://tavily.com

## Setup

### Option A — open in Rider

1. Open `AgentScope.sln` in Rider.
2. Wait for NuGet restore.
3. Right-click the solution → **Manage NuGet Packages** to verify central package management resolved.
4. Set `AgentScope.Web` as the startup project (it should auto-detect from `launchSettings.json`).
5. Configure secrets — see below.
6. Hit Run (Shift+F10 / ▶).

### Option B — command line

```bash
dotnet restore
dotnet build
cd src/AgentScope.Web
dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..."
dotnet user-secrets set "AgentScope:Tavily:ApiKey" "tvly-..."
dotnet run
```

### Configure secrets

API keys never go into `appsettings.json`. Use .NET user-secrets:

```bash
cd src/AgentScope.Web
dotnet user-secrets init
dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..."
dotnet user-secrets set "AgentScope:Tavily:ApiKey" "tvly-..."
```

### Optional — enable Qdrant working memory

Working memory is off by default. To turn it on, run Qdrant locally (e.g. `docker run -p 6334:6334 qdrant/qdrant`) and set:

```bash
dotnet user-secrets set "AgentScope:Qdrant:Enabled" "true"
dotnet user-secrets set "AgentScope:Qdrant:Host" "localhost"
dotnet user-secrets set "AgentScope:Qdrant:Port" "6334"
```

The collection (`agentscope-working-memory` by default) is created lazily on the first researcher write. Per-run isolation is enforced inside `QdrantWorkingMemory` by filtering every search on the `run_id` payload field.

Rider users: right-click `AgentScope.Web` → **Tools** → **Open Project User Secrets**.

The Web project's `UserSecretsId` is `agentscope-web-dev`.

## Running

Once configured, run `AgentScope.Web` and open https://localhost:7100.

Try: *"What's new in .NET 9?"*

You should see:
- `agent.started` for the researcher
- `tool.called` for `WebSearch.Search` with the query in `arguments`
- `tool.result` with a duration
- A stream of `agent.token` events as the response arrives
- `agent.finished` when complete

## Running tests

```bash
dotnet test
```

Or in Rider: **Test Explorer** → run all. Three test projects:

- `AgentScope.Domain.Tests` — value object behaviour
- `AgentScope.Application.Tests` — use case wiring with a fake agent
- `AgentScope.Infrastructure.Tests` — per-run event bus isolation, async-local context

## Project structure

```
AgentScope/
├── AgentScope.sln
├── Directory.Build.props          # Common project settings
├── Directory.Packages.props       # Central package versions
├── global.json                    # SDK pin
├── .editorconfig                  # C# style
├── docs/                          # Feature guides (see links above)
├── src/
│   ├── AgentScope.Domain/         # Entities, value objects, events (zero deps)
│   ├── AgentScope.Application/    # Use cases, ports
│   ├── AgentScope.Infrastructure/ # SK adapters, event bus, plugins
│   └── AgentScope.Web/            # Blazor Server, SignalR hub, Past Runs page
├── tests/
│   ├── AgentScope.Domain.Tests/
│   ├── AgentScope.Application.Tests/
│   ├── AgentScope.Infrastructure.Tests/
│   └── AgentScope.Evals/          # Eval CLI (console app, not unit tests)
└── tools/
    └── AgentScope.Indexer/        # RAG corpus indexer (one-off console app)
```

## Why Clean Architecture for this

1. **The harness is the product.** The `IResearchAgent` and `IAgentEventBus` ports are the API; SK is the current implementation. Designing them as ports (not classes) meant the multi-agent orchestrator and the practice-mode coach could slot in without breaking the use case or the UI.
2. **Testability.** The use case has a real test with a fake agent. The event bus isolation guarantee is verified, not assumed.
3. **Portfolio signal.** "Built a Clean Architecture SK app with proper layer boundaries" is a more credible engineering story than "wrapped LangChain."

## Known limitations (scheduled)

| Limitation | Fix in |
|---|---|
| Embeddings calls (`QdrantWorkingMemory`, indexer, RAG queries) aren't counted toward run cost | Soon |
| No OpenTelemetry tracing | Soon |
| Past Runs page doesn't compare variants side-by-side | Soon |
| Indexer always drops + rebuilds the architecture corpus (no incremental indexing) | As needed |
| Tavily plugin uses default options (no domain filtering, default depth) | As needed |

## License

MIT
