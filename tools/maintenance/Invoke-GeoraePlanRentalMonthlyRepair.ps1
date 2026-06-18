param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$BearerToken = '',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\rental-monthly-repair'),
    [string[]]$ProfileIds = @(),
    [int]$ExpectedCandidateCount = -1,
    [string]$ApprovalPlanPath = '',
    [switch]$AllowApplyAll,
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
        $parameters.Body = ($Body | ConvertTo-Json -Depth 50 -Compress)
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
        else { throw }
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
        $login = Invoke-Api -Method POST -Relative 'auth/login' -Body @{
            username = $Username
            password = $Password
        } -Headers @{} -ExpectedStatus @(200) -TimeoutSec 30

        $script:BearerToken = [string]$login.Body.token
        if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
            $script:BearerToken = [string]$login.Body.accessToken
        }
    }

    if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
        throw 'Login token was not returned.'
    }

    return @{
        Authorization = "Bearer $script:BearerToken"
        'X-Business-Database' = 'USENET'
    }
}

function Get-Decimal {
    param([object]$Value)
    if ($null -eq $Value) { return [decimal]0 }
    return [decimal]$Value
}

function Get-StringValue {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    return ([string]$Value).Trim()
}

function Set-NoteProperty {
    param([object]$InputObject, [string]$Name, [object]$Value)
    if ($null -eq $InputObject.PSObject.Properties[$Name]) {
        $InputObject | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $InputObject.$Name = $Value
    }
}

function Get-TemplateItems {
    param([string]$BillingTemplateJson)
    if ([string]::IsNullOrWhiteSpace($BillingTemplateJson)) { return @() }
    try {
        $parsed = $BillingTemplateJson | ConvertFrom-Json
        if ($null -eq $parsed) { return @() }
        return @($parsed)
    }
    catch { return @() }
}

function Get-TemplateLineAmount {
    param([object]$Item)
    $quantity = Get-Decimal $Item.Quantity
    if ($quantity -le 0) { $quantity = 1 }
    $unitPrice = Get-Decimal $Item.UnitPrice
    $amount = $quantity * $unitPrice
    if ($amount -gt 0) { return $amount }
    return [Math]::Max([decimal]0, (Get-Decimal $Item.Amount))
}

function New-MissingAssetRows {
    param(
        [string[]]$MissingIds,
        [hashtable]$LinkedAssetMap
    )

    @($MissingIds | ForEach-Object {
        $id = [string]$_
        if (-not [string]::IsNullOrWhiteSpace($id) -and $LinkedAssetMap.ContainsKey($id)) {
            $asset = $LinkedAssetMap[$id]
            [pscustomobject]@{
                AssetId = $id
                ItemName = [string]$asset.itemName
                SerialNumber = [string]$asset.serialNumber
                InstallLocation = [string]$asset.installLocation
                MonthlyFee = [Math]::Max([decimal]0, (Get-Decimal $asset.monthlyFee))
            }
        }
    })
}

function Format-MissingAssetPreview {
    param([object[]]$MissingAssets)
    $rows = @($MissingAssets | Select-Object -First 5 | ForEach-Object {
        $name = Get-StringValue $_.ItemName
        if ([string]::IsNullOrWhiteSpace($name)) { $name = Get-StringValue $_.AssetId }
        $fee = [decimal]$_.MonthlyFee
        if ($fee -gt 0) { "$name $($fee.ToString('N0'))" } else { "$name 0" }
    })
    if ($MissingAssets.Count -gt 5) {
        $rows += "외 $($MissingAssets.Count - 5)건"
    }
    return ($rows -join ', ')
}

function Get-DefaultRentalFeeName {
    return -join ([char[]](0xB80C, 0xD0C8, 0xB8CC))
}

function Get-DefaultBundleMode {
    return -join ([char[]](0xBB36, 0xC74C))
}

function Set-NormalizedTemplateFromAssets {
    param([object]$Profile, [object[]]$LinkedAssets)

    $defaultItemName = Get-DefaultRentalFeeName
    $defaultBundleMode = Get-DefaultBundleMode
    $linkedAssets = @($LinkedAssets | Where-Object { -not $_.isDeleted -and -not [string]::IsNullOrWhiteSpace([string]$_.id) })
    $linkedAssetMap = @{}
    foreach ($asset in $linkedAssets) { $linkedAssetMap[[string]$asset.id] = $asset }

    $linkedIds = @($linkedAssetMap.Keys | Sort-Object)
    $linkedMonthlyAmount = [decimal](@($linkedAssets | ForEach-Object { [Math]::Max([decimal]0, (Get-Decimal $_.monthlyFee)) }) | Measure-Object -Sum).Sum
    $templateItems = @(Get-TemplateItems -BillingTemplateJson ([string]$Profile.billingTemplateJson))
    $originalTemplateMonthlyAmount = [decimal](@($templateItems | ForEach-Object { Get-TemplateLineAmount $_ }) | Measure-Object -Sum).Sum
    $profileTemplateSelfConsistent = $templateItems.Count -gt 0 -and
        (Test-DecimalEquals -Left (Get-Decimal $Profile.monthlyAmount) -Right $originalTemplateMonthlyAmount)

    if ($templateItems.Count -eq 0) {
        $displayItemName = $defaultItemName
        if (-not [string]::IsNullOrWhiteSpace([string]$Profile.itemName)) {
            $displayItemName = [string]$Profile.itemName
        }
        $billingLineMode = $defaultBundleMode
        if (-not [string]::IsNullOrWhiteSpace([string]$Profile.billingType)) {
            $billingLineMode = [string]$Profile.billingType
        }
        $templateItems = @([pscustomobject]@{
            ItemId = ([guid]::NewGuid()).ToString()
            DisplayItemName = $displayItemName
            BillingLineMode = $billingLineMode
            Specification = ''
            Unit = ''
            MaterialNumber = ''
            RepresentativeAssetId = $null
            Quantity = 1
            UnitPrice = $linkedMonthlyAmount
            Amount = $linkedMonthlyAmount
            Note = ''
            IncludedAssetIds = @($linkedIds)
        })
    }

    $assigned = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $targetIndex = -1
    for ($i = 0; $i -lt $templateItems.Count; $i++) {
        $item = $templateItems[$i]
        $normalizedIds = New-Object System.Collections.Generic.List[string]
        foreach ($rawId in @($item.IncludedAssetIds)) {
            $id = [string]$rawId
            if ([string]::IsNullOrWhiteSpace($id) -or -not $linkedAssetMap.ContainsKey($id)) { continue }
            if ($assigned.Add($id)) { $normalizedIds.Add($id) | Out-Null }
        }
        if ($targetIndex -lt 0 -and $normalizedIds.Count -gt 0) { $targetIndex = $i }
        Set-NoteProperty -InputObject $item -Name 'IncludedAssetIds' -Value @($normalizedIds)
    }

    $missingIds = @($linkedIds | Where-Object { -not $assigned.Contains([string]$_) })
    $missingAssets = @(New-MissingAssetRows -MissingIds $missingIds -LinkedAssetMap $linkedAssetMap)
    $missingAssetMonthlyAmount = [decimal](@($missingAssets | ForEach-Object { [Math]::Max([decimal]0, (Get-Decimal $_.MonthlyFee)) }) | Measure-Object -Sum).Sum
    if ($missingIds.Count -gt 0) {
        if ($targetIndex -lt 0) { $targetIndex = 0 }
        $currentIds = New-Object System.Collections.Generic.List[string]
        foreach ($id in @($templateItems[$targetIndex].IncludedAssetIds)) { $currentIds.Add([string]$id) | Out-Null }
        foreach ($id in $missingIds) { $currentIds.Add([string]$id) | Out-Null }
        Set-NoteProperty -InputObject $templateItems[$targetIndex] -Name 'IncludedAssetIds' -Value @($currentIds)
    }

    foreach ($item in $templateItems) {
        $itemAssetIds = @($item.IncludedAssetIds | Where-Object { $linkedAssetMap.ContainsKey([string]$_) })
        if ($itemAssetIds.Count -eq 0) { continue }

        $itemAssets = @($itemAssetIds | ForEach-Object { $linkedAssetMap[[string]$_] })
        $totalMonthlyFee = [decimal](@($itemAssets | ForEach-Object { [Math]::Max([decimal]0, (Get-Decimal $_.monthlyFee)) }) | Measure-Object -Sum).Sum
        if ($totalMonthlyFee -le 0) { continue }

        $distinctPositiveFees = @($itemAssets | ForEach-Object { [Math]::Max([decimal]0, (Get-Decimal $_.monthlyFee)) } | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
        $lineMode = Get-StringValue $item.BillingLineMode
        if ([string]::IsNullOrWhiteSpace($lineMode)) { $lineMode = Get-StringValue $Profile.billingType }
        $shouldBundle = $itemAssets.Count -eq 1 -or [string]::Equals($lineMode, $defaultBundleMode, [System.StringComparison]::OrdinalIgnoreCase) -or $distinctPositiveFees.Count -ne 1
        $quantity = [decimal]1
        if (-not $shouldBundle) {
            $quantity = [decimal]$itemAssets.Count
        }
        $unitPrice = $totalMonthlyFee
        if (-not $shouldBundle) {
            $unitPrice = [decimal]$distinctPositiveFees[0]
        }
        $amount = $quantity * $unitPrice

        if ([string]::IsNullOrWhiteSpace((Get-StringValue $item.ItemId))) { Set-NoteProperty -InputObject $item -Name 'ItemId' -Value ([guid]::NewGuid()).ToString() }
        if ([string]::IsNullOrWhiteSpace((Get-StringValue $item.DisplayItemName))) {
            $displayItemName = $defaultItemName
            if (-not [string]::IsNullOrWhiteSpace([string]$Profile.itemName)) {
                $displayItemName = [string]$Profile.itemName
            }
            Set-NoteProperty -InputObject $item -Name 'DisplayItemName' -Value $displayItemName
        }
        if ([string]::IsNullOrWhiteSpace((Get-StringValue $item.BillingLineMode))) {
            $billingLineMode = $defaultBundleMode
            if (-not [string]::IsNullOrWhiteSpace([string]$Profile.billingType)) {
                $billingLineMode = [string]$Profile.billingType
            }
            Set-NoteProperty -InputObject $item -Name 'BillingLineMode' -Value $billingLineMode
        }
        foreach ($name in @('Specification','Unit','MaterialNumber','Note')) {
            if ($null -eq $item.PSObject.Properties[$name]) { Set-NoteProperty -InputObject $item -Name $name -Value '' }
        }
        if ($null -eq $item.PSObject.Properties['RepresentativeAssetId']) { Set-NoteProperty -InputObject $item -Name 'RepresentativeAssetId' -Value $null }
        Set-NoteProperty -InputObject $item -Name 'Quantity' -Value $quantity
        Set-NoteProperty -InputObject $item -Name 'UnitPrice' -Value $unitPrice
        Set-NoteProperty -InputObject $item -Name 'Amount' -Value $amount
    }

    $templateMonthlyAmount = [decimal](@($templateItems | ForEach-Object { Get-TemplateLineAmount $_ }) | Measure-Object -Sum).Sum
    $templateJson = ($templateItems | ConvertTo-Json -Depth 50 -Compress)
    [pscustomobject]@{
        LinkedAssetCount = $linkedAssets.Count
        LinkedAssetMonthlyAmount = $linkedMonthlyAmount
        TemplateMonthlyAmount = $templateMonthlyAmount
        TemplateJson = $templateJson
        OriginalTemplateMonthlyAmount = $originalTemplateMonthlyAmount
        ProfileTemplateSelfConsistent = $profileTemplateSelfConsistent
        ExistingTemplateLinkedAssetCount = $assigned.Count
        MissingAssetCount = $missingAssets.Count
        MissingAssetMonthlyAmount = $missingAssetMonthlyAmount
        MissingAssets = @($missingAssets)
        MissingAssetsPreview = Format-MissingAssetPreview -MissingAssets $missingAssets
    }
}

function Invoke-SyncPushRentalProfile {
    param([object]$Profile, [hashtable]$Headers)
    $body = @{ deviceId = 'rental-monthly-repair' }
    foreach ($name in @(
        'companyProfiles','units','customerCategories','priceGradeOptions','tradeTypeOptions','itemCategoryOptions',
        'customerMasters','customers','customerContracts','items','itemWarehouseStocks','transactions','transactionAttachments',
        'inventoryTransfers','rentalManagementCompanies','rentalBillingProfiles','rentalAssets','rentalAssetAssignmentHistories',
        'rentalBillingLogs','invoices','payments')) {
        $body[$name] = @()
    }
    $body['rentalBillingProfiles'] = @($Profile)
    $result = (Invoke-Api -Method POST -Relative 'sync/push' -Headers $Headers -Body $body -ExpectedStatus @(200) -TimeoutSec 120).Body
    if ([int]$result.conflictCount -ne 0) { throw "sync/push conflict for rentalBillingProfiles. conflicts=$($result.conflicts | ConvertTo-Json -Depth 20 -Compress)" }
    if ([int]$result.acceptedCount -lt 1 -and [int]$result.duplicateMutationCount -lt 1) { throw "sync/push did not accept rentalBillingProfiles. result=$($result | ConvertTo-Json -Depth 20 -Compress)" }
    return $result
}

function Assert-DecimalEquals {
    param(
        [string]$Name,
        [object]$Expected,
        [object]$Actual,
        [decimal]$Tolerance = 0.01
    )

    $expectedDecimal = Get-Decimal $Expected
    $actualDecimal = Get-Decimal $Actual
    if ([Math]::Abs([double]($expectedDecimal - $actualDecimal)) -gt [double]$Tolerance) {
        throw "approval plan mismatch for $Name. expected=$expectedDecimal actual=$actualDecimal"
    }
}

function Test-DecimalEquals {
    param(
        [object]$Left,
        [object]$Right,
        [decimal]$Tolerance = 0.01
    )

    $leftDecimal = Get-Decimal $Left
    $rightDecimal = Get-Decimal $Right
    return [Math]::Abs([double]($leftDecimal - $rightDecimal)) -le [double]$Tolerance
}

function Assert-StringEquals {
    param(
        [string]$Name,
        [object]$Expected,
        [object]$Actual
    )

    $expectedText = Get-StringValue $Expected
    $actualText = Get-StringValue $Actual
    if (-not [string]::Equals($expectedText, $actualText, [System.StringComparison]::Ordinal)) {
        throw "approval plan mismatch for $Name. expected='$expectedText' actual='$actualText'"
    }
}

function Assert-ApprovalPlanMatches {
    param(
        [string]$Path,
        [string]$CurrentBaseUrl,
        [object[]]$Rows
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'Apply 모드에서는 최근 dry-run에서 생성된 -ApprovalPlanPath JSON을 지정해야 합니다.'
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "ApprovalPlanPath 파일을 찾지 못했습니다: $Path"
    }

    $plan = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $planBaseUrl = Get-StringValue $plan.BaseUrl
    if (-not [string]::Equals($planBaseUrl.TrimEnd('/'), $CurrentBaseUrl.TrimEnd('/'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "approval plan baseUrl mismatch. expected=$planBaseUrl actual=$CurrentBaseUrl"
    }

    $planRows = @($plan.Rows)
    if ($planRows.Count -ne $Rows.Count) {
        throw "approval plan row count mismatch. expected=$($planRows.Count) actual=$($Rows.Count)"
    }

    $planIds = @($planRows | ForEach-Object { Get-StringValue $_.ProfileId } | Sort-Object)
    $actualIds = @($Rows | ForEach-Object { Get-StringValue $_.ProfileId } | Sort-Object)
    if (($planIds -join '|') -ne ($actualIds -join '|')) {
        throw "approval plan profile id set mismatch. expected=$($planIds -join ',') actual=$($actualIds -join ',')"
    }

    $planById = @{}
    foreach ($row in $planRows) {
        $planById[(Get-StringValue $row.ProfileId)] = $row
    }

    foreach ($row in $Rows) {
        $profileId = Get-StringValue $row.ProfileId
        $expected = $planById[$profileId]
        Assert-StringEquals -Name "$profileId.CustomerName" -Expected $expected.CustomerName -Actual $row.CustomerName
        Assert-StringEquals -Name "$profileId.ItemName" -Expected $expected.ItemName -Actual $row.ItemName
        Assert-StringEquals -Name "$profileId.BillingType" -Expected $expected.BillingType -Actual $row.BillingType
        Assert-DecimalEquals -Name "$profileId.CurrentMonthlyAmount" -Expected $expected.CurrentMonthlyAmount -Actual $row.CurrentMonthlyAmount
        Assert-DecimalEquals -Name "$profileId.LinkedAssetMonthlyAmount" -Expected $expected.LinkedAssetMonthlyAmount -Actual $row.LinkedAssetMonthlyAmount
        Assert-DecimalEquals -Name "$profileId.Difference" -Expected $expected.Difference -Actual $row.Difference
        Assert-DecimalEquals -Name "$profileId.LinkedAssetCount" -Expected $expected.LinkedAssetCount -Actual $row.LinkedAssetCount
        Assert-DecimalEquals -Name "$profileId.TemplateMonthlyAmountAfter" -Expected $expected.TemplateMonthlyAmountAfter -Actual $row.TemplateMonthlyAmountAfter
        Assert-DecimalEquals -Name "$profileId.MissingAssetCount" -Expected $expected.MissingAssetCount -Actual $row.MissingAssetCount
        Assert-DecimalEquals -Name "$profileId.MissingAssetMonthlyAmount" -Expected $expected.MissingAssetMonthlyAmount -Actual $row.MissingAssetMonthlyAmount
    }
}

New-DirectoryIfMissing -Path $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if ($Apply -and $ProfileIds.Count -eq 0 -and -not $AllowApplyAll) {
    throw 'Apply 모드에서는 실수 방지를 위해 -ProfileIds를 지정해야 합니다. 전체 후보를 적용하려면 -AllowApplyAll을 함께 지정하세요.'
}

if ($Apply -and (-not $BackupConfirmed -or -not $RestorePossible -or [string]::IsNullOrWhiteSpace($ApprovedBy))) {
    throw 'Apply 모드에서는 운영 데이터 보호를 위해 -BackupConfirmed -RestorePossible -ApprovedBy 값을 모두 지정해야 합니다.'
}

if ($Apply -and [string]::IsNullOrWhiteSpace($ApprovalPlanPath)) {
    throw 'Apply 모드에서는 최신 dry-run 결과 JSON을 -ApprovalPlanPath로 지정해야 합니다.'
}

$headers = New-AuthHeaders
$pull = (Invoke-Api -Method GET -Relative 'sync/pull?sinceRev=0' -Headers $headers -ExpectedStatus @(200) -TimeoutSec 180).Body

$profileIdSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($id in $ProfileIds) { if (-not [string]::IsNullOrWhiteSpace($id)) { $profileIdSet.Add($id.Trim()) | Out-Null } }

$assetsByProfile = @{}
foreach ($asset in @($pull.rentalAssets | Where-Object { -not $_.isDeleted -and -not [string]::IsNullOrWhiteSpace([string]$_.billingProfileId) })) {
    $key = [string]$asset.billingProfileId
    if (-not $assetsByProfile.ContainsKey($key)) { $assetsByProfile[$key] = New-Object System.Collections.Generic.List[object] }
    $assetsByProfile[$key].Add($asset) | Out-Null
}

$rows = New-Object System.Collections.Generic.List[object]
$pendingApplications = New-Object System.Collections.Generic.List[object]
foreach ($profile in @($pull.rentalBillingProfiles | Where-Object { -not $_.isDeleted })) {
    $profileId = [string]$profile.id
    if ($profileIdSet.Count -gt 0 -and -not $profileIdSet.Contains($profileId)) { continue }
    if (-not $assetsByProfile.ContainsKey($profileId)) { continue }

    try {
        $linkedProfileAssets = @($assetsByProfile[$profileId].ToArray())
        $normalization = Set-NormalizedTemplateFromAssets -Profile $profile -LinkedAssets $linkedProfileAssets
    }
    catch {
        throw "normalization failed. profileId=$profileId customer=$([string]$profile.customerName) message=$($_.Exception.Message)"
    }
    if ($normalization.LinkedAssetCount -le 0 -or $normalization.LinkedAssetMonthlyAmount -le 0) { continue }

    $currentMonthly = Get-Decimal $profile.monthlyAmount
    $difference = $normalization.LinkedAssetMonthlyAmount - $currentMonthly
    $templateChanged = -not [string]::Equals([string]$profile.billingTemplateJson, [string]$normalization.TemplateJson, [System.StringComparison]::Ordinal)
    if ([Math]::Abs([double]$difference) -lt 0.01) { continue }
    if ([bool]$normalization.ProfileTemplateSelfConsistent -and [int]$normalization.MissingAssetCount -eq 0) { continue }

    $applied = $false
    $pushRevision = [long]0
    if ($Apply) {
        $pendingApplications.Add([pscustomobject]@{
            Profile = $profile
            MonthlyAmount = $normalization.LinkedAssetMonthlyAmount
            TemplateJson = $normalization.TemplateJson
        }) | Out-Null
    }

    $rows.Add([pscustomobject]@{
        ProfileId = $profileId
        CustomerName = [string]$profile.customerName
        ItemName = [string]$profile.itemName
        BillingType = [string]$profile.billingType
        CurrentMonthlyAmount = $currentMonthly
        LinkedAssetMonthlyAmount = $normalization.LinkedAssetMonthlyAmount
        Difference = $difference
        LinkedAssetCount = $normalization.LinkedAssetCount
        TemplateMonthlyAmountAfter = $normalization.TemplateMonthlyAmount
        TemplateChanged = $templateChanged
        ExistingTemplateLinkedAssetCount = $normalization.ExistingTemplateLinkedAssetCount
        MissingAssetCount = $normalization.MissingAssetCount
        MissingAssetMonthlyAmount = $normalization.MissingAssetMonthlyAmount
        MissingAssetsPreview = $normalization.MissingAssetsPreview
        MissingAssets = @($normalization.MissingAssets)
        Applied = $applied
        PushedRevision = $pushRevision
    }) | Out-Null
}

if ($ExpectedCandidateCount -ge 0 -and $rows.Count -ne $ExpectedCandidateCount) {
    throw "candidate count mismatch. expected=$ExpectedCandidateCount actual=$($rows.Count)"
}

if ($Apply) {
    Assert-ApprovalPlanMatches -Path $ApprovalPlanPath -CurrentBaseUrl $BaseUrl -Rows @($rows.ToArray())

    foreach ($application in @($pendingApplications.ToArray())) {
        $profile = $application.Profile
        $profileId = Get-StringValue $profile.id
        $profile.monthlyAmount = [decimal]$application.MonthlyAmount
        $profile.billingTemplateJson = [string]$application.TemplateJson
        $profile.expectedRevision = [long]$profile.revision
        $profile.mutationId = ([guid]::NewGuid()).ToString()
        $profile.mutationCreatedAtUtc = [DateTime]::UtcNow.ToString('o')
        Invoke-SyncPushRentalProfile -Profile $profile -Headers $headers | Out-Null

        $row = @($rows | Where-Object { [string]::Equals((Get-StringValue $_.ProfileId), $profileId, [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)
        if ($row.Count -gt 0) {
            $row[0].Applied = $true
            $row[0].PushedRevision = [long]$profile.revision
        }
    }
}

$jsonPath = Join-Path $EvidenceDirectory "rental-monthly-repair-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "rental-monthly-repair-$timestamp.md"
$report = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    Apply = [bool]$Apply
    ApprovalPlanPath = $ApprovalPlanPath
    BackupConfirmed = [bool]$BackupConfirmed
    RestorePossible = [bool]$RestorePossible
    ApprovedBy = $ApprovedBy
    CandidateCount = $rows.Count
    Rows = @($rows.ToArray())
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# rental monthly repair report') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- generatedAt: $($report.GeneratedAt)") | Out-Null
$lines.Add("- baseUrl: $BaseUrl") | Out-Null
$lines.Add("- apply: $([bool]$Apply)") | Out-Null
$lines.Add("- approvalPlanPath: $ApprovalPlanPath") | Out-Null
$lines.Add("- applyAllAllowed: $([bool]$AllowApplyAll)") | Out-Null
$lines.Add("- backupConfirmed: $([bool]$BackupConfirmed)") | Out-Null
$lines.Add("- restorePossible: $([bool]$RestorePossible)") | Out-Null
$lines.Add("- approvedBy: $ApprovedBy") | Out-Null
$lines.Add("- failOnCandidates: $([bool]$FailOnCandidates)") | Out-Null
$lines.Add("- candidateCount: $($rows.Count)") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| applied | customer | item | mode | currentMonthly | linkedAssetMonthly | difference | assetCount | missingAssets | missingMonthly | templateChanged | profileId |') | Out-Null
$lines.Add('|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|') | Out-Null
foreach ($row in $rows) {
    $customer = ([string]$row.CustomerName).Replace('|','\|')
    $item = ([string]$row.ItemName).Replace('|','\|')
    $mode = ([string]$row.BillingType).Replace('|','\|')
    $lines.Add("| $($row.Applied) | $customer | $item | $mode | $($row.CurrentMonthlyAmount.ToString('N0')) | $($row.LinkedAssetMonthlyAmount.ToString('N0')) | $($row.Difference.ToString('N0')) | $($row.LinkedAssetCount) | $($row.MissingAssetCount) | $($row.MissingAssetMonthlyAmount.ToString('N0')) | $($row.TemplateChanged) | $($row.ProfileId) |") | Out-Null
    if ($row.MissingAssetCount -gt 0) {
        $missingPreview = ([string]$row.MissingAssetsPreview).Replace('|','\|')
        $lines.Add("  - missing linked assets: $missingPreview") | Out-Null
    }
}
$lines.Add('') | Out-Null
if (-not $Apply -and $rows.Count -gt 0) {
    $profileArgs = (@($rows | ForEach-Object { "'$($_.ProfileId)'" }) -join ',')
    $lines.Add('## 안전 적용 예시') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('운영 데이터 적용 전에는 최신 백업과 복구 가능 여부를 먼저 확인한 뒤, 리포트를 생성한 회사/계정과 같은 계정으로 대상 프로필을 명시해서 실행하세요.') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('```powershell') | Out-Null
    $lines.Add('$env:GEORAEPLAN_RENTAL_REPAIR_USERNAME = ''<사용자명>''') | Out-Null
    $lines.Add('$env:GEORAEPLAN_RENTAL_REPAIR_PASSWORD = ''<비밀번호>''') | Out-Null
    $lines.Add("& 'D:\거래플랜\tools\maintenance\Invoke-GeoraePlanRentalMonthlyRepair.ps1' ``") | Out-Null
    $lines.Add("  -BaseUrl '$BaseUrl' ``") | Out-Null
    $lines.Add('  -Username $env:GEORAEPLAN_RENTAL_REPAIR_USERNAME `') | Out-Null
    $lines.Add('  -Password $env:GEORAEPLAN_RENTAL_REPAIR_PASSWORD `') | Out-Null
    $lines.Add("  -ProfileIds @($profileArgs) ``") | Out-Null
    $lines.Add("  -ExpectedCandidateCount $($rows.Count) ``") | Out-Null
    $lines.Add("  -ApprovalPlanPath '$jsonPath' ``") | Out-Null
    $lines.Add("  -BackupConfirmed -RestorePossible -ApprovedBy '<승인자>' ``") | Out-Null
    $lines.Add('  -Apply') | Out-Null
    $lines.Add('```') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('적용 후에는 같은 명령에서 `-Apply`를 빼고 `-ExpectedCandidateCount 0`으로 재검증하세요.') | Out-Null
    $lines.Add('') | Out-Null
}
$lines.Add("JSON: $jsonPath") | Out-Null
Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "rental_monthly_repair_report=$mdPath"
Write-Host "rental_monthly_repair_json=$jsonPath"
Write-Host "candidate_count=$($rows.Count)"
Write-Host "apply=$([bool]$Apply)"

if ($FailOnCandidates -and -not $Apply -and $rows.Count -gt 0) {
    throw "rental monthly repair candidates remain. count=$($rows.Count). report=$mdPath"
}
