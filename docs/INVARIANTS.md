# AgentHome invariants

These are the rules the system should not violate.

## 1. The environment is durable

The `.agent/` environment is the source of truth for agent-facing state.

Important state should be local, inspectable, diffable, and recoverable.

## 2. The core is boring before it is powerful

The core should prefer plain files, explicit config, deterministic behavior, small abstractions, append-only logs, and stable CLI commands.

Magic belongs at the edges.

## 3. Ambiguity resolves toward human approval

If policy is missing, invalid, ambiguous, or underspecified, the decision is `Prompt`.

## 4. Boundaries are enforced

Folder conventions are UX. The runtime must enforce policy.

## 5. Chat is not the system of record

Conversations may be ephemeral. Tasks, decisions, artifacts, approvals, and audit logs are durable.

## 6. Secrets are capabilities

Agents do not receive raw secrets. The runtime brokers scoped actions using configured credentials.

## 7. Providers are durable surfaces, not popularity contests

The first-class provider direction is OpenAI direct, Azure, AWS Bedrock, and local/self-hosted inference.

Direct Anthropic is not a founding pillar.

## 8. Surfaces are replaceable

CLI, TUI, web, IDE, and desktop interfaces should all be clients of the runtime, not owners of the architecture.

## 9. Code review belongs in diff tools

Chat and TUI surfaces are not the right place to review code changes.

Agent-authored code should be inspected through source-control diffs in an external IDE, GitHub, or another dedicated diff surface. Runtime clients may summarize changes and link to diffs, but they should not become the code review authority.

## 10. The first audience is engineers

The project assumes technical users who value control, auditability, portability, and explicit configuration.
