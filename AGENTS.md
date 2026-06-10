# Agent instructions for this repository

You are working on EmbodySense, currently reduced to a minimal C# CLI shell.

## Current direction

- Keep the implementation small and direct.
- Use C# and the existing single-project CLI structure.
- Avoid dependencies unless they buy something concrete.

## Project axioms

- Treat `docs/OPINIONATED_PROJECT_AXIOMS.md` as the durable product-direction reference for EmbodySense's long-term harness philosophy.
- Read the axiom file before making design, scope, architecture, or roadmap decisions for this application.
- The axioms are directional context, not authorization to expand the current POC scope on their own.
- If an axiom points toward broader behavior than the current minimal CLI scope allows, keep the implementation minimal and call out the tension to the user.

## Important docs and entry points

- `README.md`: current POC description and run command.
- `docs/OPINIONATED_PROJECT_AXIOMS.md`: durable long-term product-direction reference; read it before design, scope, architecture, or roadmap decisions.
- `EmbodySense.sln`: repository-level solution entry point for Visual Studio and solution builds.
- `src/EmbodySense.Cli/Program.cs`: current CLI behavior.
- `src/EmbodySense.Cli/EmbodySense.Cli.csproj`: the single project file for the current CLI.
- `Directory.Build.props`: shared .NET build settings for the repository.
- `docs/adrs/`: intended location for durable architecture decision records; currently empty.
- `tests/`: currently empty; do not imply a test suite exists unless one is added.

## Current POC scope

The CLI currently has no real behavior beyond proving the reduced solution builds.
