# Codex prompt: build and fix POC

You are working in the AgentHome POC repository.

Goal: make the current C# CLI build and run without changing the product direction.

Steps:

1. Inspect `README.md`, `AGENTS.md`, `docs/INVARIANTS.md`, and `docs/SCOPE.md`.
2. Run `dotnet build src/AgentHome.Cli/AgentHome.Cli.csproj`.
3. Fix compile errors only.
4. Run these commands and fix runtime issues:

```bash
dotnet run --project src/AgentHome.Cli -- init ./scratch
dotnet run --project src/AgentHome.Cli -- status ./scratch
dotnet run --project src/AgentHome.Cli -- task start "Refactor authentication middleware" ./scratch
dotnet run --project src/AgentHome.Cli -- policy check file.write workspace/shared/demo.txt ./scratch
dotnet run --project src/AgentHome.Cli -- audit 20 ./scratch
dotnet run --project src/AgentHome.Cli -- context export codex ./scratch
```

5. Do not add model calls, MCP, a TUI, a web app, or a database.
6. Produce a short summary of what changed and whether the POC meets the README acceptance criteria.
