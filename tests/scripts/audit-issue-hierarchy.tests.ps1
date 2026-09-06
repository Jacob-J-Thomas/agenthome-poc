[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:assertions = 0
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$auditScript = Join-Path $repositoryRoot 'scripts/audit-issue-hierarchy.ps1'
$workflow = Join-Path $repositoryRoot '.github/workflows/issue-governance.yml'
$pwsh = (Get-Process -Id $PID).Path
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "issue-audit-$([guid]::NewGuid().ToString('N'))"

function Assert-True {
    param([Parameter(Mandatory = $true)][bool]$Condition, [Parameter(Mandatory = $true)][string]$Message)
    $script:assertions++
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Equal {
    param([Parameter(Mandatory = $false)]$Expected, [Parameter(Mandatory = $false)]$Actual, [Parameter(Mandatory = $true)][string]$Message)
    $script:assertions++
    if (($Expected | ConvertTo-Json -Depth 20 -Compress) -ne ($Actual | ConvertTo-Json -Depth 20 -Compress)) {
        throw "Assertion failed: $Message. Expected '$Expected', actual '$Actual'."
    }
}

function New-Labels {
    param([string]$Work, [string]$Status = '', [string]$Type = 'type:feature', [string]$Domain = 'domain:governance')
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($Work, $Type, $Domain, $Status)) { if (-not [string]::IsNullOrWhiteSpace($name)) { $names.Add($name) } }
    return [pscustomobject]@{ totalCount = $names.Count; nodes = @($names | ForEach-Object { [pscustomobject]@{ name = $_ } }); pageInfo = [pscustomobject]@{ hasNextPage = $false; endCursor = $null } }
}

function New-Issue {
    param(
        [int]$Number,
        [string]$Work,
        [string]$State = 'OPEN',
        [string]$Status = 'status:tracking',
        [Nullable[int]]$Parent,
        [string]$Body = '',
        [bool]$Locked = $true
    )
    $parentValue = if ($null -eq $Parent) { $null } else { [pscustomobject]@{ id = "I$Parent"; number = [int]$Parent; repository = [pscustomobject]@{ nameWithOwner = 'owner/repo' } } }
    return [pscustomobject]@{
        id = "I$Number"
        number = $Number
        title = "Issue $Number"
        body = $Body
        state = $State
        stateReason = $null
        locked = $Locked
        labels = New-Labels -Work $Work -Status $(if ($State -eq 'OPEN') { $Status } else { '' })
        parent = $parentValue
        blockedBy = [pscustomobject]@{ totalCount = 0; nodes = @() }
    }
}

function New-PullRequest {
    param([int]$Number, [int[]]$Targets = @())
    return [pscustomobject]@{
        id = "P$Number"
        number = $Number
        url = "https://example.test/pull/$Number"
        closingIssuesReferences = [pscustomobject]@{
            totalCount = $Targets.Count
            nodes = @($Targets | ForEach-Object { [pscustomobject]@{ id = "I$_"; number = $_; repository = [pscustomobject]@{ nameWithOwner = 'owner/repo' } } })
            pageInfo = [pscustomobject]@{ hasNextPage = $false; endCursor = $null }
        }
    }
}

function New-Fixture {
    $issues = [System.Collections.Generic.List[object]]::new()
    $issues.Add((New-Issue 1 'work:campaign' -Body "Work level: Campaign. Native parent: None (root Campaign).`nCurrent ancestry: None (root Campaign)."))
    $issues.Add((New-Issue 2 'work:phase' -Parent 1 -Body "Work level: Phase. Native parent: #1.`nCurrent ancestry: #1.`n`n### Parent Campaign`n#1"))
    $issues.Add((New-Issue 3 'work:uow' -Parent 2 -Body "Work level: Unit of Work. Native parent: #2.`nCurrent ancestry: #2 → #1."))
    $issues.Add((New-Issue 4 'work:bolt' -Parent 3 -Status 'status:ready' -Body "Work level: Bolt. Native parent: #3.`nCurrent ancestry: #3 → #2 → #1."))
    $issues.Add((New-Issue 5 'work:bolt' -Parent 3 -Status 'status:queued' -Body 'Native parent: #3'))
    $issues.Add((New-Issue 6 'work:uow' -Parent 2 -Body "Native parent: #2`n## Admission and placement`nIntentionally dormant with zero Bolts"))
    $issues.Add((New-Issue 7 'work:uow' -Parent 2 -Body "Native parent: #2`n## Admission and placement`nExplicit decomposition-needed posture"))
    $issues.Add((New-Issue 8 'work:bolt' -Parent 7 -Status 'status:queued' -Body 'Native parent: #7'))
    $issues.Add((New-Issue 100 'work:campaign' -Body 'Native parent: None'))
    $issues.Add((New-Issue 101 'work:phase' -Parent 100 -Body 'Native parent: #100'))
    $issues.Add((New-Issue 102 'work:uow' -Parent 101 -Body 'Native parent: #101'))
    $issues.Add((New-Issue 103 'work:bolt' -Parent 102 -Status 'status:queued' -Body 'Native parent: #102'))
    $issues.Add((New-Issue 104 'work:bolt' -Parent 102 -Status 'status:queued' -Body 'Native parent: #102'))
    $issues.Add((New-Issue 200 'work:finding' -Status 'status:needs-spec' -Body 'Native parent: None'))
    foreach ($number in 201..285) { $issues.Add((New-Issue $number 'work:finding' -State 'CLOSED')) }
    $caseUpperUow = New-Issue 338 'work:uow' -Parent 2 -Body "Work level: Unit of Work. Native parent: #2.`nCurrent ancestry: #2 → #1."
    $caseUpperUow.id = 'I_kwDOSxkvdc8AAAABMEqXQQ'
    $issues.Add($caseUpperUow)
    $caseLowerBolt = New-Issue 339 'work:bolt' -Parent 338 -Status 'status:queued' -Body "Work level: Bolt. Native parent: #338.`nCurrent ancestry: #338 → #2 → #1."
    $caseLowerBolt.id = 'I_kwDOSxkvdc8AAAABMEqXqQ'
    $caseLowerBolt.parent.id = 'I_kwDOSxkvdc8AAAABMEqXQQ'
    $issues.Add($caseLowerBolt)
    $caseUpperSibling = New-Issue 340 'work:bolt' -Parent 338 -Status 'status:queued' -Body "Work level: Bolt. Native parent: #338.`nCurrent ancestry: #338 → #2 → #1."
    $caseUpperSibling.parent.id = 'I_kwDOSxkvdc8AAAABMEqXQQ'
    $issues.Add($caseUpperSibling)
    foreach ($number in 286..292) { $issues.Add((New-Issue $number 'work:finding' -State 'CLOSED')) }

    $pullRequests = [System.Collections.Generic.List[object]]::new()
    $pullRequests.Add((New-PullRequest 1 @(4, 5, 339)))
    $pullRequests[0].closingIssuesReferences.nodes[2].id = 'I_kwDOSxkvdc8AAAABMEqXqQ'
    foreach ($number in 2..101) { $pullRequests.Add((New-PullRequest $number)) }
    return [pscustomobject]@{ repository = 'owner/repo'; issues = @($issues); pullRequests = @($pullRequests); mode = [pscustomobject]@{} }
}

function Copy-Fixture {
    param([Parameter(Mandatory = $true)][object]$Fixture)
    return ($Fixture | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100)
}

function Get-Issue {
    param([object]$Fixture, [int]$Number)
    return @($Fixture.issues | Where-Object { [int]$_.number -eq $Number })[0]
}

function Save-Fixture {
    param([Parameter(Mandatory = $true)][object]$Fixture)
    $Fixture | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'fixture.json') -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'calls.log') -Value '' -Encoding utf8NoBOM
}

function Invoke-Audit {
    param([Parameter(Mandatory = $true)][object]$Fixture, [string[]]$ExtraArguments = @())
    Save-Fixture -Fixture $Fixture
    $oldPath = $env:PATH
    $oldFixture = $env:AUDIT_FIXTURE
    $oldLog = $env:AUDIT_CALL_LOG
    try {
        $env:PATH = "$fixtureRoot$([IO.Path]::PathSeparator)$oldPath"
        $env:AUDIT_FIXTURE = Join-Path $fixtureRoot 'fixture.json'
        $env:AUDIT_CALL_LOG = Join-Path $fixtureRoot 'calls.log'
        $output = @(& $pwsh -NoProfile -File $auditScript -Repository owner/repo -Json @ExtraArguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:PATH = $oldPath
        $env:AUDIT_FIXTURE = $oldFixture
        $env:AUDIT_CALL_LOG = $oldLog
    }
    $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
    try { $result = $text | ConvertFrom-Json -Depth 100 } catch { throw "Audit did not return JSON (exit $exitCode): $text" }
    $calls = @((Get-Content -LiteralPath (Join-Path $fixtureRoot 'calls.log')) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    return [pscustomobject]@{ ExitCode = $exitCode; Result = $result; Calls = $calls }
}

function Assert-HasCode {
    param([object]$Run, [string]$Code, [string]$Message)
    Assert-True -Condition (@($Run.Result.errors | ForEach-Object { [string]$_.code }) -contains $Code) -Message $Message
}

function Set-Parent {
    param([object]$Issue, [Nullable[int]]$Parent, [string]$Repository = 'owner/repo')
    $Issue.parent = if ($null -eq $Parent) { $null } else { [pscustomobject]@{ id = "I$Parent"; number = [int]$Parent; repository = [pscustomobject]@{ nameWithOwner = $Repository } } }
}

New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$broker = @'
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (($args -join ' ') -ne 'api graphql --input -') { throw "Only api graphql --input - is supported: $($args -join ' ')" }
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json -Depth 100
if ([string]$payload.query -match '(?i)\bmutation\b') { throw 'Mutations are forbidden.' }
$fixture = Get-Content -Raw -LiteralPath $env:AUDIT_FIXTURE | ConvertFrom-Json -Depth 100
$cursor = if ($null -eq $payload.variables.after) { '' } else { [string]$payload.variables.after }
$isIssue = [string]$payload.query -match 'query\s+IssueAuditInventory'
$isPullRequest = [string]$payload.query -match 'query\s+IssueAuditOpenPullRequests'
if (-not $isIssue -and -not $isPullRequest) { throw 'Unknown query.' }
$operation = if ($isIssue) { 'issues' } else { 'pullRequests' }
Add-Content -LiteralPath $env:AUDIT_CALL_LOG -Value "$operation|$cursor"
$graphQlError = if ($null -ne $fixture.mode.PSObject.Properties['graphQlError']) { [string]$fixture.mode.graphQlError } else { '' }
$missingCursor = if ($null -ne $fixture.mode.PSObject.Properties['missingCursor']) { [string]$fixture.mode.missingCursor } else { '' }
$repeatedCursor = if ($null -ne $fixture.mode.PSObject.Properties['repeatedCursor']) { [string]$fixture.mode.repeatedCursor } else { '' }
$countMismatch = if ($null -ne $fixture.mode.PSObject.Properties['countMismatch']) { [string]$fixture.mode.countMismatch } else { '' }
if ($graphQlError -eq $operation) {
    [pscustomobject]@{ errors = @([pscustomobject]@{ message = 'frozen error' }); data = $null } | ConvertTo-Json -Depth 100
    exit 0
}
$all = if ($isIssue) { @($fixture.issues) } else { @($fixture.pullRequests) }
$offset = if ([string]::IsNullOrWhiteSpace($cursor)) { 0 } elseif ($cursor -eq 'R') { 100 } elseif ($cursor -match '^[IP](\d+)$') { [int]$Matches[1] } else { throw "Unknown cursor $cursor" }
$nodes = @($all | Select-Object -Skip $offset -First 100)
$nextOffset = $offset + $nodes.Count
$hasNext = $nextOffset -lt $all.Count
$endCursor = if ($hasNext) { "$(if ($isIssue) { 'I' } else { 'P' })$nextOffset" } else { $null }
if ($missingCursor -eq $operation -and $hasNext) { $endCursor = $null }
if ($repeatedCursor -eq $operation -and $hasNext) { $endCursor = 'R' }
$total = $all.Count
if ($countMismatch -eq $operation) { $total++ }
$connection = [pscustomobject]@{ totalCount = $total; nodes = $nodes; pageInfo = [pscustomobject]@{ hasNextPage = $hasNext; endCursor = $endCursor } }
$repository = [ordered]@{ nameWithOwner = [string]$fixture.repository }
$repository[$operation] = $connection
[pscustomobject]@{ data = [pscustomobject]@{ repository = [pscustomobject]$repository } } | ConvertTo-Json -Depth 100
'@
Set-Content -LiteralPath (Join-Path $fixtureRoot 'broker.ps1') -Value $broker -Encoding utf8NoBOM
$launcher = "#!/bin/sh`nexec '$pwsh' -NoProfile -File '$fixtureRoot/broker.ps1' `"`$@`"`n"
Set-Content -LiteralPath (Join-Path $fixtureRoot 'gh') -Value $launcher -Encoding utf8NoBOM
& chmod +x (Join-Path $fixtureRoot 'gh')

try {
    $baseline = New-Fixture
    $run = Invoke-Audit $baseline
    Assert-Equal 0 $run.ExitCode "baseline audit exits successfully: $($run.Result.errors | ConvertTo-Json -Depth 20 -Compress)"
    Assert-Equal 1 $run.Result.schemaVersion 'schema version is frozen'
    Assert-True ([bool]$run.Result.readOnly) 'result declares read-only behavior'
    Assert-Equal @('all', $null) @($run.Result.scope.mode, $run.Result.scope.campaign) 'whole-repository scope is reported'
    Assert-Equal @(1, 100) @($run.Result.campaigns) 'Campaigns are sorted and discovered'
    Assert-Equal @('AIDLC-W001', 'AIDLC-W002') @($run.Result.warnings | ForEach-Object { [string]$_.code }) 'only explicit posture warnings are emitted'
    Assert-Equal @(6, 7) @($run.Result.warnings | ForEach-Object { [int]$_.issue }) 'warning issue identities are stable'
    Assert-Equal 0 @($run.Result.errors).Count 'baseline has no errors'
    Assert-Equal 109 $run.Result.collection.collectedIssues 'all issue pages are collected'
    Assert-Equal 101 $run.Result.collection.collectedOpenPullRequests 'all PR pages are collected'
    Assert-Equal 4 $run.Result.collection.graphQlRequests 'API requests equal root page count'
    Assert-Equal 4 $run.Calls.Count 'fake broker observes only four root page calls'
    Assert-True ($run.Calls -contains 'issues|I100') 'the second issue page is fetched'
    Assert-True ($run.Calls -contains 'pullRequests|P100') 'the second PR page is fetched'
    Assert-True ((Get-Issue $baseline 338).id -ceq 'I_kwDOSxkvdc8AAAABMEqXQQ' -and (Get-Issue $baseline 339).id -ceq 'I_kwDOSxkvdc8AAAABMEqXqQ') 'case-distinct live-shape GraphQL IDs straddle issue pages'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 339).id = (Get-Issue $fixture 338).id
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E005' 'an actual exact duplicate GraphQL ID is rejected'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 339).locked = $false
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E018' 'an issue from the second issue page is validated'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 201).locked = $false
    $run = Invoke-Audit $fixture @('-Campaign', '1')
    Assert-Equal 0 $run.ExitCode 'scoped mode ignores invalid records outside selected native subtree'
    Assert-Equal 'campaign' $run.Result.scope.mode 'scoped mode is reported'
    Assert-Equal 1 $run.Result.scope.campaign 'scoped Campaign is reported'
    Assert-Equal 11 $run.Result.auditedIssues 'scoped mode follows actual native descendants'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 2).body = "Work level: Phase. Native parent: #100.`nCurrent ancestry: #100.`n`n### Parent Campaign`n#100"
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E013' 'stale body parent and ancestry are rejected'
    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 4).body = "Work level: Bolt. Native parent: #3.`nCurrent ancestry: #2 → #3 → #1."
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E013' 'ancestry remains an ordered immediate-parent-to-root contract'

    $fixture = Copy-Fixture $baseline
    $closed = Get-Issue $fixture 201
    $closed.labels = New-Labels -Work 'work:finding' -Status 'status:ready'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E017' 'closed status is rejected'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 3).state = 'CLOSED'
    (Get-Issue $fixture 3).labels = New-Labels -Work 'work:uow'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E014' 'open descendants below a closed aggregate are rejected'

    $fixture = Copy-Fixture $baseline
    Set-Parent (Get-Issue $fixture 200) 4
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E015' 'Bolt children are rejected'

    $fixture = Copy-Fixture $baseline
    $fixture.pullRequests[0].closingIssuesReferences = (New-PullRequest 999 @(3, 4)).closingIssuesReferences
    $fixture.pullRequests[1].closingIssuesReferences = (New-PullRequest 999 @(4)).closingIssuesReferences
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E022' 'PR closure of an aggregate is rejected'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E023' 'multiple open closers for one Bolt are rejected'

    $fixture = Copy-Fixture $baseline
    foreach ($number in 300..310) { $fixture.issues += New-Issue $number 'work:bolt' -Parent 3 -Status 'status:queued' -Body 'Native parent: #3' }
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E021' 'thirteen active Bolts are rejected'
    (Get-Issue $fixture 310).state = 'CLOSED'
    (Get-Issue $fixture 310).labels = New-Labels -Work 'work:bolt'
    $twelveActiveRun = Invoke-Audit $fixture
    Assert-True (-not (@($twelveActiveRun.Result.errors | ForEach-Object { [string]$_.code }) -contains 'AIDLC-E021')) 'twelve active among thirteen total Bolts is accepted'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 4).locked = $false
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E018' 'unlocked governed issues are rejected'

    $fixture = Copy-Fixture $baseline
    $blocked = Get-Issue $fixture 4
    $blocked.blockedBy = [pscustomobject]@{ totalCount = 1; nodes = @([pscustomobject]@{ number = 5 }) }
    $blocked.labels = New-Labels -Work 'work:bolt' -Status 'status:blocked'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E019' 'native blocked-by relationships are rejected'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E020' 'blocked status requires the human contract'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 4).labels = New-Labels -Work 'work:bolt' -Status 'status:tracking' -Type ''
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E016' 'missing open label dimension is rejected'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E017' 'invalid level status is rejected'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 4).labels.nodes += [pscustomobject]@{ name = 'work:finding' }
    (Get-Issue $fixture 4).labels.totalCount++
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E010' 'duplicate work-level labels are rejected'

    $fixture = Copy-Fixture $baseline
    Set-Parent (Get-Issue $fixture 2) 3
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E012' 'wrong immediate level and parent cycles are rejected'
    $fixture = Copy-Fixture $baseline
    Set-Parent (Get-Issue $fixture 2) 1 'other/repo'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E012' 'cross-repository native parents are rejected'

    $fixture = Copy-Fixture $baseline
    $fixture.mode | Add-Member missingCursor issues
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E004' 'missing pagination cursor fails closed'
    $fixture = Copy-Fixture $baseline
    foreach ($number in 400..500) { $fixture.issues += New-Issue $number 'work:finding' -State 'CLOSED' }
    $fixture.mode | Add-Member repeatedCursor issues
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E004' 'repeated pagination cursor fails closed'
    $fixture = Copy-Fixture $baseline
    $fixture.mode | Add-Member countMismatch issues
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E005' 'root count mismatch fails closed'
    $fixture = Copy-Fixture $baseline
    $fixture.mode | Add-Member graphQlError issues
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E003' 'GraphQL errors fail closed'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 4).labels.totalCount = 51
    (Get-Issue $fixture 4).labels.pageInfo.hasNextPage = $true
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E006' 'label overflow fails closed'
    $fixture = Copy-Fixture $baseline
    $fixture.pullRequests[0].closingIssuesReferences.totalCount = 101
    $fixture.pullRequests[0].closingIssuesReferences.pageInfo.hasNextPage = $true
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E006' 'closing-reference overflow fails closed'

    Assert-HasCode (Invoke-Audit $baseline @('-MaximumGraphQlRequests', '1')) 'AIDLC-E001' 'request cap fails closed'
    Assert-HasCode (Invoke-Audit $baseline @('-MaximumCollectedNodes', '10')) 'AIDLC-E002' 'node cap fails closed'

    $fixture = Copy-Fixture $baseline
    (Get-Issue $fixture 6).body = 'Native parent: #2'
    Assert-HasCode (Invoke-Audit $fixture) 'AIDLC-E024' 'under-decomposed UOW without an explicit posture is rejected'

    $workflowText = Get-Content -Raw -LiteralPath $workflow
    $scriptText = Get-Content -Raw -LiteralPath $auditScript
    Assert-True ($workflowText -match '(?m)^\s*workflow_dispatch:\s*$' -and $workflowText -match '(?m)^\s*schedule:\s*$') 'workflow retains manual and scheduled triggers'
    Assert-True ($workflowText -notmatch '(?m)^\s*(issues|issue_comment|pull_request|push):\s*$') 'workflow has no event-storm trigger'
    Assert-True ($workflowText -match '(?m)^\s*contents:\s*read\s*$' -and $workflowText -match '(?m)^\s*issues:\s*read\s*$' -and $workflowText -match '(?m)^\s*pull-requests:\s*read\s*$') 'workflow permissions remain read-only'
    Assert-True ($workflowText.IndexOf('audit-issue-hierarchy.tests.ps1', [StringComparison]::Ordinal) -lt $workflowText.LastIndexOf('audit-issue-hierarchy.ps1', [StringComparison]::Ordinal)) 'frozen tests run before the live audit'
    Assert-True ($workflowText -notmatch '(?i)-Campaign|-Phase') 'live workflow has no hardcoded Campaign or Phase'
    Assert-True ($scriptText -notmatch '(?i)\bmutation\b|/sub_issues|/dependencies|gh\s+(issue|pr)') 'auditor contains only static read-query API behavior'

    Write-Host "audit-issue-hierarchy.tests.ps1 passed with $script:assertions assertions."
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
