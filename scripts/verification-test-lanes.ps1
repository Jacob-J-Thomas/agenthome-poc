Set-StrictMode -Version Latest

function New-VerificationTestLane {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string[]]$IncludeFullyQualifiedName = @(),

        [string[]]$ExcludeFullyQualifiedName = @()
    )

    foreach ($value in @($IncludeFullyQualifiedName) + @($ExcludeFullyQualifiedName)) {
        if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny(@('(', ')', '&', '|', '~', '=', '!')) -ge 0) {
            throw "Verification lane '$Name' contains an unsafe fully-qualified-name predicate."
        }
    }

    return [pscustomobject]@{
        Name = $Name
        IncludeFullyQualifiedName = @($IncludeFullyQualifiedName)
        ExcludeFullyQualifiedName = @($ExcludeFullyQualifiedName)
    }
}

function Get-VerificationTestProjectLanes {
    param([System.IO.FileInfo]$TestProject)

    # One process per assembly avoids repeated VSTest startup, deployment, coverage instrumentation,
    # and Cobertura serialization. Assembly-level xUnit bounds and explicit collections provide the
    # safe inner parallelism; the stable-ID partition contract still proves every case exactly once.
    return @((New-VerificationTestLane -Name "all"))
}

function Get-VerificationTestLaneFilter {
    param(
        [object]$Lane,
        [string[]]$AdditionalExclusions = @()
    )

    $parts = [Collections.Generic.List[string]]::new()
    if (@($Lane.IncludeFullyQualifiedName).Count -gt 0) {
        $include = @($Lane.IncludeFullyQualifiedName | ForEach-Object { "(FullyQualifiedName~$_)" }) -join '|'
        $parts.Add("($include)")
    }

    $exclusions = [Collections.Generic.List[string]]::new()
    foreach ($exclusion in @($Lane.ExcludeFullyQualifiedName)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in @($AdditionalExclusions)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in $exclusions) {
        $parts.Add("(FullyQualifiedName!~$exclusion)")
    }

    $parts.Add("(VerificationTier!=Stress)")
    return $parts -join '&'
}
