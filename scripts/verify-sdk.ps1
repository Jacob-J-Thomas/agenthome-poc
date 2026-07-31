param(
    [Parameter(Mandatory = $true)]
    [string]$GlobalJsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-NativeArgumentText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Argument
    )

    if ($Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $escaped = [Text.StringBuilder]::new()
    [void]$escaped.Append('"')
    $backslashCount = 0

    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$escaped.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$escaped.Append('"')
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void]$escaped.Append(('\' * $backslashCount))
            $backslashCount = 0
        }

        [void]$escaped.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void]$escaped.Append(('\' * ($backslashCount * 2)))
    }

    [void]$escaped.Append('"')
    return $escaped.ToString()
}

function ConvertTo-SafeDiagnosticText {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return "<none>"
    }

    $ansiEscapePattern = [string][char]27 + '\[[0-?]*[ -/]*[@-~]'
    $normalized = [regex]::Replace($Text, $ansiEscapePattern, "")
    $normalized = [regex]::Replace($normalized, '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]', "")
    $normalized = $normalized.Replace("`r`n", "`n").Replace("`r", "`n").Trim()

    $maximumDiagnosticLength = 16384
    if ($normalized.Length -gt $maximumDiagnosticLength) {
        $normalized = $normalized.Substring(0, $maximumDiagnosticLength) + "`n[diagnostic truncated]"
    }

    return $normalized.Replace("`n", [Environment]::NewLine)
}

function ConvertTo-IndentedDiagnosticLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    return @(($Text -split '\r\n|\n|\r') | ForEach-Object { "  $_" })
}

function Invoke-CapturedNativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $argumentText = (($Arguments | ForEach-Object { ConvertTo-NativeArgumentText -Argument $_ }) -join " ")
    $extension = [IO.Path]::GetExtension($FilePath)

    if ($extension -in @(".cmd", ".bat")) {
        if ([string]::IsNullOrWhiteSpace($env:ComSpec)) {
            return [pscustomobject]@{
                Started = $false
                ExitCode = $null
                StandardOutput = ""
                StandardError = ""
                StartError = "ComSpec is unavailable, so the command script could not be launched."
            }
        }

        $startInfo.FileName = $env:ComSpec
        $startInfo.Arguments = '/d /s /c ""{0}" {1}"' -f $FilePath, $argumentText
    }
    else {
        $startInfo.FileName = $FilePath
        $startInfo.Arguments = $argumentText
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        [void]$process.Start()
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()

        return [pscustomobject]@{
            Started = $true
            ExitCode = $process.ExitCode
            StandardOutput = $standardOutputTask.GetAwaiter().GetResult()
            StandardError = $standardErrorTask.GetAwaiter().GetResult()
            StartError = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Started = $false
            ExitCode = $null
            StandardOutput = ""
            StandardError = ""
            StartError = $_.Exception.Message
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-InstalledSdkDiagnosticLines {
    param(
        [AllowNull()]
        [object]$Probe
    )

    if ($null -eq $Probe) {
        return @("Installed SDK probe: unavailable because dotnet could not be found or started.")
    }

    if (-not $Probe.Started) {
        $startError = ConvertTo-SafeDiagnosticText -Text $Probe.StartError
        return @("Installed SDK probe: unavailable because dotnet --list-sdks could not start.", "Probe launch diagnostic:") + (ConvertTo-IndentedDiagnosticLines -Text $startError)
    }

    if ($Probe.ExitCode -ne 0) {
        $lines = @("Installed SDK probe: unavailable because dotnet --list-sdks exited with code $($Probe.ExitCode).")
        $probeOutput = ConvertTo-SafeDiagnosticText -Text $Probe.StandardOutput
        $probeError = ConvertTo-SafeDiagnosticText -Text $Probe.StandardError

        if ($probeOutput -ne "<none>") {
            $lines += "dotnet --list-sdks stdout:"
            $lines += ConvertTo-IndentedDiagnosticLines -Text $probeOutput
        }

        if ($probeError -ne "<none>") {
            $lines += "dotnet --list-sdks stderr:"
            $lines += ConvertTo-IndentedDiagnosticLines -Text $probeError
        }

        return $lines
    }

    $installedSdks = ConvertTo-SafeDiagnosticText -Text $Probe.StandardOutput
    if ($installedSdks -eq "<none>") {
        return @("Installed SDKs:", "  <none reported>")
    }

    return @("Installed SDKs:") + (ConvertTo-IndentedDiagnosticLines -Text $installedSdks)
}

function New-SdkFailureMessage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FailureKind,

        [Parameter(Mandatory = $true)]
        [string]$FailureDetail,

        [Parameter(Mandatory = $true)]
        [string]$RequestedVersion,

        [Parameter(Mandatory = $true)]
        [string]$RollForward,

        [Parameter(Mandatory = $true)]
        [string]$DotnetPath,

        [Parameter(Mandatory = $true)]
        [string]$CompatibleFeatureBand,

        [AllowNull()]
        [object]$VersionProbe,

        [AllowNull()]
        [object]$InstalledSdkProbe
    )

    $lines = @(
        "Unable to use the .NET SDK required by this repository."
        "Failure kind: $FailureKind"
        "Requested SDK: $RequestedVersion"
        "Roll-forward policy: $RollForward"
        "Attempted dotnet executable: $DotnetPath"
        $FailureDetail
    )

    if ($null -ne $VersionProbe) {
        if (-not $VersionProbe.Started) {
            $launchDiagnostic = ConvertTo-SafeDiagnosticText -Text $VersionProbe.StartError
            $lines += "dotnet --version launch diagnostic:"
            $lines += ConvertTo-IndentedDiagnosticLines -Text $launchDiagnostic
        }
        else {
            $versionOutput = ConvertTo-SafeDiagnosticText -Text $VersionProbe.StandardOutput
            $versionError = ConvertTo-SafeDiagnosticText -Text $VersionProbe.StandardError
            $lines += "dotnet --version stdout:"
            $lines += ConvertTo-IndentedDiagnosticLines -Text $versionOutput
            $lines += "dotnet --version stderr:"
            $lines += ConvertTo-IndentedDiagnosticLines -Text $versionError
        }
    }

    $lines += Get-InstalledSdkDiagnosticLines -Probe $InstalledSdkProbe
    $lines += "Next action: Install .NET SDK $RequestedVersion or a newer $CompatibleFeatureBand patch allowed by global.json rollForward '$RollForward', then rerun: powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1"
    return $lines -join [Environment]::NewLine
}

function Stop-SdkValidation {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [Console]::Error.WriteLine($Message)
    exit 1
}

$globalJson = Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json
$requestedVersionText = [string]$globalJson.sdk.version
$rollForward = [string]$globalJson.sdk.rollForward
[Version]$requestedVersion = $null

if (-not [Version]::TryParse($requestedVersionText, [ref]$requestedVersion)) {
    throw "global.json contains invalid sdk.version '$requestedVersionText'. Correct global.json before rerunning the verifier."
}

$requestedFeatureBand = [Math]::Floor($requestedVersion.Build / 100)
$compatibleFeatureBand = "{0}.{1}.{2}xx" -f $requestedVersion.Major, $requestedVersion.Minor, $requestedFeatureBand
$dotnetCommands = @(Get-Command -Name "dotnet" -CommandType Application -ErrorAction SilentlyContinue)

if ($dotnetCommands.Count -eq 0) {
    $message = New-SdkFailureMessage -FailureKind "dotnet command not found" -FailureDetail "The dotnet command is not available on PATH." -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath "dotnet (not found on PATH)" -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $null -InstalledSdkProbe $null
    Stop-SdkValidation -Message $message
}

$dotnetCommand = $dotnetCommands[0]
$dotnetPath = if (-not [string]::IsNullOrWhiteSpace($dotnetCommand.Path)) { $dotnetCommand.Path } else { $dotnetCommand.Source }
$dotnetPath = ConvertTo-SafeDiagnosticText -Text $dotnetPath
$versionProbe = Invoke-CapturedNativeCommand -FilePath $dotnetPath -Arguments @("--version")

if (-not $versionProbe.Started) {
    $message = New-SdkFailureMessage -FailureKind "dotnet command could not start" -FailureDetail "The resolved dotnet command could not be launched." -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath $dotnetPath -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $versionProbe -InstalledSdkProbe $null
    Stop-SdkValidation -Message $message
}

if ($versionProbe.ExitCode -ne 0) {
    $installedSdkProbe = Invoke-CapturedNativeCommand -FilePath $dotnetPath -Arguments @("--list-sdks")
    $message = New-SdkFailureMessage -FailureKind "dotnet host SDK selection failed" -FailureDetail "dotnet --version exited with code $($versionProbe.ExitCode) before a version could be parsed." -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath $dotnetPath -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $versionProbe -InstalledSdkProbe $installedSdkProbe
    Stop-SdkValidation -Message $message
}

$versionLines = @(($versionProbe.StandardOutput -split '\r\n|\n|\r') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
[Version]$resolvedVersion = $null
$hasSingleVersionLine = $versionLines.Count -eq 1
$hasParsedVersion = $hasSingleVersionLine -and [Version]::TryParse($versionLines[0].Trim(), [ref]$resolvedVersion)

if (-not $hasParsedVersion) {
    $installedSdkProbe = Invoke-CapturedNativeCommand -FilePath $dotnetPath -Arguments @("--list-sdks")
    $detail = if ($hasSingleVersionLine) { "dotnet --version exited successfully but did not return a numeric SDK version." } else { "dotnet --version exited successfully but did not return exactly one non-empty version line." }
    $message = New-SdkFailureMessage -FailureKind "malformed dotnet --version output" -FailureDetail $detail -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath $dotnetPath -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $versionProbe -InstalledSdkProbe $installedSdkProbe
    Stop-SdkValidation -Message $message
}

$resolvedFeatureBand = [Math]::Floor($resolvedVersion.Build / 100)
$sameFeatureBand = $resolvedVersion.Major -eq $requestedVersion.Major -and $resolvedVersion.Minor -eq $requestedVersion.Minor -and $resolvedFeatureBand -eq $requestedFeatureBand

if (-not $sameFeatureBand) {
    $installedSdkProbe = Invoke-CapturedNativeCommand -FilePath $dotnetPath -Arguments @("--list-sdks")
    $detail = "The resolved SDK $resolvedVersion is in feature band $($resolvedVersion.Major).$($resolvedVersion.Minor).${resolvedFeatureBand}xx; global.json requires $compatibleFeatureBand."
    $message = New-SdkFailureMessage -FailureKind "wrong SDK feature band" -FailureDetail $detail -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath $dotnetPath -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $versionProbe -InstalledSdkProbe $installedSdkProbe
    Stop-SdkValidation -Message $message
}

if ($resolvedVersion -lt $requestedVersion) {
    $installedSdkProbe = Invoke-CapturedNativeCommand -FilePath $dotnetPath -Arguments @("--list-sdks")
    $detail = "The resolved SDK $resolvedVersion is older than the minimum patch $requestedVersion allowed in feature band $compatibleFeatureBand."
    $message = New-SdkFailureMessage -FailureKind "SDK patch is too old" -FailureDetail $detail -RequestedVersion $requestedVersionText -RollForward $rollForward -DotnetPath $dotnetPath -CompatibleFeatureBand $compatibleFeatureBand -VersionProbe $versionProbe -InstalledSdkProbe $installedSdkProbe
    Stop-SdkValidation -Message $message
}

Write-Output "Using .NET SDK $resolvedVersion selected by global.json."
exit 0
