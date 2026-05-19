#!/usr/bin/env bash
# Verifies the AgentScope solution builds and tests pass.
# Run from the solution root (where AgentScope.sln lives).

set -euo pipefail

cd "$(dirname "$0")"

echo "==> Checking .NET SDK"
dotnet --version

echo "==> Restoring packages"
dotnet restore

echo "==> Building solution"
dotnet build --no-restore --configuration Debug

echo "==> Running tests (skip integration tests requiring API keys)"
dotnet test --no-build --configuration Debug --logger "console;verbosity=normal"

echo ""
echo "✅ Build & tests OK."
echo ""
echo "Next steps:"
echo "  1. cd src/AgentScope.Web"
echo "  2. dotnet user-secrets set \"AgentScope:OpenAi:ApiKey\" \"sk-...\""
echo "  3. dotnet user-secrets set \"AgentScope:Tavily:ApiKey\" \"tvly-...\""
echo "  4. dotnet run  (then open https://localhost:7100)"
