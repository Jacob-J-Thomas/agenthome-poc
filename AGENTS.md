# Agent instructions for this repository

You are working on AgentHome, an engineer-first local agent workspace/runtime.

Do not turn this into a general coding agent. The product is the portable environment and runtime, not the model UI.

## Non-negotiable principles

- Keep the core boring before it is powerful.
- Prefer plain files and explicit config over opaque state.
- Missing or ambiguous policy defaults to human approval.
- Conversations are ephemeral; tasks and audit events are durable.
- Secrets are brokered capabilities, not model-readable context.
- Surfaces are replaceable clients of the runtime.
- Provider support should be conservative and enterprise-aware.

## Implementation rules

- Use C# and the existing project structure unless explicitly asked otherwise.
- Avoid dependencies in the core unless they buy something concrete.
- Do not add a TUI, web app, MCP server, model provider, or database in the POC unless explicitly instructed.
- Keep config files human-readable.
- Preserve backwards compatibility with existing workspace files where practical.
- Add tests when changing policy matching, task storage, or audit logging.

## Current POC scope

The CLI should support:

- `init`
- `status`
- `task start`
- `policy check`
- `audit`
- `context export codex`
