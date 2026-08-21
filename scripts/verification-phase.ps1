$script:LastCompletedVerificationPhase = "none"

function Reset-VerificationPhaseState {
    $script:LastCompletedVerificationPhase = "none"
}

function Write-VerificationPhaseCapturedOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [Threading.Tasks.Task[string]]$StandardOutputTask,

        [Parameter(Mandatory = $true)]
        [Threading.Tasks.Task[string]]$StandardErrorTask,

        [ValidateRange(1, 60000)]
        [int]$TimeoutMilliseconds = 5000
    )

    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $captureTasks = [Threading.Tasks.Task[]]@($StandardOutputTask, $StandardErrorTask)
    if (-not [Threading.Tasks.Task]::WaitAll($captureTasks, $TimeoutMilliseconds)) {
        [IO.File]::WriteAllText($OutputPath, "Verification output capture did not close within $TimeoutMilliseconds milliseconds.", [Text.UTF8Encoding]::new($false))
        return $false
    }

    [IO.File]::WriteAllText($OutputPath, $StandardOutputTask.GetAwaiter().GetResult() + $StandardErrorTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    return $true
}

function Write-VerificationContext {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$Configuration,

        [Parameter(Mandatory = $true)]
        [string]$VerificationTier
    )

    $head = "unavailable"
    try {
        $candidate = (& git -C $RepositoryRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($candidate)) {
            $head = $candidate.Trim()
        }
    }
    catch {
        $head = "unavailable"
    }

    $context = [ordered]@{
        schemaVersion = 1
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        repositoryHead = $head
        configuration = $Configuration
        verificationTier = $VerificationTier
        machineName = [Environment]::MachineName
        osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        frameworkDescription = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        processorCount = [Environment]::ProcessorCount
        is64BitProcess = [Environment]::Is64BitProcess
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        continuousIntegration = $env:CI
        runnerName = $env:RUNNER_NAME
        runnerOs = $env:RUNNER_OS
        runnerArchitecture = $env:RUNNER_ARCH
        githubRunId = $env:GITHUB_RUN_ID
        githubRunAttempt = $env:GITHUB_RUN_ATTEMPT
        githubSha = $env:GITHUB_SHA
    }
    Write-Output "VERIFY_CONTEXT_JSON=$($context | ConvertTo-Json -Compress)"
}

function Invoke-VerificationPhase {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [ValidateRange(1, 86400)]
        [int]$TimeoutSeconds,

        [string]$WorkingDirectory,

        [string]$OutputPath
    )

    $startedAtUtc = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    Write-Output "VERIFY_PHASE_START name=$Name started_at_utc=$($startedAtUtc.ToString("O")) timeout_seconds=$TimeoutSeconds last_completed=$script:LastCompletedVerificationPhase"

    $startInfo = New-VerificationProcessStartInfo -FileName $FileName -Arguments $Arguments -WorkingDirectory $WorkingDirectory

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $processStarted = $false
    $standardOutputTask = $null
    $standardErrorTask = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
        }
        try {
            if (-not $process.Start()) {
                throw "The process API returned false."
            }

            $processStarted = $true
            if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
                $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
                $standardErrorTask = $process.StandardError.ReadToEndAsync()
            }
        }
        catch {
            throw "Verification phase '$Name' could not start '$FileName'. Last completed phase: '$script:LastCompletedVerificationPhase'. $($_.Exception.Message)"
        }

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-VerificationProcessTree $process
            $processExitedAfterStop = $process.HasExited -or $process.WaitForExit(5000)
            if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
                if ($processExitedAfterStop) {
                    [void](Write-VerificationPhaseCapturedOutput -OutputPath $OutputPath -StandardOutputTask $standardOutputTask -StandardErrorTask $standardErrorTask)
                }
                else {
                    $outputDirectory = Split-Path -Parent $OutputPath
                    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
                    [IO.File]::WriteAllText($OutputPath, "Verification process remained active after bounded tree termination; redirected output was not awaited.", [Text.UTF8Encoding]::new($false))
                }
            }
            $stopwatch.Stop()
            Write-Output "VERIFY_CHILD_TIMEOUT name=$Name timeout_seconds=$TimeoutSeconds elapsed_seconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3))"
            throw "Verification phase '$Name' timed out after $TimeoutSeconds seconds (elapsed $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) seconds). Last completed phase: '$script:LastCompletedVerificationPhase'."
        }

        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            if (-not (Write-VerificationPhaseCapturedOutput -OutputPath $OutputPath -StandardOutputTask $standardOutputTask -StandardErrorTask $standardErrorTask)) {
                throw "Verification phase '$Name' exited but redirected output did not close within the bounded drain window. Last completed phase: '$script:LastCompletedVerificationPhase'."
            }
        }
        $stopwatch.Stop()
        if ($process.ExitCode -ne 0) {
            throw "Verification phase '$Name' exited with code $($process.ExitCode) after $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) seconds. Last completed phase: '$script:LastCompletedVerificationPhase'."
        }

        $script:LastCompletedVerificationPhase = $Name
        Write-Output "VERIFY_PHASE_COMPLETE name=$Name elapsed_seconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) completed_at_utc=$([DateTimeOffset]::UtcNow.ToString("O"))"
    }
    finally {
        if ($processStarted -and -not $process.HasExited) {
            Stop-VerificationProcessTree $process
        }

        $process.Dispose()
    }
}

function New-VerificationProcessStartInfo {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$WorkingDirectory,

        [hashtable]$Environment
    )

    $effectiveFileName = $FileName
    $effectiveArguments = $Arguments
    $commandScriptPath = $null
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        try {
            $resolvedCommand = Get-Command -Name $FileName -CommandType Application -ErrorAction Stop | Select-Object -First 1
            $resolvedPath = $resolvedCommand.Source
            if (-not [string]::IsNullOrWhiteSpace($resolvedPath)) {
                if ([IO.Path]::GetExtension($resolvedPath) -in @(".cmd", ".bat")) {
                    $commandScriptPath = $resolvedPath
                    $effectiveFileName = $env:ComSpec
                }
                else {
                    $effectiveFileName = $resolvedPath
                }
            }
        }
        catch {
            $effectiveFileName = $FileName
            $effectiveArguments = $Arguments
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $effectiveFileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
    }

    if ($null -ne $Environment) {
        foreach ($key in @($Environment.Keys | Sort-Object)) {
            if ([string]::IsNullOrWhiteSpace([string]$key)) {
                throw "Verification process environment names cannot be empty."
            }

            if ($null -ne $startInfo.PSObject.Properties["Environment"]) {
                $startInfo.Environment[[string]$key] = [string]$Environment[$key]
            }
            else {
                $startInfo.EnvironmentVariables[[string]$key] = [string]$Environment[$key]
            }
        }
    }

    # cmd.exe gives /S /C its own quoting semantics. ProcessStartInfo.ArgumentList escapes the
    # embedded command quotes as literal backslashes on modern PowerShell/.NET, so batch files
    # must use the canonical single command-line string even when ArgumentList is available.
    if ($null -ne $commandScriptPath) {
        $commandArguments = ($Arguments | ForEach-Object { ConvertTo-NativeArgument -Value $_ -ForceQuotes }) -join " "
        $argumentSuffix = if ([string]::IsNullOrWhiteSpace($commandArguments)) { "" } else { " $commandArguments" }
        $startInfo.Arguments = '/d /s /c ""{0}"{1}"' -f $commandScriptPath, $argumentSuffix
    }
    elseif ($null -ne $startInfo.PSObject.Properties["ArgumentList"]) {
        foreach ($argument in $effectiveArguments) {
            $startInfo.ArgumentList.Add($argument)
        }
    }
    else {
        $startInfo.Arguments = (($effectiveArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join " ")
    }

    return $startInfo
}

function ConvertTo-NativeArgument {
    param(
        [AllowEmptyString()]
        [string]$Value,

        [switch]$ForceQuotes
    )

    if ([string]::IsNullOrEmpty($Value)) {
        return '""'
    }

    if (-not $ForceQuotes -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\', ($backslashCount * 2) + 1)
            [void]$builder.Append('"')
        }
        else {
            if ($backslashCount -gt 0) {
                [void]$builder.Append('\', $backslashCount)
            }

            [void]$builder.Append($character)
        }

        $backslashCount = 0
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append('\', $backslashCount * 2)
    }

    [void]$builder.Append('"')
    return $builder.ToString()
}

function Stop-VerificationProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process]$Process
    )

    if ($Process.HasExited) {
        return
    }

    try {
        $killTreeMethod = $Process.GetType().GetMethod("Kill", [Type[]]@([bool]))
        if ($null -ne $killTreeMethod) {
            [void]$killTreeMethod.Invoke($Process, @($true))
        }
        elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
            & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
        }
        else {
            $Process.Kill()
        }
    }
    catch {
        if (-not $Process.HasExited) {
            $Process.Kill()
        }
    }
    finally {
        try {
            [void]$Process.WaitForExit(5000)
        }
        catch {
        }
    }
}
