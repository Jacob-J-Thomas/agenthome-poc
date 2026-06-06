# Agent instructions for this repository

You are working on AgentHome, currently reduced to a minimal C# CLI shell.

## Current direction

- Keep the implementation small and direct.
- Use C# and the existing single-project CLI structure.
- Avoid dependencies unless they buy something concrete.

## Project axioms

- Treat `docs/OPINIONATED_PROJECT_AXIOMS.md` as the durable product-direction reference for AgentHome's long-term harness philosophy.
- Read the axiom file before making design, scope, architecture, or roadmap decisions for this application.
- The axioms are directional context, not authorization to expand the current POC scope on their own.
- If an axiom points toward broader behavior than the current minimal CLI scope allows, keep the implementation minimal and call out the tension to the user.

## Current POC scope

The CLI currently has no real behavior beyond proving the reduced solution builds.
