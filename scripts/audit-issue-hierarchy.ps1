[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Repository = $env:GITHUB_REPOSITORY,

    [Parameter(Mandatory = $false)]
    [int]$Campaign = 332,

    [Parameter(Mandatory = $false)]
    [int]$Phase = 523,

    [Parameter(Mandatory = $false)]
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repository) -or $Repository -notmatch '^[^/]+/[^/]+$') {
    throw 'Repository must use the OWNER/REPO form.'
}

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$issueCache = @{}
$childrenCache = @{}

function Invoke-GhJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    return $text | ConvertFrom-Json -Depth 100
}

function Get-Issue {
    param([Parameter(Mandatory = $true)][int]$Number)

    $key = [string]$Number
    if (-not $issueCache.ContainsKey($key)) {
        $issueCache[$key] = Invoke-GhJson -Arguments @('api', "repos/$Repository/issues/$Number")
    }

    return $issueCache[$key]
}

function Get-Children {
    param([Parameter(Mandatory = $true)][int]$Number)

    $key = [string]$Number
    if (-not $childrenCache.ContainsKey($key)) {
        $childrenCache[$key] = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/issues/$Number/sub_issues?per_page=100"))
    }

    return @($childrenCache[$key])
}

function Get-LabelNames {
    param([Parameter(Mandatory = $true)][object]$Issue)

    return @($Issue.labels | ForEach-Object { [string]$_.name })
}

function Assert-OneLabel {
    param(
        [Parameter(Mandatory = $true)][object]$Issue,
        [Parameter(Mandatory = $true)][string]$Prefix
    )

    $labels = @(Get-LabelNames -Issue $Issue | Where-Object { $_.StartsWith($Prefix, [System.StringComparison]::Ordinal) })
    if ($labels.Count -ne 1) {
        $errors.Add("#$($Issue.number) must have exactly one $Prefix label; found $($labels.Count): $($labels -join ', ').")
    }
}

$campaignIssue = Get-Issue -Number $Campaign
$phaseIssue = Get-Issue -Number $Phase
$campaignChildren = @(Get-Children -Number $Campaign)
if ($campaignChildren.number -notcontains $Phase) {
    $errors.Add("Phase #$Phase is not a native child of Campaign #$Campaign.")
}

$campaignLabels = @(Get-LabelNames -Issue $campaignIssue)
if ($campaignLabels -notcontains 'work:campaign') {
    $errors.Add("Campaign #$Campaign must have work:campaign.")
}
foreach ($prefix in @('work:', 'type:', 'domain:', 'status:')) {
    Assert-OneLabel -Issue $campaignIssue -Prefix $prefix
}

$phaseLabels = @(Get-LabelNames -Issue $phaseIssue)
if ($phaseLabels -notcontains 'work:phase') {
    $errors.Add("Phase #$Phase must have work:phase.")
}
$phaseBodyParent = [regex]::Match([string]$phaseIssue.body, 'Native parent:\s*#(?<number>\d+)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
if ($phaseBodyParent.Success -and [int]$phaseBodyParent.Groups['number'].Value -ne $Campaign) {
    $errors.Add("Phase #$Phase body names parent #$($phaseBodyParent.Groups['number'].Value), but native parent is Campaign #$Campaign.")
}

$nodes = [System.Collections.Generic.List[object]]::new()
$parentByNumber = @{}
$depthByNumber = @{}
$rootUowByNumber = @{}
$seen = @{}
$queue = [System.Collections.Generic.Queue[object]]::new()
$queue.Enqueue([pscustomobject]@{ Number = $Phase; Parent = $Campaign; Depth = 0; RootUow = $null })

while ($queue.Count -gt 0) {
    $entry = $queue.Dequeue()
    $key = [string]$entry.Number
    if ($seen.ContainsKey($key)) {
        $errors.Add("Issue #$($entry.Number) appears more than once in the Phase hierarchy.")
        continue
    }

    $seen[$key] = $true
    $issue = Get-Issue -Number $entry.Number
    $nodes.Add($issue)
    $parentByNumber[$key] = [int]$entry.Parent
    $depthByNumber[$key] = [int]$entry.Depth
    if ($null -ne $entry.RootUow) {
        $rootUowByNumber[$key] = [int]$entry.RootUow
    }

    $children = @(Get-Children -Number $entry.Number)
    foreach ($child in $children) {
        $rootUow = $entry.RootUow
        if ($entry.Depth -eq 0) {
            $rootUow = [int]$child.number
        }

        $queue.Enqueue([pscustomobject]@{ Number = [int]$child.number; Parent = [int]$entry.Number; Depth = $entry.Depth + 1; RootUow = $rootUow })
    }
}

foreach ($issue in $nodes) {
    $number = [int]$issue.number
    $key = [string]$number
    $labels = @(Get-LabelNames -Issue $issue)
    $isOpen = [string]$issue.state -eq 'open'

    if ($isOpen) {
        foreach ($prefix in @('work:', 'type:', 'domain:', 'status:')) {
            Assert-OneLabel -Issue $issue -Prefix $prefix
        }
    }
    else {
        $statusLabels = @($labels | Where-Object { $_.StartsWith('status:', [System.StringComparison]::Ordinal) })
        if ($statusLabels.Count -gt 0) {
            $errors.Add("Closed issue #$number retains active status labels: $($statusLabels -join ', ').")
        }
    }

    if ($number -ne $Phase) {
        $parent = [int]$parentByNumber[$key]
        $bodyParent = [regex]::Match([string]$issue.body, 'Native parent:\s*#(?<number>\d+)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($bodyParent.Success -and [int]$bodyParent.Groups['number'].Value -ne $parent) {
            $errors.Add("Issue #$number body names parent #$($bodyParent.Groups['number'].Value), but native parent is #$parent.")
        }
    }

    if ($depthByNumber[$key] -eq 1 -and $labels -notcontains 'work:uow') {
        $errors.Add("Direct Phase child #$number must have work:uow.")
    }

    if ($rootUowByNumber.ContainsKey($key)) {
        $rootUow = Get-Issue -Number ([int]$rootUowByNumber[$key])
        if ([string]$rootUow.state -eq 'closed' -and $isOpen) {
            $errors.Add("Open issue #$number remains below closed UOW #$($rootUow.number).")
        }
    }
}

$uows = @($nodes | Where-Object { $depthByNumber[[string]$_.number] -eq 1 })
foreach ($uow in $uows) {
    if ([string]$uow.state -ne 'open') {
        continue
    }

    $children = @(Get-Children -Number ([int]$uow.number))
    $activeChildren = @($children | Where-Object { [string]$_.state -eq 'open' })
    if ($activeChildren.Count -gt 12) {
        $errors.Add("UOW #$($uow.number) has $($activeChildren.Count) active Bolts; maximum is 12.")
    }

    if ($children.Count -lt 2) {
        $warnings.Add("UOW #$($uow.number) has $($children.Count) Bolt(s); decompose it before marking implementation ready.")
    }

    $uowLabels = @(Get-LabelNames -Issue $uow)
    if ($children.Count -eq 0 -and ($uowLabels -contains 'status:ready' -or $uowLabels -contains 'status:in-progress')) {
        $errors.Add("UOW #$($uow.number) cannot be ready or in progress without a Bolt plan.")
    }

    foreach ($bolt in $children) {
        $boltLabels = @(Get-LabelNames -Issue $bolt)
        if ($boltLabels -notcontains 'work:bolt') {
            $errors.Add("Child #$($bolt.number) of active UOW #$($uow.number) must have work:bolt.")
        }

        $grandchildren = @(Get-Children -Number ([int]$bolt.number))
        if ($grandchildren.Count -gt 0) {
            $errors.Add("Bolt #$($bolt.number) has $($grandchildren.Count) sub-issue(s); Bolts must be leaves.")
        }
    }
}

$openNodes = @($nodes | Where-Object { [string]$_.state -eq 'open' })
$openNumbers = @{}
foreach ($issue in $openNodes) {
    $openNumbers[[string]$issue.number] = $true
}

$adjacency = @{}
$indegree = @{}
foreach ($issue in $openNodes) {
    $key = [string]$issue.number
    $adjacency[$key] = [System.Collections.Generic.List[int]]::new()
    $indegree[$key] = 0
}

foreach ($issue in $openNodes) {
    $blockedNumber = [int]$issue.number
    $dependencies = @(Invoke-GhJson -Arguments @('api', "repos/$Repository/issues/$blockedNumber/dependencies/blocked_by?per_page=100"))
    foreach ($dependency in $dependencies) {
        $blockerKey = [string]$dependency.number
        $blockedKey = [string]$blockedNumber
        if (-not $openNumbers.ContainsKey($blockerKey)) {
            $warnings.Add("Open issue #$blockedNumber is blocked by #$($dependency.number), which is outside the open Phase tree.")
            continue
        }

        $adjacency[$blockerKey].Add($blockedNumber)
        $indegree[$blockedKey] = [int]$indegree[$blockedKey] + 1
    }
}

$ready = [System.Collections.Generic.Queue[int]]::new()
foreach ($issue in $openNodes) {
    if ([int]$indegree[[string]$issue.number] -eq 0) {
        $ready.Enqueue([int]$issue.number)
    }
}

$visitedDependencyNodes = 0
while ($ready.Count -gt 0) {
    $number = $ready.Dequeue()
    $visitedDependencyNodes++
    foreach ($blocked in $adjacency[[string]$number]) {
        $blockedKey = [string]$blocked
        $indegree[$blockedKey] = [int]$indegree[$blockedKey] - 1
        if ([int]$indegree[$blockedKey] -eq 0) {
            $ready.Enqueue($blocked)
        }
    }
}

if ($visitedDependencyNodes -ne $openNodes.Count) {
    $cycleMembers = @($openNodes | Where-Object { [int]$indegree[[string]$_.number] -gt 0 } | ForEach-Object { "#$($_.number)" })
    $errors.Add("Open Phase dependency graph contains a cycle involving: $($cycleMembers -join ', ').")
}

$repositoryParts = $Repository -split '/', 2
$pullRequestQuery = 'query($owner:String!,$name:String!){repository(owner:$owner,name:$name){pullRequests(first:100,states:OPEN){nodes{number url closingIssuesReferences(first:20){nodes{number}}}}}}'
$pullRequestData = Invoke-GhJson -Arguments @('api', 'graphql', '-f', "query=$pullRequestQuery", '-f', "owner=$($repositoryParts[0])", '-f', "name=$($repositoryParts[1])")
foreach ($pullRequest in @($pullRequestData.data.repository.pullRequests.nodes)) {
    foreach ($closingIssue in @($pullRequest.closingIssuesReferences.nodes)) {
        $closingLabels = @(Get-LabelNames -Issue (Get-Issue -Number ([int]$closingIssue.number)))
        if ($closingLabels -contains 'work:campaign' -or $closingLabels -contains 'work:phase' -or $closingLabels -contains 'work:uow') {
            $errors.Add("PR #$($pullRequest.number) uses a closing relationship for aggregate issue #$($closingIssue.number). Only Bolts may be closed by PRs.")
        }
    }
}

$result = [ordered]@{
    repository = $Repository
    campaign = $Campaign
    phase = $Phase
    auditedIssues = $nodes.Count
    openIssues = $openNodes.Count
    unitsOfWork = $uows.Count
    errors = @($errors)
    warnings = @($warnings)
}

if ($Json) {
    $result | ConvertTo-Json -Depth 10
}
else {
    Write-Host "Audited $($nodes.Count) issues under Phase #$Phase in $Repository."
    foreach ($warning in $warnings) {
        Write-Warning $warning
    }

    foreach ($errorMessage in $errors) {
        Write-Error $errorMessage -ErrorAction Continue
    }

    if ($errors.Count -eq 0) {
        Write-Host "Issue governance audit passed with $($warnings.Count) warning(s)."
    }
}

if ($errors.Count -gt 0) {
    exit 1
}
