using EmbodySense.Core.Clients.CodexAppServer;
using EmbodySense.Core.Clients.CodexAppServer.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Clients.Tests.CodexAppServer;

[Collection(CodexRuntimeEnvironmentCollection.Name)]
public sealed class CodexRuntimeResolverTests
{
    [Fact]
    public async Task Explicit_compatible_executable_reports_version_and_model()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "explicit", "codex-cli compatible-test", advertisedModels: ["gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
        Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
        Assert.Equal("codex-cli compatible-test", result.Version);
        Assert.Equal("gpt-test", result.ConfiguredModel);
        Assert.Equal("explicit --codex-path", result.Source);
    }

    [Fact]
    public async Task Explicit_incompatible_executable_does_not_fall_back_to_compatible_installation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var explicitExecutable = await CreateFakeExecutableAsync(workspace, "explicit", "codex-cli stale-test", advertisedModels: ["older-model"]);
        var localApplicationData = workspace.File("local-app-data");
        _ = await CreateFakeExecutableAsync(
            workspace,
            Path.Combine("local-app-data", "OpenAI", "Codex", "bin", "current"),
            "codex-cli compatible-test",
            advertisedModels: ["gpt-test"]);
        var originalLocalApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localApplicationData);

            var result = await new CodexRuntimeResolver().ResolveAsync(explicitExecutable, "gpt-test");

            Assert.Equal(CodexRuntimeResolutionStatus.ModelUnavailable, result.Status);
            Assert.Equal(Path.GetFullPath(explicitExecutable), result.ExecutablePath);
            Assert.Equal("codex-cli stale-test", result.Version);
            Assert.Contains("No discovered Codex executable advertises", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalApplicationData);
        }
    }

    [Fact]
    public async Task Desktop_compatible_executable_wins_over_stale_path_candidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var pathDirectory = workspace.File("path");
        _ = await CreateFakeExecutableAsync(workspace, "path", "codex-cli stale-path-test", advertisedModels: ["older-model"]);
        var localApplicationData = workspace.File("local-app-data");
        var desktopExecutable = await CreateFakeExecutableAsync(
            workspace,
            Path.Combine("local-app-data", "OpenAI", "Codex", "bin", "current"),
            "codex-cli desktop-test",
            advertisedModels: ["gpt-test"]);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalLocalApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDirectory);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localApplicationData);

            var result = await new CodexRuntimeResolver().ResolveAsync(null, "gpt-test");

            Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
            Assert.Equal(Path.GetFullPath(desktopExecutable), result.ExecutablePath);
            Assert.Equal("Codex Desktop", result.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalApplicationData);
        }
    }

    [Fact]
    public async Task Model_unavailable_reports_the_candidate_that_supplied_the_actionable_version()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var localApplicationData = workspace.File("local-app-data");
        _ = await CreateFakeExecutableAsync(
            workspace,
            Path.Combine("local-app-data", "OpenAI", "Codex", "bin", "current"),
            "codex-cli broken-desktop-test",
            versionExitCode: 9);
        var pathExecutable = await CreateFakeExecutableAsync(workspace, "path", "codex-cli stale-path-test", advertisedModels: ["older-model"]);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalLocalApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(pathExecutable));
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localApplicationData);

            var result = await new CodexRuntimeResolver().ResolveAsync(null, "gpt-test");

            Assert.Equal(CodexRuntimeResolutionStatus.ModelUnavailable, result.Status);
            Assert.Equal(Path.GetFullPath(pathExecutable), result.ExecutablePath);
            Assert.Equal("codex-cli stale-path-test", result.Version);
            Assert.Equal("PATH", result.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalApplicationData);
        }
    }

    [Fact]
    public async Task Missing_explicit_executable_reports_actionable_status()
    {
        using var workspace = new TestWorkspace();
        var missingExecutable = workspace.File("missing-codex.cmd");

        var result = await new CodexRuntimeResolver().ResolveAsync(missingExecutable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.ExecutableNotFound, result.Status);
        Assert.Equal(Path.GetFullPath(missingExecutable), result.ExecutablePath);
        Assert.Contains("--codex-path", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task App_server_startup_failure_reports_probe_failure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "broken", "codex-cli broken-test", failAppServer: true, advertisedModels: ["gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.ProbeFailed, result.Status);
        Assert.Equal("codex-cli broken-test", result.Version);
        Assert.Contains("simulated app-server startup failure", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Externally_configured_model_still_requires_a_compatible_app_server()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "broken", "codex-cli broken-test", failAppServer: true, advertisedModels: ["gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, null);

        Assert.Equal(CodexRuntimeResolutionStatus.ProbeFailed, result.Status);
        Assert.Contains("simulated app-server startup failure", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_discovered_executable_reports_installation_guidance()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalLocalApplicationData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            Environment.SetEnvironmentVariable("LOCALAPPDATA", "");

            var result = await new CodexRuntimeResolver().ResolveAsync(null, "gpt-test");

            Assert.Equal(CodexRuntimeResolutionStatus.ExecutableNotFound, result.Status);
            Assert.Null(result.ExecutablePath);
            Assert.Contains("Install or update Codex", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalApplicationData);
        }
    }

    [Fact]
    public async Task Explicit_command_name_resolves_from_path()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "path", "codex-cli path-test", advertisedModels: ["gpt-test"]);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(executable));

            var result = await new CodexRuntimeResolver().ResolveAsync("codex.cmd", "gpt-test");

            Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
            Assert.Equal(Path.GetFullPath(executable), result.ExecutablePath);
            Assert.Equal("explicit --codex-path", result.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task Version_probe_failure_reports_process_detail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "broken-version", "codex-cli broken-test", versionExitCode: 9);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.ProbeFailed, result.Status);
        Assert.Contains("simulated version failure", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Externally_configured_model_accepts_a_compatible_app_server()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "compatible", "codex-cli compatible-test");

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, null);

        Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
        Assert.Contains("model selection is externally configured", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_model_catalog_reports_probe_failure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(workspace, "malformed", "codex-cli malformed-test", omitModelCatalog: true);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.ProbeFailed, result.Status);
        Assert.Contains("did not contain a model catalog", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compatible_model_on_a_later_catalog_page_is_accepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(
            workspace,
            "paginated",
            "codex-cli paginated-test",
            modelPageSize: 1,
            advertisedModels: ["older-model", "gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
        Assert.Equal("codex-cli paginated-test", result.Version);
    }

    [Fact]
    public async Task Server_request_during_probe_is_declined_without_aborting_compatibility_check()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(
            workspace,
            "server-request",
            "codex-cli server-request-test",
            requestBeforeInitialize: true,
            advertisedModels: ["gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.Compatible, result.Status);
        Assert.Equal("codex-cli server-request-test", result.Version);
    }

    [Fact]
    public async Task Candidate_probe_uses_one_deadline_across_all_protocol_stages()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var executable = await CreateFakeExecutableAsync(
            workspace,
            "staged-delay",
            "codex-cli staged-delay-test",
            stageDelaySeconds: 6,
            advertisedModels: ["gpt-test"]);

        var result = await new CodexRuntimeResolver().ResolveAsync(executable, "gpt-test");

        Assert.Equal(CodexRuntimeResolutionStatus.ProbeFailed, result.Status);
        Assert.Equal("codex-cli staged-delay-test", result.Version);
        Assert.Contains("timed out after 15 seconds", result.Detail, StringComparison.Ordinal);
    }

    private static async Task<string> CreateFakeExecutableAsync(
        TestWorkspace workspace,
        string relativeDirectory,
        string version,
        bool failAppServer = false,
        int versionExitCode = 0,
        bool omitModelCatalog = false,
        bool requestBeforeInitialize = false,
        int stageDelaySeconds = 0,
        int modelPageSize = int.MaxValue,
        params string[] advertisedModels)
    {
        var directory = workspace.File(relativeDirectory);
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "codex.ps1");
        var commandPath = Path.Combine(directory, "codex.cmd");
        var modelLiterals = string.Join(", ", advertisedModels.Select(model => "'" + model.Replace("'", "''") + "'"));
        await File.WriteAllTextAsync(scriptPath, $$"""
            if ($args -contains "--version") {
                if ({{stageDelaySeconds}} -gt 0) {
                    Start-Sleep -Seconds {{stageDelaySeconds}}
                }

                if ({{versionExitCode}} -ne 0) {
                    [Console]::Error.WriteLine("simulated version failure")
                    exit {{versionExitCode}}
                }

                Write-Output '{{version.Replace("'", "''")}}'
                exit 0
            }

            $failAppServer = ${{failAppServer.ToString().ToLowerInvariant()}}
            $omitModelCatalog = ${{omitModelCatalog.ToString().ToLowerInvariant()}}
            $requestBeforeInitialize = ${{requestBeforeInitialize.ToString().ToLowerInvariant()}}
            $stageDelaySeconds = {{stageDelaySeconds}}
            $modelPageSize = {{modelPageSize}}
            if ($failAppServer) {
                [Console]::Error.WriteLine("simulated app-server startup failure")
                exit 7
            }

            $advertisedModels = @({{modelLiterals}})

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json
                switch ($message.method) {
                    "initialize" {
                        if ($stageDelaySeconds -gt 0) {
                            Start-Sleep -Seconds $stageDelaySeconds
                        }

                        if ($requestBeforeInitialize) {
                            Write-ProtocolJson @{ id = 99; method = "unsupported/probe"; params = @{} }
                            $clientResponse = [Console]::In.ReadLine() | ConvertFrom-Json
                            if ($clientResponse.id -ne 99 -or $clientResponse.error.code -ne -32601) {
                                [Console]::Error.WriteLine("runtime probe did not decline the server request")
                                exit 8
                            }
                        }

                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "model/list" {
                        if ($stageDelaySeconds -gt 0) {
                            Start-Sleep -Seconds $stageDelaySeconds
                        }

                        $offset = if ($null -eq $message.params.cursor) { 0 } else { [int]$message.params.cursor }
                        $page = @($advertisedModels | Select-Object -Skip $offset -First $modelPageSize)
                        $models = @($page | ForEach-Object { @{ id = $_; model = $_ } })
                        if ($omitModelCatalog) {
                            Write-ProtocolJson @{ id = $message.id; result = @{} }
                        } else {
                            $nextOffset = $offset + $page.Count
                            $nextCursor = if ($nextOffset -lt $advertisedModels.Count) { "$nextOffset" } else { $null }
                            Write-ProtocolJson @{ id = $message.id; result = @{ data = $models; nextCursor = $nextCursor } }
                        }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0codex.ps1" %*
            """);
        return commandPath;
    }
}
