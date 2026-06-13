# EmbodySense POC

Minimal C# CLI shell for the EmbodySense harness POC.

## Intended Application Control Flow

EmbodySense is currently split into a small CLI project and a core library. The CLI owns command parsing and console output; the core library owns reusable harness services.

The intended flow is:

1. `Program` creates `CliArguments`, handles top-level failure reporting, and dispatches to command wrappers.
2. `Command` types own CLI concerns: argument interpretation, command-specific console output, and orchestration of domain services.
3. `EmbodySense.Core` domain namespaces own reusable behavior:
   - `Workspace` scaffolds and resolves the `.agent/` and `workspace/` environment.
   - `Permissions` loads and evaluates the directory permission policy.
   - `Audit` writes and reads structured audit events.
   - `Harness` owns reusable session state and turn execution.
   - `Inference` owns model/provider request and response handling.
4. `AgentHarnessLoop` remains in the CLI project because it owns console input/output while it coordinates core session and inference services.
5. `run` currently flows through `Program` -> `RunCommand` -> `AgentHarnessLoop` -> `AgentHarnessSession` -> `LlmInferenceClient` -> provider client, currently `CodexCliInferenceClient`.

Project and namespace separation are ownership and dependency boundaries, not security boundaries by themselves. Security-sensitive decisions must be enforced by runtime policy checks, explicit approvals, and auditable actions. Core namespaces should not depend on `EmbodySense.Cli`; command wrappers may depend on core services. Future tool-turn behavior should preserve that direction by keeping reusable execution state in `EmbodySense.Core` while leaving console-specific parsing and output in the CLI project.

Current limitation: the generated directory permission policy is scaffolded and visible through `status`, but `run` does not yet enforce it as a tool execution boundary.

## Run

From the repository root:

```powershell
dotnet run --project src\EmbodySense.Cli -- --help
dotnet run --project src\EmbodySense.Cli -- run
```

## Run From Scratch

The `scratch/` folder is useful for exercising the CLI against a disposable local workspace.

```powershell
cd C:\Users\98jak\source\repos\agenthome-poc\scratch
```

From there, run the CLI through the project path relative to `scratch/`:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- --help
```

Initialize the scratch workspace:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- init .
```

Check scratch workspace status:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- status .
```

Start the harness loop from scratch:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir .
```

Inside the harness loop, type a message and press Enter. Type `/exit`, `/quit`, `exit`, or `quit` to leave.

## Harness Run Options

The `run` command currently routes inference through the local Codex CLI.

The current implementation flow is documented in the draw.io-compatible source diagram at [`docs/AGENT_LOOP.drawio`](docs/AGENT_LOOP.drawio). Keep that diagram aligned with the README when the real CLI loop, inference, workspace, permission, or audit behavior changes.

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir . --model gpt-5.4
```

Available `run` options:

- `--model <model>` or `-m <model>`: choose the Codex model.
- `--workdir <path>` or `--working-directory <path>`: set the working directory for Codex.
- `--codex-path <path>`: use a specific Codex executable.
- `--sandbox <mode>`: pass Codex sandbox mode, such as `read-only` or `workspace-write`.
- `--approval <policy>`: pass Codex approval policy, such as `never` or `on-request`.
- `--persist-session`: do not use ephemeral Codex sessions.
- `--skip-git-repo-check`: allow Codex to run outside a Git repository.

Before running real inference, make sure Codex is installed and authenticated:

```powershell
codex.cmd login
```
