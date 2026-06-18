param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = '',
    [string]$Password = '',
    [string]$BearerToken = '',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\rental-customer-link-repair'),
    [string]$CandidatePlanPath = '',
    [string]$ApprovalPlanPath = '',
    [string[]]$ProfileIds = @(),
    [int]$ExpectedCandidateCount = -1,
    [switch]$FailOnCandidates,
    [switch]$BackupConfirmed,
    [switch]$RestorePossible,
    [string]$ApprovedBy = '',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Read-ErrorResponseBody {
    param([System.Net.WebResponse]$Response)
    if ($null -eq $Response) { return '' }
    try {
        $stream = $Response.GetResponseStream()
        if ($null -eq $stream) { return '' }
        $reader = [System.IO.StreamReader]::new($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    catch { return '' }
}

function Invoke-Api {
    param(
        [ValidateSet('GET','POST')][string]$Method,
        [string]$Relative,
        [hashtable]$Headers,
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200),
        [int]$TimeoutSec = 120
    )

    $uri = if ($Relative.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) {
        $Relative
    }
    else {
        "$($BaseUrl.TrimEnd('/'))/$($Relative.TrimStart('/'))"
    }

    $parameters = @{
        Method = $Method
        Uri = $uri
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($Headers) { $parameters.Headers = $Headers }
    if ($null -ne $Body) {
        $parameters.Body = ($Body | ConvertTo-Json -Depth 80 -Compress)
        $parameters.ContentType = 'application/json; charset=utf-8'
    }

    $status = 0
    $content = ''
    try {
        $response = Invoke-WebRequest @parameters
        $status = [int]$response.StatusCode
        $content = [string]$response.Content
    }
    catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            $content = Read-ErrorResponseBody $_.Exception.Response
        }
        else {
            throw
        }
    }

    if ($ExpectedStatus -notcontains $status) {
        throw "API $Method $uri failed. status=$status expected=$($ExpectedStatus -join ',') body=$content"
    }

    $bodyObject = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $bodyObject = $content | ConvertFrom-Json }
        catch { $bodyObject = $content }
    }

    [pscustomobject]@{
        StatusCode = $status
        Body = $bodyObject
        Raw = $content
        Uri = $uri
    }
}

function New-AuthHeaders {
    if ([string]::IsNullOrWhiteSpace($BearerToken)) {
        if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
            throw 'Username/Password 또는 BearerToken이 필요합니다.'
        }

        $login = Invoke-Api -Method POST -Relative 'auth/login' -Headers @{} -Body @{
            username = $Username
            password = $Password
        } -ExpectedStatus @(200) -TimeoutSec 30

        $script:BearerToken = [string]$login.Body.token
        if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
            $script:BearerToken = [string]$login.Body.accessToken
        }
    }

    if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
        throw 'Login token was not returned.'
    }

    return @{ Authorization = "Bearer $script:BearerToken" }
}

function Get-StringValue {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    return ([string]$Value).Trim()
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) { return $null }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    foreach ($candidate in @($Object.PSObject.Properties)) {
        if ([string]::Equals($candidate.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $candidate.Value
        }
    }

    return $null
}

function Get-LongValue {
    param([object]$Value)
    if ($null -eq $Value) { return [long]0 }
    return [long]$Value
}

function Test-EmptyGuidText {
    param([object]$Value)
    $text = Get-StringValue $Value
    return [string]::IsNullOrWhiteSpace($text) -or
        [string]::Equals($text, '00000000-0000-0000-0000-000000000000', [System.StringComparison]::OrdinalIgnoreCase)
}

function Set-JsonProperty {
    param(
        [object]$Target,
        [string]$Name,
        [object]$Value
    )

    $property = $Target.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Target | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    }
    else {
        $property.Value = $Value
    }
}

function New-EmptySyncPushBody {
    $body = @{ deviceId = 'rental-customer-link-repair' }
    foreach ($name in @(
        'companyProfiles','units','customerCategories','priceGradeOptions','tradeTypeOptions','itemCategoryOptions',
        'customerMasters','customers','customerContracts','items','itemWarehouseStocks','transactions','transactionAttachments',
        'inventoryTransfers','rentalManagementCompanies','rentalBillingProfiles','rentalAssets','rentalAssetAssignmentHistories',
        'rentalBillingLogs','invoices','payments')) {
        $body[$name] = @()
    }
    return $body
}

function Invoke-SyncPush {
    param(
        [hashtable]$Headers,
        [object[]]$Profiles,
        [object[]]$Assets
    )

    $body = New-EmptySyncPushBody
    $body['rentalBillingProfiles'] = @($Profiles)
    $body['rentalAssets'] = @($Assets)
    $result = (Invoke-Api -Method POST -Relative 'sync/push' -Headers $Headers -Body $body -ExpectedStatus @(200) -TimeoutSec 180).Body
    if ([int]$result.conflictCount -ne 0) {
        throw "sync/push conflict. conflicts=$($result.conflicts | ConvertTo-Json -Depth 40 -Compress)"
    }

    $expectedAccepted = @($Profiles).Count + @($Assets).Count
    $handledCount = [int]$result.acceptedCount + [int]$result.duplicateMutationCount
    if ($handledCount -lt $expectedAccepted) {
        throw "sync/push did not accept all mutations. expected=$expectedAccepted result=$($result | ConvertTo-Json -Depth 40 -Compress)"
    }

    return $result
}

function Read-PlanRows {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'CandidatePlanPath 또는 ApprovalPlanPath가 필요합니다.'
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "계획 파일을 찾지 못했습니다: $Path"
    }

    $plan = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $rowSource = Get-ObjectPropertyValue -Object $plan -Name 'proposedLinks'
    $rows = @($rowSource | Where-Object { $null -ne $_ })
    if ($rows.Count -eq 0) {
        $rowSource = Get-ObjectPropertyValue -Object $plan -Name 'rows'
        $rows = @($rowSource | Where-Object { $null -ne $_ })
    }
    if ($rows.Count -eq 0) {
        throw "계획 파일에 proposedLinks 또는 rows가 없습니다: $Path"
    }

    return @($rows)
}

function Assert-ApprovalPlanMatches {
    param(
        [string]$Path,
        [object[]]$Rows
    )

    $approvedRows = Read-PlanRows -Path $Path
    if ($approvedRows.Count -ne $Rows.Count) {
        throw "approval plan row count mismatch. expected=$($approvedRows.Count) actual=$($Rows.Count)"
    }

    $approvedByProfileId = @{}
    foreach ($row in $approvedRows) {
        $approvedByProfileId[(Get-StringValue (Get-ObjectPropertyValue -Object $row -Name 'profileId'))] = $row
    }

    foreach ($row in $Rows) {
        $profileId = Get-StringValue $row.ProfileId
        if (-not $approvedByProfileId.ContainsKey($profileId)) {
            throw "approval plan does not contain profileId=$profileId"
        }

        $expected = $approvedByProfileId[$profileId]
        foreach ($field in @('candidateCustomerId','candidateCustomerName','profileCustomerName')) {
            $expectedValue = Get-StringValue (Get-ObjectPropertyValue -Object $expected -Name $field)
            $actualProperty = switch ($field) {
                'candidateCustomerId' { 'CandidateCustomerId' }
                'candidateCustomerName' { 'CandidateCustomerName' }
                'profileCustomerName' { 'ProfileCustomerName' }
            }
            $actualValue = Get-StringValue (Get-ObjectPropertyValue -Object $row -Name $actualProperty)
            if (-not [string]::Equals($expectedValue, $actualValue, [System.StringComparison]::Ordinal)) {
                throw "approval plan mismatch for $profileId.$field. expected='$expectedValue' actual='$actualValue'"
            }
        }

        $expectedRevision = Get-LongValue (Get-ObjectPropertyValue -Object $expected -Name 'expectedProfileRevision')
        if ($expectedRevision -gt 0 -and $expectedRevision -ne [long]$row.ExpectedProfileRevision) {
            throw "approval plan revision mismatch for $profileId. expected=$expectedRevision actual=$($row.ExpectedProfileRevision)"
        }
    }
}

function Copy-JsonObject {
    param([object]$InputObject)
    return ($InputObject | ConvertTo-Json -Depth 80 | ConvertFrom-Json)
}

New-DirectoryIfMissing -Path $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ($Apply -and (-not $BackupConfirmed -or -not $RestorePossible -or [string]::IsNullOrWhiteSpace($ApprovedBy))) {
    throw 'Apply 모드에는 -BackupConfirmed -RestorePossible -ApprovedBy 값이 모두 필요합니다.'
}
if ($Apply -and [string]::IsNullOrWhiteSpace($ApprovalPlanPath)) {
    throw 'Apply 모드에는 최신 dry-run JSON을 -ApprovalPlanPath로 지정해야 합니다.'
}

$sourcePlanPath = if ($Apply) { $ApprovalPlanPath } else { $CandidatePlanPath }
$planRows = Read-PlanRows -Path $sourcePlanPath
$profileIdSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($id in $ProfileIds) {
    if (-not [string]::IsNullOrWhiteSpace($id)) {
        $profileIdSet.Add($id.Trim()) | Out-Null
    }
}

$headers = New-AuthHeaders
$pull = (Invoke-Api -Method GET -Relative 'sync/pull?sinceRev=0' -Headers $headers -ExpectedStatus @(200) -TimeoutSec 180).Body
$profilesById = @{}
foreach ($profile in @($pull.rentalBillingProfiles | Where-Object { -not $_.isDeleted })) {
    $profilesById[(Get-StringValue $profile.id)] = $profile
}
$customersById = @{}
foreach ($customer in @($pull.customers | Where-Object { -not $_.isDeleted })) {
    $customersById[(Get-StringValue $customer.id)] = $customer
}
$assetsByProfileId = @{}
foreach ($asset in @($pull.rentalAssets | Where-Object { -not $_.isDeleted -and -not [string]::IsNullOrWhiteSpace((Get-StringValue $_.billingProfileId)) })) {
    $profileId = Get-StringValue $asset.billingProfileId
    if (-not $assetsByProfileId.ContainsKey($profileId)) {
        $assetsByProfileId[$profileId] = New-Object System.Collections.Generic.List[object]
    }
    $assetsByProfileId[$profileId].Add($asset) | Out-Null
}

$rows = New-Object System.Collections.Generic.List[object]
$profilesToApply = New-Object System.Collections.Generic.List[object]
$assetsToApply = New-Object System.Collections.Generic.List[object]

foreach ($planRow in $planRows) {
    $profileId = Get-StringValue (Get-ObjectPropertyValue -Object $planRow -Name 'profileId')
    if ($profileIdSet.Count -gt 0 -and -not $profileIdSet.Contains($profileId)) { continue }

    $candidateCustomerId = Get-StringValue (Get-ObjectPropertyValue -Object $planRow -Name 'candidateCustomerId')
    if ([string]::IsNullOrWhiteSpace($profileId) -or [string]::IsNullOrWhiteSpace($candidateCustomerId)) {
        throw "profileId/candidateCustomerId가 비어 있습니다. profileId=$profileId"
    }
    if (-not $profilesById.ContainsKey($profileId)) {
        throw "프로필을 찾지 못했습니다: $profileId"
    }
    if (-not $customersById.ContainsKey($candidateCustomerId)) {
        throw "후보 거래처를 찾지 못했습니다: profileId=$profileId customerId=$candidateCustomerId"
    }

    $profile = $profilesById[$profileId]
    $candidateCustomer = $customersById[$candidateCustomerId]
    [object[]]$linkedAssets = if ($assetsByProfileId.ContainsKey($profileId)) { @($assetsByProfileId[$profileId].ToArray()) } else { @() }
    $assetMutations = @($linkedAssets | Where-Object { Test-EmptyGuidText $_.customerId })

    $row = [pscustomobject]@{
        ProfileId = $profileId
        ProfileCustomerName = Get-StringValue $profile.customerName
        CandidateCustomerId = $candidateCustomerId
        CandidateCustomerName = Get-StringValue $candidateCustomer.nameOriginal
        BillingStatus = Get-StringValue $profile.billingStatus
        ExpectedProfileRevision = Get-LongValue $profile.revision
        ExistingProfileCustomerId = Get-StringValue $profile.customerId
        LinkedAssetCount = $linkedAssets.Count
        AssetMutationCount = $assetMutations.Count
        AssetIds = @($assetMutations | ForEach-Object { Get-StringValue $_.id })
        ApplyEligible = Test-EmptyGuidText $profile.customerId
        Applied = $false
    }
    $rows.Add($row) | Out-Null

    if ($Apply) {
        if (-not $row.ApplyEligible) {
            throw "profileId=$profileId 는 이미 CustomerId가 있어 자동 적용 대상이 아닙니다."
        }

        $profileMutation = Copy-JsonObject $profile
        Set-JsonProperty -Target $profileMutation -Name 'customerId' -Value $candidateCustomerId
        Set-JsonProperty -Target $profileMutation -Name 'customerName' -Value (Get-StringValue $candidateCustomer.nameOriginal)
        Set-JsonProperty -Target $profileMutation -Name 'expectedRevision' -Value ([long]$profile.revision)
        Set-JsonProperty -Target $profileMutation -Name 'mutationId' -Value (([guid]::NewGuid()).ToString())
        Set-JsonProperty -Target $profileMutation -Name 'mutationCreatedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
        $profilesToApply.Add($profileMutation) | Out-Null

        foreach ($asset in $assetMutations) {
            $assetMutation = Copy-JsonObject $asset
            Set-JsonProperty -Target $assetMutation -Name 'customerId' -Value $candidateCustomerId
            Set-JsonProperty -Target $assetMutation -Name 'customerName' -Value (Get-StringValue $candidateCustomer.nameOriginal)
            Set-JsonProperty -Target $assetMutation -Name 'currentCustomerName' -Value (Get-StringValue $candidateCustomer.nameOriginal)
            Set-JsonProperty -Target $assetMutation -Name 'expectedRevision' -Value ([long]$asset.revision)
            Set-JsonProperty -Target $assetMutation -Name 'mutationId' -Value (([guid]::NewGuid()).ToString())
            Set-JsonProperty -Target $assetMutation -Name 'mutationCreatedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
            $assetsToApply.Add($assetMutation) | Out-Null
        }
    }
}

if ($ExpectedCandidateCount -ge 0 -and $rows.Count -ne $ExpectedCandidateCount) {
    throw "candidate count mismatch. expected=$ExpectedCandidateCount actual=$($rows.Count)"
}

if ($Apply) {
    Assert-ApprovalPlanMatches -Path $ApprovalPlanPath -Rows @($rows.ToArray())
    Invoke-SyncPush -Headers $headers -Profiles @($profilesToApply.ToArray()) -Assets @($assetsToApply.ToArray()) | Out-Null
    foreach ($row in $rows) { $row.Applied = $true }
}

$plannedProfileMutationCount = @($rows | Where-Object { $_.ApplyEligible }).Count
$plannedAssetMutationCount = 0
foreach ($row in $rows) {
    $plannedAssetMutationCount += [int]$row.AssetMutationCount
}

$jsonPath = Join-Path $EvidenceDirectory "rental-customer-link-repair-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "rental-customer-link-repair-$timestamp.md"
$report = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    Apply = [bool]$Apply
    CandidatePlanPath = $CandidatePlanPath
    ApprovalPlanPath = $ApprovalPlanPath
    BackupConfirmed = [bool]$BackupConfirmed
    RestorePossible = [bool]$RestorePossible
    ApprovedBy = $ApprovedBy
    CandidateCount = $rows.Count
    PlannedProfileMutationCount = $plannedProfileMutationCount
    PlannedAssetMutationCount = $plannedAssetMutationCount
    ProfileMutationCount = $profilesToApply.Count
    AssetMutationCount = $assetsToApply.Count
    Rows = @($rows.ToArray())
}
$report | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# rental customer link repair report') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- generatedAt: $($report.GeneratedAt)") | Out-Null
$lines.Add("- baseUrl: $BaseUrl") | Out-Null
$lines.Add("- apply: $([bool]$Apply)") | Out-Null
$lines.Add("- candidatePlanPath: $CandidatePlanPath") | Out-Null
$lines.Add("- approvalPlanPath: $ApprovalPlanPath") | Out-Null
$lines.Add("- backupConfirmed: $([bool]$BackupConfirmed)") | Out-Null
$lines.Add("- restorePossible: $([bool]$RestorePossible)") | Out-Null
$lines.Add("- approvedBy: $ApprovedBy") | Out-Null
$lines.Add("- candidateCount: $($rows.Count)") | Out-Null
$lines.Add("- plannedProfileMutationCount: $plannedProfileMutationCount") | Out-Null
$lines.Add("- plannedAssetMutationCount: $plannedAssetMutationCount") | Out-Null
$lines.Add("- profileMutationCount: $($profilesToApply.Count)") | Out-Null
$lines.Add("- assetMutationCount: $($assetsToApply.Count)") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| applied | profileId | profileCustomer | currentCustomerId | candidateCustomer | candidateCustomerId | billingStatus | profileRev | linkedAssets | assetMutations |') | Out-Null
$lines.Add('|---|---|---|---|---|---|---|---:|---:|---:|') | Out-Null
foreach ($row in $rows) {
    $lines.Add("| $($row.Applied) | $($row.ProfileId) | $($row.ProfileCustomerName) | $($row.ExistingProfileCustomerId) | $($row.CandidateCustomerName) | $($row.CandidateCustomerId) | $($row.BillingStatus) | $($row.ExpectedProfileRevision) | $($row.LinkedAssetCount) | $($row.AssetMutationCount) |") | Out-Null
}
$lines.Add('') | Out-Null
if (-not $Apply -and $rows.Count -gt 0) {
    $profileArgs = (@($rows | ForEach-Object { "'$($_.ProfileId)'" }) -join ',')
    $lines.Add('## 안전 적용 예시') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('운영 적용 전에는 최신 백업/복구 가능성, 후보 거래처 정확성, 최신 dry-run JSON을 확인한 뒤 아래처럼 실행하세요.') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('```powershell') | Out-Null
    $lines.Add('$env:GEORAEPLAN_RENTAL_CUSTOMER_LINK_USERNAME = ''<사용자명>''') | Out-Null
    $lines.Add('$env:GEORAEPLAN_RENTAL_CUSTOMER_LINK_PASSWORD = ''<비밀번호>''') | Out-Null
    $lines.Add("& 'D:\거래플랜\tools\maintenance\Invoke-GeoraePlanRentalCustomerLinkRepair.ps1' ``") | Out-Null
    $lines.Add("  -BaseUrl '$BaseUrl' ``") | Out-Null
    $lines.Add('  -Username $env:GEORAEPLAN_RENTAL_CUSTOMER_LINK_USERNAME `') | Out-Null
    $lines.Add('  -Password $env:GEORAEPLAN_RENTAL_CUSTOMER_LINK_PASSWORD `') | Out-Null
    $lines.Add("  -ApprovalPlanPath '$jsonPath' ``") | Out-Null
    $lines.Add("  -ProfileIds @($profileArgs) ``") | Out-Null
    $lines.Add("  -ExpectedCandidateCount $($rows.Count) ``") | Out-Null
    $lines.Add("  -BackupConfirmed -RestorePossible -ApprovedBy '<승인자>' ``") | Out-Null
    $lines.Add('  -Apply') | Out-Null
    $lines.Add('```') | Out-Null
    $lines.Add('') | Out-Null
}
$lines.Add("JSON: $jsonPath") | Out-Null
Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "rental_customer_link_repair_report=$mdPath"
Write-Host "rental_customer_link_repair_json=$jsonPath"
Write-Host "candidate_count=$($rows.Count)"
Write-Host "apply=$([bool]$Apply)"

if ($FailOnCandidates -and -not $Apply -and $rows.Count -gt 0) {
    throw "rental customer link candidates remain. count=$($rows.Count). report=$mdPath"
}
