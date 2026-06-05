# AgentHome workspace spec v0

## Workspace root

Any directory can be an AgentHome workspace if it contains a `.agent/` directory created by the runtime.

## Required files

```text
.agent/
  AGENT.md
  CONTEXT.md
  MEMORY.md
  models.json
  tools.json
  permissions.json
  logs/events.ndjson
```

## Required directories

```text
.agent/
  tasks/
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

## Workspace boundary semantics

`workspace/private/`
: Human-owned. Agent access should require prompt or be denied by default.

`workspace/shared/`
: Coauthored. Agent read is usually allowed. Write usually prompts.

`workspace/generated/`
: Agent-owned generated output. Agent write may be allowed by default.

`workspace/system/`
: Harness/system-owned. User and agent should treat it as runtime-managed.

## Permission decision values

```text
Allow
Prompt
Deny
```

## Permission action examples

```text
file.read
file.write
shell.execute
network.request
secret.use
memory.write
config.write
task.write
audit.append
```

## Policy matching

v0 uses first-match-wins rules from `.agent/permissions.json`.

If no rule matches, `defaultDecision` is returned.

If the file is missing or invalid, the runtime returns `Prompt`.

## Audit log

Audit events are stored as newline-delimited JSON in `.agent/logs/events.ndjson`.

The log is append-only by convention in v0 and should become append-only by enforcement later.
