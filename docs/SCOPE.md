# POC scope

## v0 objective

Create a minimal local runtime that can initialize and operate on a portable `.agent/` workspace.

## v0 commands

- `init [root]`
- `status [root]`
- `task start "goal" [root]`
- `policy check <action> <target> [root]`
- `audit [count] [root]`
- `context export codex [root]`

## v0 does not include

- Model inference.
- MCP execution.
- Shell execution.
- Network execution.
- Secrets vault.
- Cron.
- Hooks execution.
- TUI.
- Web UI.
- IDE extension.
- Database.

## Why so small?

The POC needs to validate the environment model before adding agent power.

If the environment is not useful without model calls, model calls will only hide the design failure.
