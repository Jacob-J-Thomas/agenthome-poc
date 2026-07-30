namespace EmbodySense.Tests.Support;

public static class FakeCodexExecutable
{
    public static async Task<string> CreateCompatibleAsync(TestWorkspace workspace, params string[] advertisedModels)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = workspace.File("fake-codex");
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, "codex.cmd");
        var scriptPath = Path.Combine(directory, "codex.ps1");
        var modelLiterals = string.Join(", ", advertisedModels.Select(model => "'" + model.Replace("'", "''") + "'"));
        await File.WriteAllTextAsync(scriptPath, $$"""
            param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

            if ($Arguments -contains "--version") {
                Write-Output "codex-cli compatible-test"
                exit 0
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
                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "model/list" {
                        $models = @($advertisedModels | ForEach-Object { @{ id = $_; model = $_ } })
                        Write-ProtocolJson @{ id = $message.id; result = @{ data = $models } }
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
