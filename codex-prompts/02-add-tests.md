# Codex prompt: add tests

Goal: add a small test project for the POC core behavior.

Test these behaviors:

- Workspace init creates required files and directories.
- Missing policy returns Prompt.
- First matching policy rule is used.
- Task start creates task JSON and appends an audit event.
- Context export creates a Codex handoff file.

Constraints:

- Keep the implementation small.
- Do not change the workspace spec unless a test exposes a real bug.
- Avoid heavy dependencies.
