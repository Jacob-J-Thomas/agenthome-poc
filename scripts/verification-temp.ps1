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
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX) -and ($physicalPath -eq "/var" -or $physicalPath.StartsWith("/var/", [StringComparison]::Ordinal))) {
        $physicalPath = "/private" + $physicalPath
    }

    return $physicalPath
}
