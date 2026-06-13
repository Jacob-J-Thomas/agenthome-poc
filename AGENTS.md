# Agent instructions for this repository

You are working on EmbodySense, a small C# CLI harness POC.

## Current direction

- Keep the implementation small and direct.
- Use C# and the existing small CLI plus core library structure.
- Avoid dependencies unless they buy something concrete.

## Code style

- Prefer single-line method calls and argument lists.
- Do not split method arguments across multiple lines unless there are more than 3 arguments, or keeping one line would make the code genuinely hard to read.
- When a call must be split, use the smallest readable split and avoid cascading vertical formatting through nearby code.

## Project axioms

- Treat `docs/OPINIONATED_PROJECT_AXIOMS.md` as the durable product-direction reference for EmbodySense's long-term harness philosophy.
- Read the axiom file before making design, scope, architecture, or roadmap decisions for this application.
- The axioms are directional context, not authorization to expand the current POC scope on their own.
- If an axiom points toward broader behavior than the current minimal CLI scope allows, keep the implementation minimal and call out the tension to the user.

## Documentation maintenance

- Keep `README.md` aligned with the real CLI behavior.
- Keep `docs/AGENT_LOOP.drawio` aligned with the real implementation whenever the harness loop, inference path, workspace scaffolding, permissions, or audit behavior changes.
- Treat `docs/AGENT_LOOP.drawio` as editable source for diagrams.net / draw.io, not as a generated screenshot.
- Do not describe aspirational agent-loop behavior as implemented unless it exists in `src/EmbodySense.Cli` or `src/EmbodySense.Core`.

## Important docs and entry points

- `README.md`: current POC description and run command.
- `docs/AGENT_LOOP.drawio`: draw.io-compatible source diagram of the current CLI harness loop and its implementation boundaries.
- `docs/OPINIONATED_PROJECT_AXIOMS.md`: durable long-term product-direction reference; read it before design, scope, architecture, or roadmap decisions.
- `EmbodySense.sln`: repository-level solution entry point for Visual Studio and solution builds.
- `src/EmbodySense.Cli/Program.cs`: current CLI entry point and command dispatch.
- `src/EmbodySense.Cli/EmbodySense.Cli.csproj`: the CLI project file.
- `src/EmbodySense.Core/EmbodySense.Core.csproj`: reusable core harness services project file.
- `src/EmbodySense.Core/`: audit, workspace, permissions, and inference services used by the CLI.
- `src/EmbodySense.Core/Harness/AgentHarnessSession.cs`: reusable in-memory harness session state and turn execution.
- `Directory.Build.props`: shared .NET build settings for the repository.
- `docs/adrs/`: intended location for durable architecture decision records; currently empty.
- `tests/`: currently empty; do not imply a test suite exists unless one is added.

## Current POC scope

The CLI currently includes workspace initialization/status, audit-tail display, and a minimal interactive `run` loop. The CLI console loop delegates accumulated chat message state and turn execution to `AgentHarnessSession`, which sends inference requests through `LlmInferenceClient` and the current local Codex CLI provider path using `codex exec -`.

The harness loop does not yet include a first-class action/tool execution broker, direct enforcement of the scaffolded directory permission policy inside `run`, or multi-provider orchestration beyond the current Codex CLI path and placeholder unsupported surfaces.
