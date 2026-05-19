# AgentScope

A transparent multi-agent research assistant built on Semantic Kernel + Blazor Server, structured as a Clean Architecture .NET 8 solution.

The hook: most multi-agent demos are black boxes. AgentScope shows the agent graph executing live — every tool call, every token, every decision visible in real time.

## Status — Week 1 (foundations)

What works:
- Clean Architecture solution (Domain → Application → Infrastructure → Web)
- Centralised package management (`Directory.Packages.props`)
- Semantic Kernel `ChatCompletionAgent` with auto function calling
- Tavily web search plugin registered as a kernel function
- **Per-run event channels** — concurrent runs are isolated (fixes the v1 shared-bus issue)
- **AsyncLocal run context** — concurrent agents attribute their tool events correctly
- `IFunctionInvocationFilter` captures every tool call onto the event bus, with no per-tool wiring
- Live token streaming + event log UI over SignalR
- Tests for the domain, the use case, the event bus isolation, and the async-local context

Coming in week 2: planner → researcher(s) → critic → synthesizer orchestration, Qdrant working memory.

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
├── src/
│   ├── AgentScope.Domain/         # Entities, value objects, events (zero deps)
│   ├── AgentScope.Application/    # Use cases, ports
│   ├── AgentScope.Infrastructure/ # SK adapters, event bus impl
│   └── AgentScope.Web/            # Blazor Server, SignalR hub, composition root
└── tests/
    ├── AgentScope.Domain.Tests/
    ├── AgentScope.Application.Tests/
    └── AgentScope.Infrastructure.Tests/
```

## Why Clean Architecture for this

1. **The harness is the product.** The `IResearchAgent` and `IAgentEventBus` ports are the API; SK is the current implementation. Designing them as ports (not classes) means week 2's orchestrator slots in without breaking the use case or the UI.
2. **Testability.** The use case has a real test with a fake agent. The event bus isolation guarantee is verified, not assumed.
3. **Portfolio signal.** "Built a Clean Architecture SK app with proper layer boundaries" is a more credible engineering story than "wrapped LangChain."

## Known limitations (scheduled)

| Limitation | Fix in |
|---|---|
| `AgentFinishedEvent` reports 0 tokens (no usage tracking) | Week 4 |
| No persistence — runs are in-memory only | Week 4 |
| No OpenTelemetry tracing | Week 4 |
| Tavily plugin uses default options (no domain filtering, default depth) | As needed |

## License

MIT
