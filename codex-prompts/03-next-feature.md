# Codex prompt: next feature after POC

Goal: add structured approval records without adding autonomous execution.

Add:

- `.agent/approvals/`
- approval record model
- `approval request <action> <target>` command
- `approval list` command
- audit events for approval requests

Do not execute approved actions yet. This is still a planning/control-plane feature.
