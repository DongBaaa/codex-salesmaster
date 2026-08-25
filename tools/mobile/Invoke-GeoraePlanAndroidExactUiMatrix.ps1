[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [Parameter(Mandatory = $true)][string]$ActionContractPath,
    [Parameter(Mandatory = $true)][string]$ExecutionPlanPath,
    [string]$AdbPath,
    [string]$DeviceId,
    [string]$ApkPath,
    [string]$EvidenceDirectory,
    [switch]$ValidateContractsOnly,
    [switch]$SkipBuild,
    [switch]$KeepInstalled
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageName = 'kr.georaeplan.mobile.uimatrix'
$requestExtra = 'georaeplan.uiMatrix.request'
$resultLeaf = 'ui-matrix-result.json'
$expectedActionContractSha256 = 'F11CA04D63DD8195F62E5DDF6560EDDE9B88914F6755ECAB6C2FF4B665171135'
$expectedExecutionPlanSha256 = '5E393E292A39D573B9DCE6C84BCDEA60B8090226FA12B5457D2E7A5C3DCB17BE'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Resolve-AbsoluteLeaf {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [IO.Path]::IsPathRooted($Path) -or
        -not [IO.File]::Exists($Path)) {
        throw "$Label is missing or not absolute: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Label must be a regular file: $Path"
    }
    return $item.FullName
}
function Resolve-AdbPath {
    param([string]$RequestedPath)

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath)
    }
    foreach ($root in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $candidates.Add((Join-Path $root 'platform-tools\adb.exe'))
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'))
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\platform-tools\adb.exe'))
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([IO.File]::Exists($candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw 'adb.exe was not found.'
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $script:ResolvedAdb @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    $text = ($output -join "`n")
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "adb failed (exit=$exitCode): adb $($Arguments -join ' ')`n$text"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $text }
}

function Invoke-DotnetBuild {
    param([string]$ProjectPath)

    $dotnetCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\dotnet8\dotnet.exe'),
        (Join-Path $ProjectRoot '.dotnet\dotnet.exe')
    )
    $dotnetPath = $dotnetCandidates |
        Where-Object { [IO.File]::Exists($_) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($dotnetPath)) {
        throw 'The dedicated Android dotnet runtime is missing.'
    }

    $arguments = @(
        'build',
        $ProjectPath,
        '-c', 'Debug',
        '--no-restore',
        '-p:GeoraePlanMobileUiMatrix=true',
        '-p:AndroidManifest=Platforms\Android\AndroidManifest.UiMatrix.xml',
        '-p:AndroidPackageFormat=apk'
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $dotnetPath @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        throw "UI matrix APK build failed.`n$($output -join "`n")"
    }
}

function Get-ConnectedDeviceId {
    $devices = (Invoke-Adb -Arguments @('devices')).Output -split "`r?`n" |
        Where-Object { $_ -match '^\S+\s+device$' }
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) {
        $match = @($devices | Where-Object { ($_ -split '\s+')[0] -eq $DeviceId })
        if ($match.Count -ne 1) {
            throw "Requested Android device is not connected: $DeviceId"
        }
        return $DeviceId
    }
    if (@($devices).Count -ne 1) {
        throw "Exactly one Android device is required; actual=$(@($devices).Count)."
    }
    return (@($devices)[0] -split '\s+')[0]
}

function Require-Exact {
    param($Actual, $Expected, [string]$Label)
    if ($Actual -ne $Expected) {
        throw "$Label mismatch. expected=$Expected actual=$Actual"
    }
}

function Get-ExactPropertyNames {
    param($Object)
    return @($Object.PSObject.Properties.Name | Sort-Object)
}

function Assert-ExactProperties {
    param($Object, [string[]]$Expected, [string]$Label)
    $actual = @(Get-ExactPropertyNames -Object $Object)
    $wanted = @($Expected | Sort-Object)
    if (($actual -join "`n") -cne ($wanted -join "`n")) {
        throw "$Label property contract mismatch."
    }
}

function Get-Sha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Convert-LabelExpression {
    param([string]$Expression)
    if ($Expression -match '^"(?<value>.*)"$') {
        return [Text.RegularExpressions.Regex]::Unescape($Matches['value'])
    }
    return ''
}

function Read-Contracts {
    Require-Exact (Get-Sha256 $script:ActionPath) $expectedActionContractSha256 'action contract SHA-256'
    Require-Exact (Get-Sha256 $script:PlanPath) $expectedExecutionPlanSha256 'execution plan SHA-256'
    $action = [IO.File]::ReadAllText($script:ActionPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $plan = [IO.File]::ReadAllText($script:PlanPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    Assert-ExactProperties $action @(
        'SchemaVersion','PageCount','PriorHeuristicActionCount','ExcludedHelperImplementationCount',
        'LogicalActionCount','KindSummaries','StateRequirementSummaries','PageSummaries',
        'ExcludedHelperImplementations','Actions') 'action contract'
    Assert-ExactProperties $plan @(
        'SchemaVersion','StateCount','ActionCoverage','BaseScenarioCount','KeyboardScenarioCount',
        'KeyboardStateCount','MeasurementCount','KeyboardClosedCount','KeyboardOpenCount','Measurements') `
        'execution plan'
    Require-Exact ([int]$action.SchemaVersion) 1 'action schema'
    Require-Exact ([int]$action.PageCount) 18 'page count'
    Require-Exact ([int]$action.LogicalActionCount) 117 'action count'
    Require-Exact ([int]$plan.SchemaVersion) 1 'plan schema'
    Require-Exact ([int]$plan.StateCount) 33 'state count'
    Require-Exact ([int]$plan.ActionCoverage) 117 'plan action coverage'
    Require-Exact ([int]$plan.BaseScenarioCount) 24 'base scenario count'
    Require-Exact ([int]$plan.KeyboardScenarioCount) 12 'keyboard scenario count'
    Require-Exact ([int]$plan.KeyboardStateCount) 24 'keyboard state count'
    Require-Exact ([int]$plan.MeasurementCount) 1080 'measurement count'
    Require-Exact (@($plan.Measurements).Count) 1080 'measurement array count'
    Require-Exact (@($plan.Measurements.MeasurementId | Sort-Object -Unique).Count) 1080 `
        'unique measurement count'

    $actionById = @{}
    $ordinalById = @{}
    foreach ($entry in @($action.Actions)) {
        $id = [string]$entry.StableId
        if ($actionById.ContainsKey($id)) {
            throw "Duplicate action id: $id"
        }
        $actionById[$id] = $entry
    }
    foreach ($group in @($action.Actions | Group-Object Page, Kind)) {
        $ordinal = 0
        foreach ($entry in @($group.Group | Sort-Object Line, Column)) {
            $ordinal++
            $ordinalById[[string]$entry.StableId] = $ordinal
        }
    }
    foreach ($measurement in @($plan.Measurements)) {
        Require-Exact @($measurement.ActionIds).Count ([int]$measurement.ActionCount) `
            "action count $($measurement.MeasurementId)"
        foreach ($id in @($measurement.ActionIds)) {
            if (-not $actionById.ContainsKey([string]$id)) {
                throw "Measurement references an unknown action: $id"
            }
        }
    }
    return [pscustomobject]@{
        Action = $action
        Plan = $plan
        ActionById = $actionById
        OrdinalById = $ordinalById
    }
}

function Get-OverrideValue {
    param([string]$Text, [string]$Pattern)
    $match = [regex]::Match($Text, $Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return $match.Groups['value'].Value }
    return $null
}

function Get-DeviceSettingsSnapshot {
    $size = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','size')).Output
    $density = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','density')).Output
    return [pscustomobject]@{
        SizeOverride = Get-OverrideValue $size 'Override\s+size:\s*(?<value>\d+x\d+)'
        DensityOverride = Get-OverrideValue $density 'Override\s+density:\s*(?<value>\d+)'
        FontScale = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','get','system','font_scale')).Output.Trim()
        AccelerometerRotation = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','get','system','accelerometer_rotation')).Output.Trim()
        UserRotation = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','get','system','user_rotation')).Output.Trim()
        ShowIme = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','get','secure','show_ime_with_hard_keyboard')).Output.Trim()
    }
}

function Restore-Setting {
    param([string]$Namespace, [string]$Name, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq 'null') {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','delete',$Namespace,$Name) | Out-Null
    }
    else {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','put',$Namespace,$Name,$Value) | Out-Null
    }
}

function Restore-DeviceSettings {
    param($Snapshot)
    if ([string]::IsNullOrWhiteSpace([string]$Snapshot.SizeOverride)) {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','size','reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','size',[string]$Snapshot.SizeOverride) | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace([string]$Snapshot.DensityOverride)) {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','density','reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','density',[string]$Snapshot.DensityOverride) | Out-Null
    }
    Restore-Setting 'system' 'font_scale' ([string]$Snapshot.FontScale)
    Restore-Setting 'system' 'accelerometer_rotation' ([string]$Snapshot.AccelerometerRotation)
    Restore-Setting 'system' 'user_rotation' ([string]$Snapshot.UserRotation)
    Restore-Setting 'secure' 'show_ime_with_hard_keyboard' ([string]$Snapshot.ShowIme)
}

function Set-Scenario {
    param($Measurement)
    $naturalWidth = if ([string]$Measurement.Orientation -eq 'landscape') {
        [int]$Measurement.Height
    }
    else { [int]$Measurement.Width }
    $naturalHeight = if ([string]$Measurement.Orientation -eq 'landscape') {
        [int]$Measurement.Width
    }
    else { [int]$Measurement.Height }
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','size',("{0}x{1}" -f $naturalWidth,$naturalHeight)) | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','density','160') | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','put','system','font_scale',([string]$Measurement.FontScale)) | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','put','system','accelerometer_rotation','0') | Out-Null
    $rotation = if ([string]$Measurement.Orientation -eq 'landscape') { '1' } else { '0' }
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','put','system','user_rotation',$rotation) | Out-Null
    if ([string]$Measurement.Keyboard -eq 'open') {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','settings','put','secure','show_ime_with_hard_keyboard','1') | Out-Null
    }

    $expectedSize = "${naturalWidth}x${naturalHeight}"
    $expectedRotation = if ($rotation -eq '1') { 'ROTATION_90' } else { 'ROTATION_0' }
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $sizeState = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','wm','size')).Output
        $rotationState = (Invoke-Adb -Arguments @(
            '-s',$script:ResolvedDevice,'shell','dumpsys','window','displays')).Output
        if ($sizeState -match "Override size:\s*$([regex]::Escape($expectedSize))" -and
            $rotationState -match "mCurrentRotation=$([regex]::Escape($expectedRotation))") {
            Start-Sleep -Milliseconds 350
            return
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Android scenario did not settle: $($Measurement.Scenario)"
}

function Resolve-LauncherActivity {
    $result = Invoke-Adb -Arguments @(
        '-s',$script:ResolvedDevice,'shell','cmd','package','resolve-activity','--brief',
        '-a','android.intent.action.MAIN','-c','android.intent.category.LAUNCHER',$packageName)
    $activity = @($result.Output -split "`r?`n" | Where-Object { $_ -match '^[^/]+/[^\s]+$' } | Select-Object -Last 1)
    if ($activity.Count -ne 1) { throw 'UI matrix launcher activity was not resolved.' }
    return [string]$activity[0]
}

function Assert-NoNetworkPermission {
    $dump = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','dumpsys','package',$packageName)).Output
    foreach ($permission in @('android.permission.INTERNET','android.permission.ACCESS_NETWORK_STATE')) {
        if ($dump -match [regex]::Escape($permission)) {
            throw "UI matrix package retains forbidden network permission: $permission"
        }
    }
}

function New-RequestPayload {
    param($Measurement, $Contracts)
    $actions = @()
    foreach ($id in @($Measurement.ActionIds)) {
        $contract = $Contracts.ActionById[[string]$id]
        $actions += [pscustomobject][ordered]@{
            StableId = [string]$id
            Kind = [string]$contract.Kind
            Label = Convert-LabelExpression ([string]$contract.LabelExpression)
            VisualOrdinal = [int]$Contracts.OrdinalById[[string]$id]
        }
    }
    $request = [pscustomobject][ordered]@{
        SchemaVersion = 1
        MeasurementId = [string]$Measurement.MeasurementId
        Page = [string]$Measurement.Page
        StateRequirement = [string]$Measurement.StateRequirement
        StateOwner = [string]$Measurement.StateOwner
        Scenario = [string]$Measurement.Scenario
        Keyboard = [string]$Measurement.Keyboard
        Actions = $actions
    }
    $json = $request | ConvertTo-Json -Depth 8 -Compress
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
}

function Wait-Result {
    param([string]$MeasurementId)
    $deadline = (Get-Date).AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 250
        $logs = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'logcat','-d','-s','GeoraePlanUiMatrix:I','*:S')).Output
        if ($logs -match "GEORAEPLAN_UI_MATRIX_READY_V1\s+$([regex]::Escape($MeasurementId))\s+$resultLeaf") {
            $read = Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'exec-out','run-as',$packageName,'cat',("cache/$resultLeaf"))
            if ($read.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($read.Output)) {
                $result = $read.Output | ConvertFrom-Json
                if ([string]$result.MeasurementId -eq $MeasurementId) { return $result }
            }
        }
    } while ((Get-Date) -lt $deadline)
    throw "UI matrix result timed out: $MeasurementId"
}

function Get-KeyboardVisible {
    $dump = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','dumpsys','input_method')).Output
    return $dump -match 'mInputShown=true|isInputViewShown=true|mIsInputViewShown=true'
}

function Assert-NoCrashOrAnr {
    param([string]$MeasurementId)
    $log = (Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'logcat','-d')).Output
    $package = [regex]::Escape($packageName)
    if ($log -match "(?s)(FATAL EXCEPTION.*$package|ANR in\s+$package|Process:\s*$package.*FATAL EXCEPTION)") {
        throw "Android crash/ANR was observed: $MeasurementId"
    }
}

function Save-FailureEvidence {
    param($Measurement, [string]$Reason)
    $safe = [string]$Measurement.MeasurementId
    $remotePng = "/sdcard/georaeplan-ui-matrix-$safe.png"
    $remoteXml = "/sdcard/georaeplan-ui-matrix-$safe.xml"
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','screencap','-p',$remotePng) -AllowFailure | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'pull',$remotePng,(Join-Path $script:EvidenceRoot "$safe.png")) -AllowFailure | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','uiautomator','dump',$remoteXml) -AllowFailure | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'pull',$remoteXml,(Join-Path $script:EvidenceRoot "$safe.xml")) -AllowFailure | Out-Null
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','rm','-f',$remotePng,$remoteXml) -AllowFailure | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $script:EvidenceRoot "$safe.failure.txt"),
        $Reason,
        [Text.UTF8Encoding]::new($false))
}

function Read-PngEvidenceMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$ExpectedWidth,
        [Parameter(Mandatory = $true)][int]$ExpectedHeight
    )
    if (-not [IO.File]::Exists($Path)) { throw 'Android success screenshot was not created.' }
    $info = [IO.FileInfo]::new($Path)
    if ($info.Length -lt 33 -or $info.Length -gt 16777216) {
        throw 'Android success screenshot length is outside the accepted range.'
    }
    $bytes = [IO.File]::ReadAllBytes($Path)
    try {
        $signature = @(137,80,78,71,13,10,26,10)
        for ($index = 0; $index -lt $signature.Count; $index++) {
            if ([int]$bytes[$index] -ne [int]$signature[$index]) {
                throw 'Android success screenshot does not have a PNG signature.'
            }
        }
        $chunkType = [Text.Encoding]::ASCII.GetString($bytes,12,4)
        if ($chunkType -cne 'IHDR') { throw 'Android success screenshot has no leading IHDR chunk.' }
        $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor
            ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
        $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor
            ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
        if ($width -ne $ExpectedWidth -or $height -ne $ExpectedHeight) {
            throw 'Android success screenshot dimensions do not match the measured screen.'
        }
        return [pscustomobject][ordered]@{
            Width = $width
            Height = $height
            Length = [long]$info.Length
            Sha256 = Get-Sha256 $Path
        }
    }
    finally { [Array]::Clear($bytes,0,$bytes.Length) }
}

function Save-PageSuccessEvidence {
    param($Measurement)
    $measurementId = [string]$Measurement.MeasurementId
    $page = [string]$Measurement.Page
    if ($measurementId -cnotmatch '^[A-F0-9]{24}$' -or $page -cnotmatch '^[A-Za-z]+Page$') {
        throw 'Android success screenshot identity is invalid.'
    }
    $leaf = ('success-{0}-{1}.png' -f $page,$measurementId)
    $localPng = Join-Path $script:EvidenceRoot $leaf
    $remotePng = "/sdcard/georaeplan-ui-success-$measurementId.png"
    try {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','screencap','-p',$remotePng) | Out-Null
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'pull',$remotePng,$localPng) | Out-Null
        $metadata = Read-PngEvidenceMetadata `
            -Path $localPng `
            -ExpectedWidth ([int]$Measurement.Width) `
            -ExpectedHeight ([int]$Measurement.Height)
        return [pscustomobject][ordered]@{
            Page = $page
            MeasurementId = $measurementId
            Scenario = [string]$Measurement.Scenario
            RelativePath = $leaf
            Width = [int]$metadata.Width
            Height = [int]$metadata.Height
            Length = [long]$metadata.Length
            Sha256 = [string]$metadata.Sha256
        }
    }
    finally {
        Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','rm','-f',$remotePng) -AllowFailure | Out-Null
    }
}

function Write-AtomicJson {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 20
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes,0,$bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        [IO.File]::Move($temporary,$Path)
    }
    finally {
        [Array]::Clear($bytes,0,$bytes.Length)
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

$script:ActionPath = Resolve-AbsoluteLeaf $ActionContractPath 'action contract'
$script:PlanPath = Resolve-AbsoluteLeaf $ExecutionPlanPath 'execution plan'
$contracts = Read-Contracts
if ($ValidateContractsOnly) {
    [pscustomobject][ordered]@{
        Result = 'PASS'
        PageCount = 18
        LogicalActionCount = 117
        StateCount = 33
        MeasurementCount = 1080
        ActionContractSha256 = Get-Sha256 $script:ActionPath
        ExecutionPlanSha256 = Get-Sha256 $script:PlanPath
    } | ConvertTo-Json -Compress
    return
}

$script:ResolvedAdb = Resolve-AdbPath $AdbPath
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}
$project = Join-Path ([IO.Path]::GetFullPath($ProjectRoot)) 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
if (-not [IO.File]::Exists($project)) { throw "Mobile project is missing: $project" }

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = "D:\DevCaches\temp\georaeplan-android-ui-matrix\$timestamp"
}
if (-not [IO.Path]::IsPathRooted($EvidenceDirectory)) {
    throw 'EvidenceDirectory must be absolute.'
}
$script:EvidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if ([IO.Directory]::Exists($script:EvidenceRoot)) {
    throw "EvidenceDirectory already exists: $script:EvidenceRoot"
}
[IO.Directory]::CreateDirectory($script:EvidenceRoot) | Out-Null

if (-not $SkipBuild) { Invoke-DotnetBuild $project }
if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $apk = Get-ChildItem -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($project)) 'bin\Debug\net8.0-android') `
        -Filter '*-Signed.apk' -File -Recurse | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $apk) { throw 'Signed UI matrix APK was not found after build.' }
    $ApkPath = $apk.FullName
}
$resolvedApk = Resolve-AbsoluteLeaf $ApkPath 'UI matrix APK'
$script:ResolvedDevice = $null
$settingsSnapshot = $null
$installed = $false
$results = New-Object 'System.Collections.Generic.List[object]'
$successScreenshots = New-Object 'System.Collections.Generic.List[object]'
$startedAt = [DateTime]::UtcNow

try {
    Invoke-Adb -Arguments @('start-server') | Out-Null
    $script:ResolvedDevice = Get-ConnectedDeviceId
    $settingsSnapshot = Get-DeviceSettingsSnapshot
    Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'install','-r','-t',$resolvedApk) | Out-Null
    $installed = $true
    Assert-NoNetworkPermission
    $launcher = Resolve-LauncherActivity

    $index = 0
    $lastScenario = ''
    $orderedMeasurements = @(
        $contracts.Plan.Measurements |
            Sort-Object Scenario, Page, StateRequirement, StateOwner, MeasurementId
    )
    foreach ($measurement in $orderedMeasurements) {
        $index++
        try {
            if ($lastScenario -cne [string]$measurement.Scenario) {
                Set-Scenario $measurement
                Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','am','force-stop',$packageName) | Out-Null
                $lastScenario = [string]$measurement.Scenario
            }
            Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'logcat','-c') | Out-Null
            Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','run-as',$packageName,'rm','-f',("cache/$resultLeaf")) -AllowFailure | Out-Null
            $encoded = New-RequestPayload $measurement $contracts
            Invoke-Adb -Arguments @(
                '-s',$script:ResolvedDevice,'shell','am','start','-W','-n',$launcher,
                '--es',$requestExtra,$encoded) | Out-Null
            $result = Wait-Result ([string]$measurement.MeasurementId)
            Assert-NoCrashOrAnr ([string]$measurement.MeasurementId)
            $keyboardVisible = Get-KeyboardVisible
            $passed = [bool]$result.Passed
            if ([string]$measurement.Keyboard -eq 'open' -and -not $keyboardVisible) {
                $passed = $false
                $result.Errors += 'android-keyboard-not-visible'
            }
            if ([int]$result.ExpectedActionCount -ne [int]$measurement.ActionCount -or
                [int]$result.ActualActionCount -ne [int]$measurement.ActionCount) {
                $passed = $false
            }
            $results.Add([pscustomobject][ordered]@{
                MeasurementId = [string]$measurement.MeasurementId
                Page = [string]$measurement.Page
                StateRequirement = [string]$measurement.StateRequirement
                Scenario = [string]$measurement.Scenario
                Keyboard = [string]$measurement.Keyboard
                Passed = $passed
                KeyboardVisible = $keyboardVisible
                Result = $result
            })
            if (-not $passed) {
                Save-FailureEvidence $measurement (($result.Errors | ForEach-Object { [string]$_ }) -join "`n")
            }
            elseif (
                [string]$measurement.Scenario -ceq '390x844@font1.0' -and
                [string]$measurement.StateRequirement -ceq 'initial-layout' -and
                [string]$measurement.Keyboard -ceq 'closed'
            ) {
                $successScreenshots.Add((Save-PageSuccessEvidence $measurement))
            }
        }
        catch {
            $message = $_.Exception.Message
            $results.Add([pscustomobject][ordered]@{
                MeasurementId = [string]$measurement.MeasurementId
                Page = [string]$measurement.Page
                StateRequirement = [string]$measurement.StateRequirement
                Scenario = [string]$measurement.Scenario
                Keyboard = [string]$measurement.Keyboard
                Passed = $false
                KeyboardVisible = $false
                Result = [pscustomobject]@{ Passed = $false; Errors = @($message) }
            })
            Save-FailureEvidence $measurement $message
            Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'shell','am','force-stop',$packageName) -AllowFailure | Out-Null
        }
        if (($index % 25) -eq 0 -or $index -eq 1080) {
            Write-Host ("android_ui_matrix progress={0}/1080 failures={1}" -f $index,@($results | Where-Object { -not $_.Passed }).Count)
        }
    }

    $failed = @($results | Where-Object { -not $_.Passed })
    $capturedPages = @($successScreenshots | ForEach-Object { [string]$_.Page } | Sort-Object -Unique)
    $expectedPages = @($contracts.Action.PageSummaries | ForEach-Object { [string]$_.Page } | Sort-Object -Unique)
    if ($successScreenshots.Count -ne 18 -or
        $capturedPages.Count -ne 18 -or
        @(Compare-Object -ReferenceObject $expectedPages -DifferenceObject $capturedPages).Count -ne 0) {
        throw 'Android success screenshot coverage is not the exact 18-page set.'
    }
    $summary = [pscustomobject][ordered]@{
        SchemaVersion = 2
        CreatedAtUtc = [DateTime]::UtcNow.ToString('O')
        StartedAtUtc = $startedAt.ToString('O')
        DeviceId = $script:ResolvedDevice
        PackageName = $packageName
        ApkPath = $resolvedApk
        ApkSha256 = Get-Sha256 $resolvedApk
        ActionContractPath = $script:ActionPath
        ActionContractSha256 = Get-Sha256 $script:ActionPath
        ExecutionPlanPath = $script:PlanPath
        ExecutionPlanSha256 = Get-Sha256 $script:PlanPath
        PageCount = 18
        ActionCount = 117
        StateCount = 33
        MeasurementCount = $results.Count
        PassedCount = @($results | Where-Object Passed).Count
        FailedCount = $failed.Count
        NetworkPermissionCount = 0
        SuccessScreenshotCount = $successScreenshots.Count
        SuccessScreenshots = $successScreenshots.ToArray()
        Results = $results.ToArray()
    }
    $resultPath = Join-Path $script:EvidenceRoot 'android-exact-ui-matrix-result.json'
    Write-AtomicJson $resultPath $summary
    Write-Host ("android_ui_matrix result={0} passed={1} failed={2}" -f $resultPath,$summary.PassedCount,$summary.FailedCount)
    if ($failed.Count -ne 0) { throw "Android exact UI matrix failed: $($failed.Count)/1080" }
}
finally {
    $cleanupErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($null -ne $settingsSnapshot -and -not [string]::IsNullOrWhiteSpace([string]$script:ResolvedDevice)) {
        try { Restore-DeviceSettings $settingsSnapshot }
        catch { $cleanupErrors.Add("device-settings: $($_.Exception.Message)") }
    }
    if ($installed -and -not $KeepInstalled) {
        try {
            $uninstall = Invoke-Adb -Arguments @('-s',$script:ResolvedDevice,'uninstall',$packageName) -AllowFailure
            if ($uninstall.ExitCode -ne 0) {
                $cleanupErrors.Add("package-uninstall: $($uninstall.Output)")
            }
        }
        catch { $cleanupErrors.Add("package-uninstall: $($_.Exception.Message)") }
    }
    if ($cleanupErrors.Count -gt 0) {
        throw "Android UI matrix cleanup failed:`n$($cleanupErrors -join "`n")"
    }
}
