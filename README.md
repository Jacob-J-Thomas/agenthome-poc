# EmbodySense POC

Minimal C# CLI shell for the EmbodySense harness POC.

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
