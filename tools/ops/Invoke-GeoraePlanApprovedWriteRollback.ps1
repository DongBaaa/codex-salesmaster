param(
    [string]$BaseUrl = 'https://trade.2884.kr',
    [string]$Username = '',
    [string]$Password = '',
    [string]$BearerToken = '',
    [string]$ApprovedTargetsPath = '',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'audit-output\approved-write-rollback'),
    [switch]$GenerateTargets,
    [string]$GeneratedTargetsPath = '',
    [string]$TenantCode = 'USENET_GROUP',
    [string]$OfficeCode = 'USENET',
    [int]$MaxPerType = 1,
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
        [ValidateSet('GET','POST','PUT')][string]$Method,
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

    return @{
        Authorization = "Bearer $script:BearerToken"
        'X-Business-Database' = $OfficeCode
    }
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return $property.Value }
    foreach ($candidate in @($Object.PSObject.Properties)) {
        if ([string]::Equals($candidate.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $candidate.Value
        }
    }
    return $null
}

function Get-StringValue {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    return [string]$Value
}

function Get-ValueHash {
    param([object]$Value)
    $text = Get-StringValue $Value
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
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

function Copy-JsonObject {
    param([object]$InputObject)
    return ($InputObject | ConvertTo-Json -Depth 80 | ConvertFrom-Json)
}

function Get-TargetSpec {
    param([string]$TargetType)

    switch ($TargetType) {
        'customers' { return [pscustomobject]@{ Collection = 'customers'; Route = 'customers'; DefaultField = 'notes'; DisplayField = 'nameOriginal' } }
        'items' { return [pscustomobject]@{ Collection = 'items'; Route = 'items'; DefaultField = 'notes'; DisplayField = 'nameOriginal' } }
        'invoices' { return [pscustomobject]@{ Collection = 'invoices'; Route = 'invoices'; DefaultField = 'memo'; DisplayField = 'customerName' } }
        'payments' { return [pscustomobject]@{ Collection = 'payments'; Route = 'payments'; DefaultField = 'note'; DisplayField = 'paymentDate' } }
        default { throw "지원하지 않는 승인 대상 유형입니다: $TargetType" }
    }
}

function Get-FullPull {
    param([hashtable]$Headers)
    return (Invoke-Api -Method GET -Relative 'sync/pull?sinceRev=0' -Headers $Headers -ExpectedStatus @(200) -TimeoutSec 180).Body
}

function Find-Entity {
    param(
        [object]$Pull,
        [string]$Collection,
        [string]$Id
    )

    $rows = @($Pull.$Collection)
    return $rows | Where-Object { [string](Get-ObjectPropertyValue -Object $_ -Name 'id') -eq $Id } | Select-Object -First 1
}

function New-MutationPayload {
    param(
        [object]$Current,
        [string]$Field,
        [string]$Value
    )

    $payload = Copy-JsonObject $Current
    Set-JsonProperty -Target $payload -Name $Field -Value $Value
    Set-JsonProperty -Target $payload -Name 'expectedRevision' -Value ([long](Get-ObjectPropertyValue -Object $Current -Name 'revision'))
    Set-JsonProperty -Target $payload -Name 'mutationId' -Value (([guid]::NewGuid()).ToString())
    Set-JsonProperty -Target $payload -Name 'mutationCreatedAtUtc' -Value ([DateTime]::UtcNow.ToString('o'))
    return $payload
}

function Get-ApprovedRows {
    param(
        [object]$Targets,
        [string]$TypeName
    )

    $value = Get-ObjectPropertyValue -Object $Targets -Name $TypeName
    return @($value | Where-Object { $null -ne $_ })
}

function New-TargetRow {
    param(
        [object]$Entity,
        [string]$Field,
        [string]$DisplayField
    )

    $id = [string](Get-ObjectPropertyValue -Object $Entity -Name 'id')
    $value = Get-ObjectPropertyValue -Object $Entity -Name $Field
    [pscustomobject]@{
        id = $id
        field = $Field
        originalHash = Get-ValueHash $value
        originalLength = (Get-StringValue $value).Length
        displayHint = (Get-StringValue (Get-ObjectPropertyValue -Object $Entity -Name $DisplayField))
        expectedRevision = [long](Get-ObjectPropertyValue -Object $Entity -Name 'revision')
    }
}

function Select-GenerateCandidates {
    param(
        [object[]]$Rows,
        [string]$Field,
        [string]$DisplayField
    )

    return @($Rows |
        Where-Object {
            (Get-ObjectPropertyValue -Object $_ -Name 'isDeleted') -ne $true -and
            ([string](Get-ObjectPropertyValue -Object $_ -Name 'tenantCode') -eq $TenantCode -or [string]::IsNullOrWhiteSpace([string](Get-ObjectPropertyValue -Object $_ -Name 'tenantCode'))) -and
            ([string](Get-ObjectPropertyValue -Object $_ -Name 'officeCode') -in @($OfficeCode, 'ALL', ''))
        } |
        Where-Object {
            $display = Get-StringValue (Get-ObjectPropertyValue -Object $_ -Name $DisplayField)
            $display -notmatch 'mobilee|repeated-save|반복저장검증|운영쓰기검증'
        } |
        Sort-Object -Property @{ Expression = { [long](Get-ObjectPropertyValue -Object $_ -Name 'revision') }; Descending = $true } |
        Select-Object -First $MaxPerType |
        ForEach-Object { New-TargetRow -Entity $_ -Field $Field -DisplayField $DisplayField })
}

New-DirectoryIfMissing -Path $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$headers = New-AuthHeaders

if ($GenerateTargets) {
    if ([string]::IsNullOrWhiteSpace($GeneratedTargetsPath)) {
        $GeneratedTargetsPath = Join-Path $EvidenceDirectory "approved-write-targets-$timestamp.json"
    }

    $pull = Get-FullPull -Headers $headers
    $targetObject = [pscustomobject]@{
        safety = [pscustomobject]@{
            backupConfirmed = $false
            backupConfirmedAt = ''
            restorePossible = $false
            approvedBy = ''
            notes = '생성된 후보입니다. 운영 적용 전 백업/복구 가능성/승인자를 채운 뒤 실행하세요. 원문 값은 저장하지 않고 hash/length만 저장합니다.'
        }
        customers = Select-GenerateCandidates -Rows @($pull.customers) -Field 'notes' -DisplayField 'nameOriginal'
        items = Select-GenerateCandidates -Rows @($pull.items) -Field 'notes' -DisplayField 'nameOriginal'
        invoices = Select-GenerateCandidates -Rows @($pull.invoices) -Field 'memo' -DisplayField 'customerName'
        payments = Select-GenerateCandidates -Rows @($pull.payments) -Field 'note' -DisplayField 'paymentDate'
    }

    $targetObject | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $GeneratedTargetsPath -Encoding UTF8
    Write-Host "approved_write_targets=$GeneratedTargetsPath"
    Write-Host "generate_only=True"
    if (-not $Apply) { return }
}

if ([string]::IsNullOrWhiteSpace($ApprovedTargetsPath)) {
    throw 'ApprovedTargetsPath가 필요합니다.'
}
if (-not (Test-Path -LiteralPath $ApprovedTargetsPath)) {
    throw "승인 대상 파일을 찾지 못했습니다: $ApprovedTargetsPath"
}

$targets = Get-Content -LiteralPath $ApprovedTargetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$safety = Get-ObjectPropertyValue -Object $targets -Name 'safety'
$backupConfirmed = [bool](Get-ObjectPropertyValue -Object $safety -Name 'backupConfirmed')
$restorePossible = [bool](Get-ObjectPropertyValue -Object $safety -Name 'restorePossible')
$approvedBy = Get-StringValue (Get-ObjectPropertyValue -Object $safety -Name 'approvedBy')
if (-not $backupConfirmed -or -not $restorePossible -or [string]::IsNullOrWhiteSpace($approvedBy)) {
    throw '승인 대상 파일에는 safety.backupConfirmed=true, restorePossible=true, approvedBy 값이 필요합니다.'
}
if (-not $Apply) {
    throw '실행하려면 -Apply를 지정하세요.'
}

$rows = New-Object System.Collections.Generic.List[object]
$targetTypes = @('customers','items','invoices','payments')
$pull = Get-FullPull -Headers $headers

foreach ($typeName in $targetTypes) {
    $spec = Get-TargetSpec -TargetType $typeName
    foreach ($target in Get-ApprovedRows -Targets $targets -TypeName $typeName) {
        $id = Get-StringValue (Get-ObjectPropertyValue -Object $target -Name 'id')
        $field = Get-StringValue (Get-ObjectPropertyValue -Object $target -Name 'field')
        if ([string]::IsNullOrWhiteSpace($field)) { $field = $spec.DefaultField }
        if ($field -ne $spec.DefaultField) {
            throw "$typeName/$id 필드는 $($spec.DefaultField)만 허용됩니다. requested=$field"
        }
        if ([string]::IsNullOrWhiteSpace($id)) {
            throw "$typeName 승인 대상 id가 비어 있습니다."
        }

        $current = Find-Entity -Pull $pull -Collection $spec.Collection -Id $id
        if ($null -eq $current) { throw "$typeName/$id 항목을 sync/pull에서 찾지 못했습니다." }
        if ((Get-ObjectPropertyValue -Object $current -Name 'isDeleted') -eq $true) { throw "$typeName/$id 항목은 삭제 상태입니다." }

        $originalValue = Get-StringValue (Get-ObjectPropertyValue -Object $current -Name $field)
        $originalHash = Get-ValueHash $originalValue
        $expectedHash = Get-StringValue (Get-ObjectPropertyValue -Object $target -Name 'originalHash')
        if (-not [string]::IsNullOrWhiteSpace($expectedHash) -and -not [string]::Equals($expectedHash, $originalHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$typeName/$id 원본 hash가 승인 대상 파일과 다릅니다."
        }

        $probeValue = if ([string]::IsNullOrWhiteSpace($originalValue)) {
            "운영쓰기검증-$timestamp"
        }
        else {
            "$originalValue | 운영쓰기검증-$timestamp"
        }
        if ($probeValue.Length -gt 900) {
            $probeValue = $probeValue.Substring(0, 900)
        }

        $beforeRevision = [long](Get-ObjectPropertyValue -Object $current -Name 'revision')
        $updatePayload = New-MutationPayload -Current $current -Field $field -Value $probeValue
        $updated = (Invoke-Api -Method PUT -Relative "$($spec.Route)/$id" -Headers $headers -Body $updatePayload -ExpectedStatus @(200) -TimeoutSec 120).Body
        $updateRevision = [long](Get-ObjectPropertyValue -Object $updated -Name 'revision')
        if ($updateRevision -le $beforeRevision) {
            throw "$typeName/$id update revision did not advance."
        }

        $verifyPull = Get-FullPull -Headers $headers
        $verifyUpdated = Find-Entity -Pull $verifyPull -Collection $spec.Collection -Id $id
        $verifyUpdatedValue = Get-StringValue (Get-ObjectPropertyValue -Object $verifyUpdated -Name $field)
        if (-not [string]::Equals($verifyUpdatedValue, $probeValue, [System.StringComparison]::Ordinal)) {
            throw "$typeName/$id update value was not visible through sync/pull."
        }

        $restorePayload = New-MutationPayload -Current $verifyUpdated -Field $field -Value $originalValue
        $restored = (Invoke-Api -Method PUT -Relative "$($spec.Route)/$id" -Headers $headers -Body $restorePayload -ExpectedStatus @(200) -TimeoutSec 120).Body
        $restoreRevision = [long](Get-ObjectPropertyValue -Object $restored -Name 'revision')
        if ($restoreRevision -le $updateRevision) {
            throw "$typeName/$id restore revision did not advance."
        }

        $finalPull = Get-FullPull -Headers $headers
        $final = Find-Entity -Pull $finalPull -Collection $spec.Collection -Id $id
        $finalValue = Get-StringValue (Get-ObjectPropertyValue -Object $final -Name $field)
        $finalHash = Get-ValueHash $finalValue
        if (-not [string]::Equals($finalHash, $originalHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$typeName/$id restore hash mismatch."
        }

        $rows.Add([pscustomobject]@{
            Type = $typeName
            Id = $id
            Field = $field
            BeforeRevision = $beforeRevision
            UpdateRevision = $updateRevision
            RestoreRevision = $restoreRevision
            OriginalHash = $originalHash
            FinalHash = $finalHash
            OriginalLength = $originalValue.Length
            Result = 'PASS'
        }) | Out-Null

        $pull = $finalPull
    }
}

$jsonPath = Join-Path $EvidenceDirectory "approved-write-rollback-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "approved-write-rollback-$timestamp.md"
$report = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    ApprovedTargetsPath = $ApprovedTargetsPath
    ApprovedBy = $approvedBy
    RowCount = $rows.Count
    Result = 'PASS'
    Rows = @($rows.ToArray())
}
$report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 운영 승인 대상 쓰기/원복 검증') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add("- 승인 대상: $ApprovedTargetsPath") | Out-Null
$lines.Add("- 승인자: $approvedBy") | Out-Null
$lines.Add("- 결과: **PASS**") | Out-Null
$lines.Add("- JSON: $jsonPath") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| 유형 | ID | 필드 | 원본 Revision | 변경 Revision | 원복 Revision | 원문 길이 | 결과 |') | Out-Null
$lines.Add('|---|---|---|---:|---:|---:|---:|---|') | Out-Null
foreach ($row in $rows) {
    $lines.Add("| $($row.Type) | $($row.Id) | $($row.Field) | $($row.BeforeRevision) | $($row.UpdateRevision) | $($row.RestoreRevision) | $($row.OriginalLength) | $($row.Result) |") | Out-Null
}
Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "approved_write_rollback_report=$mdPath"
Write-Host "approved_write_rollback_json=$jsonPath"
Write-Host "result=PASS"


