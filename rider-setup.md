# Opening in JetBrains Rider

## First-time setup

1. **Open the solution**: File → Open → select `AgentScope.sln`.

2. **Wait for indexing & restore**: Rider will run `dotnet restore` automatically. Watch the bottom-right status bar; once it's idle, NuGet has resolved.

3. **Set the startup project**: Right-click `AgentScope.Web` in the Explorer → "Set as Startup Project".

4. **Configure secrets** (don't put them in `appsettings.json`):
   - Right-click `AgentScope.Web` → Tools → Open Project User Secrets.
   - Paste:
     ```json
     {
       "AgentScope": {
         "OpenAi": { "ApiKey": "sk-..." },
         "Tavily":  { "ApiKey": "tvly-..." }
       }
     }
     ```
   - Save.

5. **Pick a run config**: top-right dropdown should show `AgentScope.Web` and `AgentScope.Web (HTTP only)`. The HTTPS one is the default.

6. **Run** (Shift+F10 / ▶). Rider opens https://localhost:7100 in your browser.

## Running tests

Open the Unit Tests tool window (Alt+8). All three test projects should appear:
- `AgentScope.Domain.Tests`
- `AgentScope.Application.Tests`
- `AgentScope.Infrastructure.Tests`

Click ▶ on the root node to run everything, or right-click a single test to debug it.

## Troubleshooting

**"Project does not have target framework 'net8.0'."**
You're on .NET 7 or earlier. Install .NET 8 SDK from https://dot.net.

**NuGet restore fails with NU1301**
Network issue or you're behind a corporate proxy. In Rider: File → Settings → Build, Execution, Deployment → NuGet → Sources, verify `nuget.org` is enabled.

**"SKEXP0010: This API is experimental"**
These are suppressed at the solution level via `Directory.Build.props`. If you still see them, Rider's analyzer cache may be stale — `File → Invalidate Caches`.

**"Microsoft.SemanticKernel.Plugins.Web is not found"**
This package is `-alpha`. If a different alpha exists, update `Directory.Packages.props` to match. Run `dotnet list package --outdated` to see current versions.

**Blazor page is blank, no errors in browser console**
Open browser DevTools → Network tab → verify `_framework/blazor.web.js` returns 200. If 404, restart with `dotnet run`. If 500, check the dotnet console for the actual error.

**Tool calls don't appear in the event log**
Verify the agent is actually choosing to call the tool. Try an explicit prompt like "Search the web for the latest .NET release notes". Some questions are answered from training data without tool use.
