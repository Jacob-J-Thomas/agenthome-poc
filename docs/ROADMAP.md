# Roadmap

## Phase 0: Workspace runtime

- Initialize `.agent/` workspace.
- Start durable tasks.
- Append audit events.
- Evaluate policy.
- Export context for Codex.

## Phase 1: Policy hardening

- Add schema validation.
- Add specificity-based policy matching.
- Add dry-run action plans.
- Add structured approval records.
- Add tests for policy behavior.

## Phase 2: Tool execution sandbox

- Add shell command planning without execution.
- Add gated shell execution.
- Add filesystem read/write broker.
- Add per-task capability grants.

## Phase 3: Model provider abstraction

- Add role-based model registry.
- Add OpenAI direct provider.
- Add local provider stub.
- Add Azure provider.
- Add AWS Bedrock provider.

## Phase 4: External agent interop

- Improve Codex export.
- Add Claude/OpenCode export formats only if useful.
- Add import of task summaries from external runs.
- Add adapter contracts.

## Phase 5: Optional surfaces

- TUI.
- Localhost web UI.
- IDE adapter.

Do not begin Phase 5 until the runtime is useful from CLI alone.
