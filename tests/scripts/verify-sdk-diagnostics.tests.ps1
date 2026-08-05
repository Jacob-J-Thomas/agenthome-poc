Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-verify-sdk-{0}" -f [Guid]::NewGuid().ToString("N"))
$powershellPath = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)

if (-not $runningOnWindows) {
    throw "The verifier SDK process harness currently requires Windows because repository verification runs on windows-latest and fake dotnet uses a command script."
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Scenario
    )

    if ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw "$Scenario did not contain expected text '$Expected'. Actual output:`n$Actual"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$Unexpected,

        [Parameter(Mandatory = $true)]
        [string]$Scenario
    )

    if ($Actual.IndexOf($Unexpected, [StringComparison]::Ordinal) -ge 0) {
        throw "$Scenario unexpectedly contained '$Unexpected'. Actual output:`n$Actual"
    }
}

function Set-ScenarioFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [AllowEmptyString()]
        [string]$Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-VerifierScenario {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [switch]$CommandNotFound,

        [AllowEmptyString()]
        [string]$VersionStandardOutput = "",

        [AllowEmptyString()]
        [string]$VersionStandardError = "",

        [int]$VersionExitCode = 0,

        [AllowEmptyString()]
        [string]$ListStandardOutput = "10.0.301 [C:\Program Files\dotnet\sdk]",

        [AllowEmptyString()]
        [string]$ListStandardError = "",

        [int]$ListExitCode = 0,

        [string[]]$VerifierArguments = @()
    )

    $scenarioRoot = Join-Path $tempRoot $Name
    $scriptsPath = Join-Path $scenarioRoot "scripts"
    $testResultsPath = Join-Path $scenarioRoot "tests\Fake.Tests\TestResults"
    $fakeBinPath = Join-Path $scenarioRoot "fake-bin"
    [void](New-Item -ItemType Directory -Path $scriptsPath, $testResultsPath, $fakeBinPath -Force)
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\verify.ps1") -Destination $scriptsPath
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\verify-sdk.ps1") -Destination $scriptsPath
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\verification-phase.ps1") -Destination $scriptsPath
    Copy-Item -LiteralPath (Join-Path $repoRoot "global.json") -Destination $scenarioRoot

    $sentinelPath = Join-Path $testResultsPath "sentinel.txt"
    Set-ScenarioFile -Path $sentinelPath -Content "must survive SDK bootstrap failure"
    $callLogPath = Join-Path $scenarioRoot "dotnet-calls.log"
    $workingDirectoryLogPath = Join-Path $scenarioRoot "dotnet-working-directories.log"
    $versionOutputPath = Join-Path $scenarioRoot "version-stdout.txt"
    $versionErrorPath = Join-Path $scenarioRoot "version-stderr.txt"
    $listOutputPath = Join-Path $scenarioRoot "list-stdout.txt"
    $listErrorPath = Join-Path $scenarioRoot "list-stderr.txt"
    Set-ScenarioFile -Path $versionOutputPath -Content $VersionStandardOutput
    Set-ScenarioFile -Path $versionErrorPath -Content $VersionStandardError
    Set-ScenarioFile -Path $listOutputPath -Content $ListStandardOutput
    Set-ScenarioFile -Path $listErrorPath -Content $ListStandardError

    if (-not $CommandNotFound) {
        $fakeDotnet = @'
@echo off
echo %*>>"%EMBODYSENSE_VERIFY_FAKE_CALL_LOG%"
echo %CD%>>"%EMBODYSENSE_VERIFY_FAKE_WORKING_DIRECTORY_LOG%"
if "%~1"=="--version" (
  if exist "%EMBODYSENSE_VERIFY_FAKE_VERSION_STDOUT%" type "%EMBODYSENSE_VERIFY_FAKE_VERSION_STDOUT%"
  if exist "%EMBODYSENSE_VERIFY_FAKE_VERSION_STDERR%" type "%EMBODYSENSE_VERIFY_FAKE_VERSION_STDERR%" 1>&2
  exit /b %EMBODYSENSE_VERIFY_FAKE_VERSION_EXIT%
)
if "%~1"=="--list-sdks" (
  if exist "%EMBODYSENSE_VERIFY_FAKE_LIST_STDOUT%" type "%EMBODYSENSE_VERIFY_FAKE_LIST_STDOUT%"
  if exist "%EMBODYSENSE_VERIFY_FAKE_LIST_STDERR%" type "%EMBODYSENSE_VERIFY_FAKE_LIST_STDERR%" 1>&2
  exit /b %EMBODYSENSE_VERIFY_FAKE_LIST_EXIT%
)
exit /b 71
'@
        Set-ScenarioFile -Path (Join-Path $fakeBinPath "dotnet.cmd") -Content $fakeDotnet
    }

    $verifierPath = Join-Path $scriptsPath "verify.ps1"
    $verifierArgumentText = (($VerifierArguments | ForEach-Object { '"{0}"' -f $_ }) -join " ")
    $launcherPath = Join-Path $scenarioRoot "run-verifier.cmd"
    $launcher = @"
@echo off
set "PATH=$fakeBinPath"
set "EMBODYSENSE_VERIFY_FAKE_CALL_LOG=$callLogPath"
set "EMBODYSENSE_VERIFY_FAKE_WORKING_DIRECTORY_LOG=$workingDirectoryLogPath"
set "EMBODYSENSE_VERIFY_FAKE_VERSION_STDOUT=$versionOutputPath"
set "EMBODYSENSE_VERIFY_FAKE_VERSION_STDERR=$versionErrorPath"
set "EMBODYSENSE_VERIFY_FAKE_VERSION_EXIT=$VersionExitCode"
set "EMBODYSENSE_VERIFY_FAKE_LIST_STDOUT=$listOutputPath"
set "EMBODYSENSE_VERIFY_FAKE_LIST_STDERR=$listErrorPath"
set "EMBODYSENSE_VERIFY_FAKE_LIST_EXIT=$ListExitCode"
"$powershellPath" -NoProfile -ExecutionPolicy Bypass -File "$verifierPath" -SkipCoverage $verifierArgumentText
exit /b %ERRORLEVEL%
"@
    Set-ScenarioFile -Path $launcherPath -Content $launcher
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $env:ComSpec
    $startInfo.Arguments = '/d /s /c ""{0}""' -f $launcherPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        [void]$process.Start()
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $calls = if (Test-Path -LiteralPath $callLogPath) { @(Get-Content -LiteralPath $callLogPath) } else { @() }
        $workingDirectories = if (Test-Path -LiteralPath $workingDirectoryLogPath) { @(Get-Content -LiteralPath $workingDirectoryLogPath) } else { @() }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = ($standardOutput + [Environment]::NewLine + $standardError).Trim()
            Calls = $calls
            WorkingDirectories = $workingDirectories
            RepositoryRoot = $scenarioRoot
            SentinelExists = Test-Path -LiteralPath $sentinelPath
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-BootstrapFailureStoppedEarly {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Result,

        [Parameter(Mandatory = $true)]
        [string]$Scenario
    )

    Assert-True -Condition ($Result.ExitCode -ne 0) -Message "$Scenario unexpectedly succeeded."
    Assert-True -Condition $Result.SentinelExists -Message "$Scenario removed TestResults before SDK validation completed."
    $callText = $Result.Calls -join [Environment]::NewLine
    Assert-NotContains -Actual $callText -Unexpected "build" -Scenario $Scenario
    Assert-NotContains -Actual $callText -Unexpected "test" -Scenario $Scenario
    Assert-NotContains -Actual $callText -Unexpected "format" -Scenario $Scenario
    Assert-NotContains -Actual $Result.Output -Unexpected "System.Object[]" -Scenario $Scenario
    Assert-NotContains -Actual $Result.Output -Unexpected "Write-Error:" -Scenario $Scenario
    Assert-Contains -Actual $Result.Output -Expected "Requested SDK: 10.0.302" -Scenario $Scenario
    Assert-Contains -Actual $Result.Output -Expected "Roll-forward policy: latestPatch" -Scenario $Scenario
    Assert-Contains -Actual $Result.Output -Expected "powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1" -Scenario $Scenario
    Assert-True -Condition (@($Result.WorkingDirectories).Count -eq @($Result.Calls).Count) -Message "$Scenario did not capture one working directory for every dotnet command."
    foreach ($workingDirectory in $Result.WorkingDirectories) {
        Assert-True -Condition ([string]::Equals([IO.Path]::GetFullPath($workingDirectory), [IO.Path]::GetFullPath($Result.RepositoryRoot), [StringComparison]::OrdinalIgnoreCase)) -Message "$Scenario ran a dotnet command from '$workingDirectory' instead of repository root '$($Result.RepositoryRoot)'."
    }
    $primaryErrorCount = [regex]::Matches($Result.Output, [regex]::Escape("Unable to use the .NET SDK required by this repository.")).Count
    Assert-True -Condition ($primaryErrorCount -eq 1) -Message "$Scenario reported $primaryErrorCount primary SDK errors instead of one."
}

try {
    [void](New-Item -ItemType Directory -Path $tempRoot -Force)

    $commandNotFound = Invoke-VerifierScenario -Name "command-not-found" -CommandNotFound
    Assert-BootstrapFailureStoppedEarly -Result $commandNotFound -Scenario "command-not-found"
    Assert-Contains -Actual $commandNotFound.Output -Expected "Failure kind: dotnet command not found" -Scenario "command-not-found"
    Assert-Contains -Actual $commandNotFound.Output -Expected "dotnet (not found on PATH)" -Scenario "command-not-found"
    Assert-True -Condition (@($commandNotFound.Calls).Count -eq 0) -Message "command-not-found unexpectedly invoked dotnet."
    Assert-True -Condition (@($commandNotFound.WorkingDirectories).Count -eq 0) -Message "command-not-found unexpectedly launched a dotnet process."

    $observedHostError = @'
The command could not be loaded, possibly because:
  * You intended to execute a .NET SDK command:
      A compatible .NET SDK was not found.
Requested SDK version: 10.0.302
'@
    $observedFailure = Invoke-VerifierScenario -Name "observed-host-failure" -VersionStandardError $observedHostError -VersionExitCode -2147450725 -ListStandardOutput "7.0.120 [C:\Program Files\dotnet\sdk]`r`n10.0.301 [C:\Program Files\dotnet\sdk]"
    Assert-BootstrapFailureStoppedEarly -Result $observedFailure -Scenario "observed-host-failure"
    Assert-Contains -Actual $observedFailure.Output -Expected "Failure kind: dotnet host SDK selection failed" -Scenario "observed-host-failure"
    Assert-Contains -Actual $observedFailure.Output -Expected "dotnet --version exited with code -2147450725 before a version could be parsed." -Scenario "observed-host-failure"
    Assert-Contains -Actual $observedFailure.Output -Expected "A compatible .NET SDK was not found." -Scenario "observed-host-failure"
    Assert-Contains -Actual $observedFailure.Output -Expected "10.0.301 [C:\Program Files\dotnet\sdk]" -Scenario "observed-host-failure"
    Assert-True -Condition (($observedFailure.Calls -join "|") -eq "--version|--list-sdks") -Message "observed-host-failure did not perform only the two SDK probes."

    $multilineFailure = Invoke-VerifierScenario -Name "multiline-host-failure" -VersionStandardOutput "stdout line one`r`nstdout line two" -VersionStandardError "stderr line one`nstderr line two" -VersionExitCode 42
    Assert-BootstrapFailureStoppedEarly -Result $multilineFailure -Scenario "multiline-host-failure"
    Assert-Contains -Actual $multilineFailure.Output -Expected "stdout line two" -Scenario "multiline-host-failure"
    Assert-Contains -Actual $multilineFailure.Output -Expected "stderr line two" -Scenario "multiline-host-failure"

    $failedListProbe = Invoke-VerifierScenario -Name "failed-list-probe" -VersionStandardError "primary host selection cause" -VersionExitCode 43 -ListStandardError "list probe unavailable" -ListExitCode 9
    Assert-BootstrapFailureStoppedEarly -Result $failedListProbe -Scenario "failed-list-probe"
    Assert-Contains -Actual $failedListProbe.Output -Expected "Failure kind: dotnet host SDK selection failed" -Scenario "failed-list-probe"
    Assert-Contains -Actual $failedListProbe.Output -Expected "Installed SDK probe: unavailable because dotnet --list-sdks exited with code 9." -Scenario "failed-list-probe"
    Assert-Contains -Actual $failedListProbe.Output -Expected "primary host selection cause" -Scenario "failed-list-probe"

    $nonnumericSuccess = Invoke-VerifierScenario -Name "nonnumeric-success" -VersionStandardOutput "not-a-version"
    Assert-BootstrapFailureStoppedEarly -Result $nonnumericSuccess -Scenario "nonnumeric-success"
    Assert-Contains -Actual $nonnumericSuccess.Output -Expected "Failure kind: malformed dotnet --version output" -Scenario "nonnumeric-success"
    Assert-Contains -Actual $nonnumericSuccess.Output -Expected "did not return a numeric SDK version" -Scenario "nonnumeric-success"

    $multilineSuccess = Invoke-VerifierScenario -Name "multiline-success" -VersionStandardOutput "10.0.302`r`nunexpected second value" -VersionStandardError "unexpected warning line one`r`nunexpected warning line two"
    Assert-BootstrapFailureStoppedEarly -Result $multilineSuccess -Scenario "multiline-success"
    Assert-Contains -Actual $multilineSuccess.Output -Expected "did not return exactly one non-empty version line" -Scenario "multiline-success"
    Assert-Contains -Actual $multilineSuccess.Output -Expected "unexpected warning line two" -Scenario "multiline-success"

    $wrongFeatureBand = Invoke-VerifierScenario -Name "wrong-feature-band" -VersionStandardOutput "10.0.401"
    Assert-BootstrapFailureStoppedEarly -Result $wrongFeatureBand -Scenario "wrong-feature-band"
    Assert-Contains -Actual $wrongFeatureBand.Output -Expected "Failure kind: wrong SDK feature band" -Scenario "wrong-feature-band"
    Assert-Contains -Actual $wrongFeatureBand.Output -Expected "global.json requires 10.0.3xx" -Scenario "wrong-feature-band"

    $tooOldPatch = Invoke-VerifierScenario -Name "too-old-patch" -VersionStandardOutput "10.0.301"
    Assert-BootstrapFailureStoppedEarly -Result $tooOldPatch -Scenario "too-old-patch"
    Assert-Contains -Actual $tooOldPatch.Output -Expected "Failure kind: SDK patch is too old" -Scenario "too-old-patch"
    Assert-Contains -Actual $tooOldPatch.Output -Expected "older than the minimum patch 10.0.302" -Scenario "too-old-patch"

    foreach ($compatibleVersion in @("10.0.302", "10.0.309")) {
        $scenario = "compatible-$($compatibleVersion.Replace('.', '-'))"
        $compatible = Invoke-VerifierScenario -Name $scenario -VersionStandardOutput $compatibleVersion
        Assert-True -Condition ($compatible.ExitCode -ne 0) -Message "$scenario should stop at the fake build sentinel."
        Assert-True -Condition (-not $compatible.SentinelExists) -Message "$scenario did not proceed to normal TestResults cleanup."
        Assert-Contains -Actual $compatible.Output -Expected "Using .NET SDK $compatibleVersion selected by global.json." -Scenario $scenario
        Assert-True -Condition (($compatible.Calls -join [Environment]::NewLine).Contains("build")) -Message "$scenario did not reach the fake build sentinel. Verifier output: $($compatible.Output)"
        Assert-Contains -Actual ($compatible.Calls -join [Environment]::NewLine) -Expected '"-c" "Release"' -Scenario $scenario
        Assert-NotContains -Actual $compatible.Output -Unexpected "Unable to use the .NET SDK required by this repository." -Scenario $scenario
    }

    $explicitDebug = Invoke-VerifierScenario -Name "explicit-debug" -VersionStandardOutput "10.0.302" -VerifierArguments @("-Configuration", "Debug")
    Assert-True -Condition ($explicitDebug.ExitCode -ne 0) -Message "explicit-debug should stop at the fake build sentinel."
    Assert-True -Condition (-not $explicitDebug.SentinelExists) -Message "explicit-debug did not proceed to normal TestResults cleanup."
    Assert-Contains -Actual ($explicitDebug.Calls -join [Environment]::NewLine) -Expected '"-c" "Debug"' -Scenario "explicit-debug"

    Write-Output "Verifier SDK diagnostic process tests passed (8 failure scenarios, 3 compatible SDK scenarios)."
}
finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ((Test-Path -LiteralPath $resolvedTempRoot) -and $resolvedTempRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
