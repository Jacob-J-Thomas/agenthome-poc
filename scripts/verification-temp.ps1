function Resolve-VerificationPhysicalTempRoot {
    param(
        [AllowEmptyString()]
        [string]$RunnerTemp,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$SystemTempPath
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($RunnerTemp)) { $SystemTempPath } else { $RunnerTemp }
    if (-not [IO.Path]::IsPathFullyQualified($candidate)) {
        throw "Verification temporary storage must be a fully qualified path."
    }

    $physicalPath = [IO.Path]::GetFullPath($candidate)
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX) -and ($physicalPath -eq "/var" -or $physicalPath.StartsWith("/var/", [StringComparison]::Ordinal) -or $physicalPath -eq "/tmp" -or $physicalPath.StartsWith("/tmp/", [StringComparison]::Ordinal))) {
        $physicalPath = "/private" + $physicalPath
    }

    return $physicalPath
}

function Get-VerificationLaneFixturePath {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$PhysicalTempRoot,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$RunIdentity,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$LaneIdentity
    )

    if (-not [IO.Path]::IsPathFullyQualified($PhysicalTempRoot)) {
        throw "Verification lane temporary storage must use a fully qualified root."
    }

    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $identityBytes = [Text.Encoding]::UTF8.GetBytes("$RunIdentity`n$LaneIdentity")
        $digest = $hash.ComputeHash($identityBytes)
    }
    finally {
        $hash.Dispose()
    }

    # Six digest bytes keep each lane collision-resistant while leaving enough room
    # for CoreFxPipe_<name> below macOS's 104-byte Unix-domain-socket limit.
    $token = ([BitConverter]::ToString($digest, 0, 6)).Replace("-", "").ToLowerInvariant()
    $fixturePath = Join-Path ([IO.Path]::GetFullPath($PhysicalTempRoot)) "e-$token"
    $runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    $maximumUnixFixturePathBytes = 72
    if (-not $runningOnWindows -and [Text.Encoding]::UTF8.GetByteCount($fixturePath) -gt $maximumUnixFixturePathBytes) {
        throw "Verification lane temporary path '$fixturePath' is too long for bounded Unix named-pipe endpoints. Use a shorter fully qualified temporary root."
    }

    return $fixturePath
}
