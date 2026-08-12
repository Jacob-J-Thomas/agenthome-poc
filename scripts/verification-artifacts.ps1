Set-StrictMode -Version Latest

function Get-VerificationIsolatedOutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$IsolationRoot,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Configuration,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$TargetFramework
    )

    foreach ($segment in @($Configuration, $TargetFramework)) {
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $segment.Contains([IO.Path]::DirectorySeparatorChar) -or
            $segment.Contains([IO.Path]::AltDirectorySeparatorChar)) {
            throw "Verification output topology segment '$segment' is not a single safe path segment."
        }
    }

    return [IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $IsolationRoot "bin") $Configuration) $TargetFramework))
}

function Get-VerificationDirectoryManifest {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Directory
    )

    $fullDirectory = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $fullDirectory -PathType Container)) {
        throw "Verification artifact directory is missing: $fullDirectory"
    }

    $files = @(Get-ChildItem -LiteralPath $fullDirectory -Recurse -File -Force | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Verification artifact directory is empty: $fullDirectory"
    }

    return @($files | ForEach-Object {
        [pscustomobject]@{
            RelativePath = [IO.Path]::GetRelativePath($fullDirectory, $_.FullName).Replace('\', '/')
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Assert-VerificationDirectoryManifest {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Expected,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Description
    )

    $actual = @(Get-VerificationDirectoryManifest -Directory $Directory)
    $expectedLines = @($Expected | Sort-Object -Property RelativePath | ForEach-Object { "$($_.RelativePath)|$($_.Length)|$($_.Sha256)" })
    $actualLines = @($actual | Sort-Object -Property RelativePath | ForEach-Object { "$($_.RelativePath)|$($_.Length)|$($_.Sha256)" })
    $difference = @(Compare-Object -ReferenceObject $expectedLines -DifferenceObject $actualLines -CaseSensitive)
    if ($difference.Count -ne 0) {
        $details = @($difference | Select-Object -First 20 | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join '; '
        throw "$Description failed immutable artifact verification for '$([IO.Path]::GetFullPath($Directory))'. $details"
    }
}

function Copy-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Description
    )

    $source = [IO.Path]::GetFullPath($SourceDirectory)
    $destination = [IO.Path]::GetFullPath($DestinationDirectory)
    if (Test-Path -LiteralPath $destination) {
        throw "$Description destination already exists and could substitute stale artifacts: $destination"
    }

    $sourceManifest = @(Get-VerificationDirectoryManifest -Directory $source)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($entry in $sourceManifest) {
        $sourcePath = Join-Path $source $entry.RelativePath
        $destinationPath = Join-Path $destination $entry.RelativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }
    Assert-VerificationDirectoryManifest -Expected $sourceManifest -Directory $destination -Description $Description
    return $sourceManifest
}
