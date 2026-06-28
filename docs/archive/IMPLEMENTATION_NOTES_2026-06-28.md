# Archived Implementation Notes - 2026-06-28

Status: historical notes only.

This file is not product scope, not a roadmap, not project management, not standing authorization, and not a fixed implementation sequence. The user owns prioritization and may change direction at any time.

Use this only as source-backed context when it helps explain why an older document or memory mentions roadmap-style sequencing. For current decisions, verify the live source, tests, docs, and the user's latest direction.

## Why This Exists

The repository previously had a roadmap-style sequencing note. The user asked to remove active references to roadmap and refactor-plan documents because future sessions should not treat those artifacts as gospel.

The only durable scope anchor is `docs/OPINIONATED_PROJECT_AXIOMS.md`, plus the user's latest direction. README and AGENTS files are operating/status guidance. Diagrams are implementation-status artifacts. Archived planning notes are historical evidence.

## Current Implementation Anchors

- The localhost Web UI is the primary implemented client. The CLI remains a verification and conformance surface.
- Governed model-accessible workspace actions currently flow through the `embodysense.command` dynamic app-server tool and `ToolBroker`.
- Native Codex app-server command, file-change, permission, MCP elicitation, user-input, shell, subagent, and web-search paths remain declined or disabled until EmbodySense-governed equivalents exist.
- Runtime context currently loads the nearest `AGENTS.md` found by walking upward from `--workdir` when that file is non-empty, plus non-empty `.agent/AGENT.md`, `.agent/SOUL.md`, `.agent/PERSONALITY.md`, `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json` under `--workdir`.
- `.agent/MEMORY.md` is intended to be the durable memory registry; conversation history is transcript evidence and should be queried selectively.

## Near-Term Alignment Candidates

These are candidates from the 2026-06-28 alignment review, not a required order.

1. Add first-class loop domain models and persistence in `Core.Application` and `Core.Persistence`. The current default chat behavior should become a governed loop with explicit identity rather than an in-memory prompt/session convention.
2. Persist a default conversation loop and loop-run records before adding scheduling, subagents, graph editing, or broader automation. The first loop model should explain role, authority, trigger, memory scope, tool/capability assignment, review behavior, state, timestamps, and failure posture.
3. Add capability registry groundwork around existing capabilities before expanding the body. Register conversation turns, governed workspace file commands, approvals, audit, transcript history, agent documents, configuration reads, and provider inference with honest implemented/planned/disabled status.

## Useful Deferred Concepts

These concepts appeared in prior sequencing notes. They remain useful design vocabulary, but they are not live work orders.

- Configuration control plane with validated preview/apply/reject transactions.
- Governed memory append/correct/supersede/forget workflows with provenance citations.
- Inspectable compaction artifacts linked from memory instead of silently replacing transcript evidence.
- Failure and replay records that explain what loop ran, what authority was active, what changed, and what should be resumed or repaired.
- Loop graph workspace or other visual loop editor over durable loop definition files.
- Scheduled jobs, heartbeats, hooks, subagents, MCP integrations, skills, and model routing built through the same loop/capability/authority model.
