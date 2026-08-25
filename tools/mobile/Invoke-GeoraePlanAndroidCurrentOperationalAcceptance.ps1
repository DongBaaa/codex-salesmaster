[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$ActionContractPath,
    [Parameter(Mandatory = $true)][string]$StateContractPath,
    [Parameter(Mandatory = $true)][string]$ExecutionPlanPath,
    [string]$AuditRoot = 'D:\DevCaches\temp\georaeplan-window-lifecycle-audit',
    [string]$HistoricalAuditRoot =
        'D:\DevCaches\temp\georaeplan-v6-recovery-local\nested',
    [string]$DotnetPath = 'D:\.dotnet-sdk\dotnet.exe',
    [string]$AdbPath = '',
    [string]$DeviceId = '',
    [string]$UiMatrixApkPath = '',
    [string]$RestrictedApkPath = '',
    [string]$ResultRoot = '',
    [switch]$SkipUiMatrixBuild,
    [switch]$ValidateOnly,
    [switch]$ExecuteLocalEmulatorAcceptance
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$uiMatrixPackageName = 'kr.georaeplan.mobile.uimatrix'
$productPackageName = 'kr.georaeplan.mobile'
$expectedMatrixSafeSha256 =
    '10070EEC9CBC79FAC59F8438AF7DF7561D82E7D147EBC68CB8DB8A7DC02FE2D8'
$expectedRestrictedCurrentSha256 =
    'E879A83678133A96946FAAB84056235BE1EDECC6382D9C42323D86A4715EDDFC'
$expectedActionContractSha256 =
    'F11CA04D63DD8195F62E5DDF6560EDDE9B88914F6755ECAB6C2FF4B665171135'
$expectedStateContractSha256 =
    'B9B12048ADEA6F70A29C9FAA0DD039596A1095A03A268CAB7D3C88F922209736'
$expectedExecutionPlanSha256 =
    '5E393E292A39D573B9DCE6C84BCDEA60B8090226FA12B5457D2E7A5C3DCB17BE'
$expectedSuccessEvidenceValidatorSha256 =
    'C30204BCAD423EC868F8B3C41CC115EB369E5EAACBA9F6646EED7D6CD6AFCD76'

function Resolve-RegularLeaf {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$ExpectedSha256 = ''
    )

    if (-not [IO.Path]::IsPathRooted($Path)) {
        throw "$Label path must be absolute."
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($fullPath)) {
        throw "$Label is missing."
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Label must be a regular non-reparse file."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -cne
            $ExpectedSha256) {
        throw "$Label SHA-256 changed."
    }
    return $fullPath
}

function Resolve-RegularDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [IO.Path]::IsPathRooted($Path)) {
        throw "$Label path must be absolute."
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Directory]::Exists($fullPath)) {
        throw "$Label is missing."
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Label must be a regular non-reparse directory."
    }
    return $fullPath
}

function ConvertFrom-ExactTerminalJsonOutput {
    param(
        [Parameter(Mandatory = $true)][object[]]$Output,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Output.Count -lt 1) {
        throw "$Label did not return a result."
    }
    $terminal = [string]$Output[$Output.Count - 1]
    if ([string]::IsNullOrWhiteSpace($terminal)) {
        throw "$Label returned an empty terminal result."
    }
    try {
        $parsed = $terminal | ConvertFrom-Json
    }
    catch {
        throw "$Label terminal result is not JSON."
    }
    if ($null -eq $parsed -or $parsed -is [Array]) {
        throw "$Label terminal result must be one JSON object."
    }
    return $parsed
}

function Resolve-AdbExecutable {
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
    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if ([IO.File]::Exists($candidate)) {
            return Resolve-RegularLeaf `
                -Path ([IO.Path]::GetFullPath($candidate)) `
                -Label 'adb.exe'
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
    $text = (($output | Out-String).Trim())
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "adb failed. exit=$exitCode output=$text"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $text }
}

function Resolve-ExactDeviceId {
    param([string]$RequestedDeviceId)

    $devices = @(
        ((Invoke-Adb -Arguments @('devices')).Output -split "`r?`n") |
            ForEach-Object {
                if ($_ -match '^([^\s]+)\s+device$') { $matches[1] }
            })
    if (-not [string]::IsNullOrWhiteSpace($RequestedDeviceId)) {
        $match = @($devices | Where-Object { $_ -ceq $RequestedDeviceId })
        if ($match.Count -ne 1) {
            throw 'The requested Android device is not exactly one ready device.'
        }
        return $RequestedDeviceId
    }
    if ($devices.Count -ne 1) {
        throw "Exactly one ready Android device is required. actual=$($devices.Count)"
    }
    return [string]$devices[0]
}

function Test-PackageInstalled {
    param([Parameter(Mandatory = $true)][string]$PackageName)

    $probe = Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'pm', 'path', $PackageName) `
        -AllowFailure
    return ($probe.ExitCode -eq 0 -or $probe.Output -match '(?m)^package:')
}

function Assert-CleanEmulator {
    $qemu = (Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'getprop', 'ro.kernel.qemu')).Output.Trim()
    if ($qemu -cne '1') {
        throw 'The current Android operational acceptance is emulator-only.'
    }
    foreach ($packageName in @($uiMatrixPackageName, $productPackageName)) {
        if (Test-PackageInstalled -PackageName $packageName) {
            throw "The Android package already exists before acceptance: $packageName"
        }
    }
}

function Get-Setting {
    param([string]$Namespace, [string]$Name)

    return (Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'settings', 'get', $Namespace, $Name)).Output.Trim()
}

function Get-DeviceSettingsSnapshot {
    return [pscustomobject][ordered]@{
        Size = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'size')).Output.Trim()
        Density = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'density')).Output.Trim()
        FontScale = Get-Setting 'system' 'font_scale'
        AccelerometerRotation = Get-Setting 'system' 'accelerometer_rotation'
        UserRotation = Get-Setting 'system' 'user_rotation'
        ShowIme = Get-Setting 'secure' 'show_ime_with_hard_keyboard'
    }
}

function Get-OverrideValue {
    param([string]$Text)

    $match = [regex]::Match($Text, '(?m)^Override [^:]+:\s*(.+)$')
    if ($match.Success) { return $match.Groups[1].Value.Trim() }
    return ''
}

function Restore-Setting {
    param([string]$Namespace, [string]$Name, [string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -ceq 'null') {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'settings', 'delete', $Namespace, $Name) |
            Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'settings', 'put', $Namespace, $Name, $Value) |
            Out-Null
    }
}

function Restore-DeviceSettings {
    param([Parameter(Mandatory = $true)]$Snapshot)

    $sizeOverride = Get-OverrideValue ([string]$Snapshot.Size)
    if ([string]::IsNullOrWhiteSpace($sizeOverride)) {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'size', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'size', $sizeOverride) | Out-Null
    }
    $densityOverride = Get-OverrideValue ([string]$Snapshot.Density)
    if ([string]::IsNullOrWhiteSpace($densityOverride)) {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'density', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'wm', 'density', $densityOverride) | Out-Null
    }
    Restore-Setting 'system' 'font_scale' ([string]$Snapshot.FontScale)
    Restore-Setting 'system' 'accelerometer_rotation' ([string]$Snapshot.AccelerometerRotation)
    Restore-Setting 'system' 'user_rotation' ([string]$Snapshot.UserRotation)
    Restore-Setting 'secure' 'show_ime_with_hard_keyboard' ([string]$Snapshot.ShowIme)
}

function Assert-DeviceSettingsEqual {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    foreach ($name in @(
        'Size','Density','FontScale','AccelerometerRotation','UserRotation','ShowIme')) {
        if ([string]$Before.$name -cne [string]$After.$name) {
            throw "Android device setting was not restored: $name"
        }
    }
}

function Remove-PackageIfPresent {
    param([Parameter(Mandatory = $true)][string]$PackageName)

    $packageName = $PackageName
    if (Test-PackageInstalled -PackageName $packageName) {
        $uninstall = Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'uninstall', $packageName) -AllowFailure
        if ($uninstall.ExitCode -ne 0 -or
            (Test-PackageInstalled -PackageName $packageName)) {
            throw "The Android package could not be removed: $packageName"
        }
    }
}

function New-ResultDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.Path]::IsPathRooted($Path)) {
        throw 'ResultRoot must be absolute.'
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.Directory]::Exists($fullPath) -or [IO.File]::Exists($fullPath)) {
        throw 'ResultRoot must not already exist.'
    }
    $parent = Resolve-RegularDirectory `
        -Path ([IO.Path]::GetDirectoryName($fullPath)) `
        -Label 'ResultRoot parent'
    $parentPrefix = $parent.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ResultRoot escaped its parent.'
    }
    [IO.Directory]::CreateDirectory($fullPath) | Out-Null
    return Resolve-RegularDirectory -Path $fullPath -Label 'ResultRoot'
}

$project = Resolve-RegularDirectory -Path $ProjectRoot -Label 'ProjectRoot'
$audit = Resolve-RegularDirectory -Path $AuditRoot -Label 'AuditRoot'
$historical = Resolve-RegularDirectory `
    -Path $HistoricalAuditRoot `
    -Label 'HistoricalAuditRoot'
$matrixSafe = Resolve-RegularLeaf `
    -Path (Join-Path $project 'tools\mobile\Invoke-GeoraePlanAndroidUiMatrixSafeAcceptance.ps1') `
    -Label 'current Android UI-matrix safe acceptance' `
    -ExpectedSha256 $expectedMatrixSafeSha256
$restrictedCurrent = Resolve-RegularLeaf `
    -Path (Join-Path $project 'tools\mobile\Invoke-GeoraePlanRestrictedScopeAndroidCurrentAcceptance.ps1') `
    -Label 'current restricted Android acceptance' `
    -ExpectedSha256 $expectedRestrictedCurrentSha256
$actionContract = Resolve-RegularLeaf `
    -Path $ActionContractPath `
    -Label 'current Android action contract' `
    -ExpectedSha256 $expectedActionContractSha256
$stateContract = Resolve-RegularLeaf `
    -Path $StateContractPath `
    -Label 'current Android state contract' `
    -ExpectedSha256 $expectedStateContractSha256
$executionPlan = Resolve-RegularLeaf `
    -Path $ExecutionPlanPath `
    -Label 'current Android execution plan' `
    -ExpectedSha256 $expectedExecutionPlanSha256
$successEvidenceValidator = Resolve-RegularLeaf `
    -Path (Join-Path $historical 'Assert-GeoraePlanAndroidSuccessEvidence.ps1') `
    -Label 'Android success evidence validator' `
    -ExpectedSha256 $expectedSuccessEvidenceValidatorSha256

if ($ValidateOnly) {
    $matrixOutput = @(& $matrixSafe `
        -ProjectRoot $project `
        -ActionContractPath $actionContract `
        -ExecutionPlanPath $executionPlan `
        -AuditRoot $audit `
        -ValidateOnly)
    $matrix = ConvertFrom-ExactTerminalJsonOutput `
        -Output $matrixOutput `
        -Label 'current Android UI-matrix ValidateOnly'
    if ([string]$matrix.Result -cne 'PASS' -or
        [string]$matrix.Mode -cne 'PATCHED_SOURCE' -or
        [string]$matrix.ContractValidation -cne 'PASS' -or
        -not [bool]$matrix.EmulatorOnly -or
        -not [bool]$matrix.DeviceSettingsRestored -or
        -not [bool]$matrix.PackageRemoved) {
        throw 'The current Android UI-matrix ValidateOnly contract did not pass.'
    }

    $restrictedOutput = @(& $restrictedCurrent `
        -ProjectRoot $project `
        -StateContractPath $stateContract `
        -HistoricalAuditRoot $historical `
        -DotnetPath $DotnetPath `
        -ValidateOnly)
    $restricted = ConvertFrom-ExactTerminalJsonOutput `
        -Output $restrictedOutput `
        -Label 'current restricted Android ValidateOnly'
    if ([string]$restricted.Result -cne 'PASS' -or
        [string]$restricted.Mode -cne 'CURRENT_SOURCE_VALIDATE_ONLY' -or
        [int]$restricted.PageMeasurementCount -ne 1080 -or
        [int]$restricted.ShellMeasurementCount -ne 120 -or
        [int]$restricted.AssignedPermissionCount -ne 8 -or
        [int]$restricted.MobileRelevantAssignedPermissionCount -ne 7 -or
        [int]$restricted.DesktopOnlyAssignedPermissionCount -ne 1 -or
        [int]$restricted.DeniedPermissionCount -ne 5 -or
        [int]$restricted.PermissionBindingFileCount -ne 15 -or
        [int]$restricted.PermissionBindingTokenCount -ne 81 -or
        [bool]$restricted.ActualEmulatorUsed -or
        [bool]$restricted.ActualPackageActionExecuted -or
        -not [bool]$restricted.RealAndroidUiStillRequired -or
        [bool]$restricted.LiveDataUsed) {
        throw 'The current restricted Android ValidateOnly contract did not pass.'
    }

    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'CURRENT_SOURCE_VALIDATE_ONLY'
        PageCount = 18
        ActionCount = 117
        StateCount = 33
        KeyboardStateCount = 24
        PageMeasurementCount = 1080
        PageSuccessScreenshotCount = 18
        ShellScenarioCount = 24
        ShellTabCount = 5
        ShellMeasurementCount = 120
        RestrictedAssignedPermissionCount = 8
        RestrictedMobilePermissionCount = 7
        RestrictedDesktopOnlyPermissionCount = 1
        RestrictedDeniedPermissionCount = 5
        RestrictedBindingFileCount = 15
        RestrictedBindingTokenCount = 81
        RestrictedScreenshotCount = 2
        ActualEmulatorUsed = $false
        ActualPackageActionExecuted = $false
        RealAndroidUiStillRequired = $true
        LiveDataUsed = $false
    } | ConvertTo-Json -Compress
    return
}

if (-not $ExecuteLocalEmulatorAcceptance) {
    throw 'ExecuteLocalEmulatorAcceptance is required for the reversible local Android run.'
}
if ([string]::IsNullOrWhiteSpace($RestrictedApkPath)) {
    throw 'RestrictedApkPath is required for the actual local Android run.'
}
if ($SkipUiMatrixBuild -and [string]::IsNullOrWhiteSpace($UiMatrixApkPath)) {
    throw 'UiMatrixApkPath is required when SkipUiMatrixBuild is used.'
}
$restrictedApk = Resolve-RegularLeaf `
    -Path $RestrictedApkPath `
    -Label 'restricted Android APK'
$uiMatrixApk = if ([string]::IsNullOrWhiteSpace($UiMatrixApkPath)) {
    ''
}
else {
    Resolve-RegularLeaf -Path $UiMatrixApkPath -Label 'UI-matrix APK'
}
if ([string]::IsNullOrWhiteSpace($ResultRoot)) {
    $ResultRoot = Join-Path `
        'D:\DevCaches\georaeplan-v1-test-runs' `
        ('android-current-operational-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$result = New-ResultDirectory -Path $ResultRoot
$matrixEvidence = Join-Path $result 'android-ui-matrix'
$restrictedEvidence = Join-Path $result 'restricted-android'

$script:ResolvedAdb = Resolve-AdbExecutable -RequestedPath $AdbPath
Invoke-Adb -Arguments @('start-server') | Out-Null
$script:ResolvedDevice = Resolve-ExactDeviceId -RequestedDeviceId $DeviceId
Assert-CleanEmulator
$settingsBefore = Get-DeviceSettingsSnapshot
$primaryError = $null
$cleanupErrors = New-Object 'System.Collections.Generic.List[string]'
$matrix = $null
$successEvidence = $null
$restricted = $null
try {
    $matrixParameters = @{
        ProjectRoot = $project
        ActionContractPath = $actionContract
        ExecutionPlanPath = $executionPlan
        AuditRoot = $audit
        AdbPath = $script:ResolvedAdb
        DeviceId = $script:ResolvedDevice
        EvidenceDirectory = $matrixEvidence
    }
    if (-not [string]::IsNullOrWhiteSpace($uiMatrixApk)) {
        $matrixParameters.ApkPath = $uiMatrixApk
    }
    if ($SkipUiMatrixBuild) {
        $matrixParameters.SkipBuild = $true
    }
    $matrixOutput = @(& $matrixSafe @matrixParameters)
    $matrix = ConvertFrom-ExactTerminalJsonOutput `
        -Output $matrixOutput `
        -Label 'current Android actual UI-matrix acceptance'
    if ([string]$matrix.Result -cne 'PASS' -or
        -not [bool]$matrix.EmulatorOnly -or
        -not [bool]$matrix.PackageAbsentAfterRun -or
        -not [bool]$matrix.DeviceSettingsRestored -or
        [int]$matrix.SuccessScreenshotCount -ne 18) {
        throw 'The current Android actual UI-matrix acceptance failed.'
    }
    $successEvidence = & $successEvidenceValidator `
        -EvidenceDirectory $matrixEvidence | ConvertFrom-Json
    if ([string]$successEvidence.Result -cne 'PASS' -or
        [int]$successEvidence.PageCount -ne 18 -or
        [int]$successEvidence.MeasurementCount -ne 1080 -or
        [int]$successEvidence.ScreenshotCount -ne 18 -or
        [string]$successEvidence.ScreenshotAggregateSha256 -cnotmatch '^[A-F0-9]{64}$') {
        throw 'The current Android exact 18-page evidence validation failed.'
    }

    $restrictedParameters = @{
        ProjectRoot = $project
        StateContractPath = $stateContract
        HistoricalAuditRoot = $historical
        DotnetPath = $DotnetPath
        AdbPath = $script:ResolvedAdb
        ApkPath = $restrictedApk
        ResultRoot = $restrictedEvidence
        ExecuteLocalEmulatorAcceptance = $true
    }
    $restrictedOutput = @(& $restrictedCurrent @restrictedParameters)
    $restricted = ConvertFrom-ExactTerminalJsonOutput `
        -Output $restrictedOutput `
        -Label 'current restricted Android actual acceptance'
    if ([string]$restricted.Result -cne 'PASS' -or
        [string]$restricted.Mode -cne 'CURRENT_SOURCE_ACTUAL_ISOLATED_EMULATOR' -or
        [string]$restricted.PermissionRevision -cne 'PASS' -or
        [string]$restricted.TokenRefresh -cne 'PASS' -or
        [string]$restricted.OfflineReturn -cne 'PASS' -or
        [string]$restricted.IsolatedAdminShell -cne 'PASS' -or
        [int]$restricted.IsolatedAdminShellScenarioCount -ne 24 -or
        [int]$restricted.IsolatedAdminShellMeasurementCount -ne 120 -or
        [int]$restricted.ScreenshotCount -ne 2 -or
        [bool]$restricted.LiveDataUsed) {
        throw 'The current restricted Android actual acceptance failed.'
    }
    $restrictedSummary = Resolve-RegularLeaf `
        -Path ([string]$restricted.SummaryPath) `
        -Label 'restricted Android summary' `
        -ExpectedSha256 ([string]$restricted.SummarySha256)
}
catch {
    $primaryError = $_.Exception
}
finally {
    foreach ($packageName in @($uiMatrixPackageName, $productPackageName)) {
        try {
            Remove-PackageIfPresent -PackageName $packageName
        }
        catch {
            $cleanupErrors.Add("package $packageName`: $($_.Exception.Message)")
        }
    }
    try {
        $settingsAfter = Get-DeviceSettingsSnapshot
        try {
            Assert-DeviceSettingsEqual -Before $settingsBefore -After $settingsAfter
        }
        catch {
            Restore-DeviceSettings -Snapshot $settingsBefore
            $settingsAfterRestore = Get-DeviceSettingsSnapshot
            Assert-DeviceSettingsEqual -Before $settingsBefore -After $settingsAfterRestore
        }
    }
    catch {
        $cleanupErrors.Add("settings: $($_.Exception.Message)")
    }
}

if ($null -ne $primaryError) {
    if ($cleanupErrors.Count -ne 0) {
        throw [InvalidOperationException]::new(
            ($primaryError.Message + ' Cleanup also failed: ' +
             [string]::Join(' | ', @($cleanupErrors))),
            $primaryError)
    }
    throw $primaryError
}
if ($cleanupErrors.Count -ne 0) {
    throw ('Android operational cleanup failed: ' +
        [string]::Join(' | ', @($cleanupErrors)))
}
foreach ($packageName in @($uiMatrixPackageName, $productPackageName)) {
    if (Test-PackageInstalled -PackageName $packageName) {
        throw "Android package remains after the outer cleanup boundary: $packageName"
    }
}
$settingsFinal = Get-DeviceSettingsSnapshot
Assert-DeviceSettingsEqual -Before $settingsBefore -After $settingsFinal

$summary = [pscustomobject][ordered]@{
    SchemaVersion = 1
    Result = 'PASS'
    Mode = 'CURRENT_SOURCE_ACTUAL_ISOLATED_EMULATOR'
    CompletedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    DeviceId = $script:ResolvedDevice
    PageCount = 18
    ActionCount = 117
    StateCount = 33
    KeyboardStateCount = 24
    PageMeasurementCount = 1080
    PageSuccessScreenshotCount = 18
    PageSuccessScreenshotAggregateSha256 =
        [string]$successEvidence.ScreenshotAggregateSha256
    PageResultSha256 = [string]$successEvidence.ResultSha256
    ShellScenarioCount = 24
    ShellTabCount = 5
    ShellMeasurementCount = 120
    RestrictedAssignedPermissionCount = 8
    RestrictedMobilePermissionCount = 7
    RestrictedDesktopOnlyPermissionCount = 1
    RestrictedDeniedPermissionCount = 5
    RestrictedBindingFileCount = 15
    RestrictedBindingTokenCount = 81
    RestrictedScreenshotCount = 2
    RestrictedSummaryPath = [string]$restricted.SummaryPath
    RestrictedSummarySha256 = [string]$restricted.SummarySha256
    PackageAbsentAfterRun = $true
    DeviceSettingsRestored = $true
    ActualEmulatorUsed = $true
    ActualPackageActionExecuted = $true
    RealAndroidUiStillRequired = $false
    LiveDataUsed = $false
}
$summaryPath = Join-Path $result 'android-current-operational-summary.json'
[IO.File]::WriteAllText(
    $summaryPath,
    ($summary | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))
$summary | Add-Member NoteProperty SummaryPath $summaryPath
$summary | Add-Member NoteProperty SummarySha256 (
    (Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash)
$summary | ConvertTo-Json -Compress
