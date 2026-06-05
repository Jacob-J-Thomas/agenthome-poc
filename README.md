# AgentHome POC

AgentHome is a working-title proof of concept for an engineer-first, local-first agent workspace and runtime.

The goal is not to build another coding agent. The goal is to create a portable agent environment that can survive tool churn across Codex, Claude Code, OpenCode, local scripts, local inference, Azure, AWS, and future agent surfaces.

The POC proves four things:

1. A repository can initialize a local `.agent/` environment with readable docs, policies, memory, tasks, logs, skills, hooks, recipes, and exports.
2. Human/agent permissions can be represented as plain config and evaluated before actions.
3. Conversations can stay ephemeral while tasks and audit events remain durable.
4. The environment can export context for external tools like Codex without making Codex the source of truth.

## Status

This is intentionally small and boring. The first implementation is a CLI and core library. No TUI, no desktop app, no IDE extension, no MCP server, no model calls.

That is deliberate.

## Quick start

From the repository root:

```bash
dotnet run --project src/AgentHome.Cli -- init ./scratch

dotnet run --project src/AgentHome.Cli -- status ./scratch

dotnet run --project src/AgentHome.Cli -- task start "Refactor authentication middleware" ./scratch

dotnet run --project src/AgentHome.Cli -- policy check file.write workspace/shared/demo.txt ./scratch

dotnet run --project src/AgentHome.Cli -- audit 20 ./scratch

dotnet run --project src/AgentHome.Cli -- context export codex ./scratch
```

Generated workspace shape:

```text
scratch/
  .agent/
    AGENT.md
    CONTEXT.md
    MEMORY.md
    models.json
    tools.json
    permissions.json
    tasks/
    logs/events.ndjson
    skills/
    hooks/
    recipes/
    exports/
  workspace/
    private/
    shared/
    generated/
    system/
```

## POC acceptance criteria

The POC is successful when:

- `init` creates a predictable, readable workspace.
- `task start` creates durable task state and logs an audit event.
- `policy check` returns `Allow`, `Prompt`, or `Deny` based on config.
- `audit` reads append-only events.
- `context export codex` produces a markdown handoff file external agents can consume.

The POC is not successful if it becomes a half-finished full agent app.

## Founding position

AgentHome is for engineers first. It assumes users are comfortable with local files, config, source control, CLIs, and explicit policy.

It is not initially a consumer assistant product.

## Provider stance

Core provider strategy, when model execution is added later:

- OpenAI direct API for individual developers.
- Azure AI / Azure OpenAI for enterprise Microsoft environments.
- AWS Bedrock for enterprise AWS environments.
- Local/self-hosted inference for privacy, cost, experimentation, and resilience.

Direct Anthropic should not be a founding pillar. If Anthropic-family models are used through AWS or Azure, that is an AWS/Azure provider path.

## Non-goals for v0

- No direct model calls.
- No autonomous coding agent.
- No plugin marketplace.
- No direct secret exposure to models.
- No desktop app.
- No IDE extension.
- No consumer onboarding flow.
- No background autonomy.

## Next implementation pass

Use `codex-prompts/01-build-and-fix.md` with Codex or a similar coding agent after extracting this repository.
