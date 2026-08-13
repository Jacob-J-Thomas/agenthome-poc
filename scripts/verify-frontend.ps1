param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LogsPath,

    [ValidateRange(1, 300)]
    [int]$InstallTimeoutSeconds = 300,

    [ValidateRange(1, 300)]
    [int]$TestTimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRootPath = [IO.Path]::GetFullPath($RepositoryRoot)
$logsPathRoot = [IO.Path]::GetFullPath($LogsPath)
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)

. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")

function Invoke-NpmVerificationPhase {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string[]]$NpmArguments,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $fileName = if ($runningOnWindows) { $env:ComSpec } else { "npm" }
    $arguments = if ($runningOnWindows) {
        @("/d", "/s", "/c", "npm.cmd $($NpmArguments -join ' ')")
    }
    else {
        @($NpmArguments)
    }

    try {
        Add-VerificationParallelPhase -Name $Name -FileName $fileName -Arguments $arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repositoryRootPath -OutputPath (Join-Path $logsPathRoot "$Name.log")
        Invoke-VerificationParallelPhases -MaximumWorkers 1 -MaximumResourceCapacity 1 | Out-Null
    }
    finally {
        Reset-VerificationParallelPhaseState
    }
}

New-Item -ItemType Directory -Path $logsPathRoot -Force | Out-Null
foreach ($staleLogName in @("npm-ci.log", "frontend-tests.log")) {
    $staleLogPath = Join-Path $logsPathRoot $staleLogName
    if (Test-Path -LiteralPath $staleLogPath) {
        Remove-Item -LiteralPath $staleLogPath -Force
    }
}
Invoke-NpmVerificationPhase -Name "npm-ci" -NpmArguments @("ci", "--include=dev") -TimeoutSeconds $InstallTimeoutSeconds
Invoke-NpmVerificationPhase -Name "frontend-tests" -NpmArguments @("test") -TimeoutSeconds $TestTimeoutSeconds
Write-Output "VERIFY_FRONTEND_COMPLETE schema_version=1 status=passed install_log=$(Join-Path $logsPathRoot 'npm-ci.log') test_log=$(Join-Path $logsPathRoot 'frontend-tests.log')"
