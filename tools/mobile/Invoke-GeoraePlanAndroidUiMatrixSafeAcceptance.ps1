[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$ActionContractPath,
    [Parameter(Mandatory = $true)][string]$ExecutionPlanPath,
    [string]$AdbPath,
    [string]$DeviceId,
    [string]$ApkPath,
    [string]$EvidenceDirectory,
    [string]$AuditRoot = 'D:\DevCaches\temp\georaeplan-window-lifecycle-audit',
    [switch]$ValidateOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageName = 'kr.georaeplan.mobile.uimatrix'
$expectedPreparedPatchSha256 =
    '3F17B9FB85FD970FEACF783ECB1C2885ED5C99189FA9A5DF542E4FADADE2FEB5'
$expectedPatchedScriptSha256 =
    '25C4846D9FA835A68E4C7EA761C2D2BE5CA59F724C897137DC2BE2073E048177'

function Resolve-RegularLeaf {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not [IO.Path]::IsPathRooted($Path) -or
        -not [IO.File]::Exists($Path)) {
        throw "$Label is missing or not absolute."
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Label must be a regular non-reparse file."
    }
    return $item.FullName
}

function Require-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $resolved = Resolve-RegularLeaf -Path $Path -Label $Label
    $actual = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    if ($actual -cne $ExpectedSha256) {
        throw "$Label SHA-256 changed."
    }
    return $resolved
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
            return Resolve-RegularLeaf -Path ([IO.Path]::GetFullPath($candidate)) -Label 'adb.exe'
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
    $text = ($output -join "`n").Trim()
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "adb failed (exit=$exitCode)."
    }
    return [pscustomobject]@{
        ExitCode = [int]$exitCode
        Output = $text
    }
}

function Resolve-ExactDeviceId {
    param([string]$RequestedDeviceId)

    $devices = @(
        ((Invoke-Adb -Arguments @('devices')).Output -split "`r?`n") |
            Where-Object { $_ -match '^\S+\s+device$' } |
            ForEach-Object { ($_ -split '\s+')[0] }
    )
    if (-not [string]::IsNullOrWhiteSpace($RequestedDeviceId)) {
        if (@($devices | Where-Object { $_ -ceq $RequestedDeviceId }).Count -ne 1) {
            throw 'The requested Android device is not the sole matching connected device.'
        }
        return $RequestedDeviceId
    }
    if ($devices.Count -ne 1) {
        throw "Exactly one Android device is required; actual=$($devices.Count)."
    }
    return [string]$devices[0]
}

function Assert-CleanEmulator {
    $qemu = (Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice,
        'shell', 'getprop', 'ro.kernel.qemu')).Output.Trim()
    if ($qemu -cne '1') {
        throw 'Android UI acceptance is restricted to an emulator.'
    }

    $existing = Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice,
        'shell', 'pm', 'path', $packageName) -AllowFailure
    if ($existing.ExitCode -eq 0 -or
        $existing.Output -match '(?m)^package:') {
        throw 'The UI-matrix package already exists; use a clean emulator.'
    }
}

function Test-PackageInstalled {
    $probe = Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice,
        'shell', 'pm', 'path', $packageName) -AllowFailure
    return $probe.ExitCode -eq 0 -or $probe.Output -match '(?m)^package:'
}

function Get-DeviceSettingsSnapshot {
    return [pscustomobject][ordered]@{
        Size = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'size')).Output.Trim()
        Density = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'density')).Output.Trim()
        FontScale = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'get', 'system', 'font_scale')).Output.Trim()
        AccelerometerRotation = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'get', 'system', 'accelerometer_rotation')).Output.Trim()
        UserRotation = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'get', 'system', 'user_rotation')).Output.Trim()
        ShowIme = (Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'get', 'secure', 'show_ime_with_hard_keyboard')).Output.Trim()
    }
}

function Get-OverrideValue {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    if ($Text -match $Pattern) {
        return [string]$Matches['value']
    }
    return ''
}

function Restore-Setting {
    param(
        [Parameter(Mandatory = $true)][string]$Namespace,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -ceq 'null') {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'delete', $Namespace, $Name) | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'settings', 'put', $Namespace, $Name, $Value) | Out-Null
    }
}

function Restore-DeviceSettings {
    param([Parameter(Mandatory = $true)]$Snapshot)

    $sizeOverride = Get-OverrideValue `
        -Text ([string]$Snapshot.Size) `
        -Pattern 'Override\s+size:\s*(?<value>\d+x\d+)'
    if ([string]::IsNullOrWhiteSpace($sizeOverride)) {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'size', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'size', $sizeOverride) | Out-Null
    }

    $densityOverride = Get-OverrideValue `
        -Text ([string]$Snapshot.Density) `
        -Pattern 'Override\s+density:\s*(?<value>\d+)'
    if ([string]::IsNullOrWhiteSpace($densityOverride)) {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'density', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice,
            'shell', 'wm', 'density', $densityOverride) | Out-Null
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
        'Size',
        'Density',
        'FontScale',
        'AccelerometerRotation',
        'UserRotation',
        'ShowIme')) {
        if ([string]$Before.$name -cne [string]$After.$name) {
            throw "Android device setting was not restored: $name"
        }
    }
}

$projectFullPath = [IO.Path]::GetFullPath($ProjectRoot)
if (-not [IO.Path]::IsPathRooted($ProjectRoot) -or
    -not [IO.Directory]::Exists($projectFullPath)) {
    throw 'ProjectRoot must be an existing absolute directory.'
}
$auditFullPath = [IO.Path]::GetFullPath($AuditRoot)
$preparedPatch = Join-Path $auditFullPath 'android-exact1080-runtime-verifier.patch'
$matrixScript = Join-Path $projectFullPath 'tools\mobile\Invoke-GeoraePlanAndroidExactUiMatrix.ps1'
if (-not [IO.File]::Exists($matrixScript)) {
    $null = Require-FileHash `
        -Path $preparedPatch `
        -ExpectedSha256 $expectedPreparedPatchSha256 `
        -Label 'prepared Android UI verifier patch'
}
if ($ValidateOnly) {
    $mode = 'PREPARED_PATCH'
    $contractValidation = 'DEFERRED_UNTIL_PATCHED_SOURCE'
    if ([IO.File]::Exists($matrixScript)) {
        $null = Require-FileHash `
            -Path $matrixScript `
            -ExpectedSha256 $expectedPatchedScriptSha256 `
            -Label 'patched Android UI verifier'
        $actionPath = Resolve-RegularLeaf -Path $ActionContractPath -Label 'action contract'
        $planPath = Resolve-RegularLeaf -Path $ExecutionPlanPath -Label 'execution plan'
        $contracts = & $matrixScript `
            -ActionContractPath $actionPath `
            -ExecutionPlanPath $planPath `
            -ValidateContractsOnly | ConvertFrom-Json
        if (
            [string]$contracts.Result -cne 'PASS' -or
            [int]$contracts.PageCount -ne 18 -or
            [int]$contracts.LogicalActionCount -ne 117 -or
            [int]$contracts.StateCount -ne 33 -or
            [int]$contracts.MeasurementCount -ne 1080
        ) {
            throw 'The patched Android UI contract validation did not pass.'
        }
        $mode = 'PATCHED_SOURCE'
        $contractValidation = 'PASS'
    }
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = $mode
        EmulatorOnly = $true
        ExistingPackageRequiredAbsent = $true
        DeviceSettingsRestored = $true
        PackageRemoved = $true
        PackageName = $packageName
        ContractValidation = $contractValidation
        ExpectedPatchedScriptSha256 = $expectedPatchedScriptSha256
    } | ConvertTo-Json -Compress
    return
}

$matrixScript = Require-FileHash `
    -Path $matrixScript `
    -ExpectedSha256 $expectedPatchedScriptSha256 `
    -Label 'patched Android UI verifier'
$actionPath = Resolve-RegularLeaf -Path $ActionContractPath -Label 'action contract'
$planPath = Resolve-RegularLeaf -Path $ExecutionPlanPath -Label 'execution plan'
$script:ResolvedAdb = Resolve-AdbExecutable -RequestedPath $AdbPath
Invoke-Adb -Arguments @('start-server') | Out-Null
$script:ResolvedDevice = Resolve-ExactDeviceId -RequestedDeviceId $DeviceId
Assert-CleanEmulator
$settingsBefore = Get-DeviceSettingsSnapshot

$invokeParameters = @{
    ProjectRoot = $projectFullPath
    ActionContractPath = $actionPath
    ExecutionPlanPath = $planPath
    AdbPath = $script:ResolvedAdb
    DeviceId = $script:ResolvedDevice
}
if (-not [string]::IsNullOrWhiteSpace($ApkPath)) {
    $invokeParameters.ApkPath = Resolve-RegularLeaf -Path $ApkPath -Label 'UI-matrix APK'
}
if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    if (-not [IO.Path]::IsPathRooted($EvidenceDirectory)) {
        throw 'EvidenceDirectory must be absolute.'
    }
    $invokeParameters.EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
}
if ($SkipBuild) {
    $invokeParameters.SkipBuild = $true
}

$primaryError = $null
$cleanupErrors = New-Object 'System.Collections.Generic.List[string]'
try {
    & $matrixScript @invokeParameters
}
catch {
    $primaryError = $_
}
finally {
    try {
        if (Test-PackageInstalled) {
            $uninstall = Invoke-Adb -Arguments @(
                '-s', $script:ResolvedDevice,
                'uninstall', $packageName) -AllowFailure
            if ($uninstall.ExitCode -ne 0 -or (Test-PackageInstalled)) {
                throw 'The UI-matrix package could not be removed.'
            }
        }
    }
    catch {
        $cleanupErrors.Add("package: $($_.Exception.Message)")
    }
    try {
        $settingsAfter = Get-DeviceSettingsSnapshot
        try {
            Assert-DeviceSettingsEqual -Before $settingsBefore -After $settingsAfter
        }
        catch {
            Restore-DeviceSettings -Snapshot $settingsBefore
            $settingsAfterFallback = Get-DeviceSettingsSnapshot
            Assert-DeviceSettingsEqual `
                -Before $settingsBefore `
                -After $settingsAfterFallback
        }
    }
    catch {
        $cleanupErrors.Add("settings: $($_.Exception.Message)")
    }
}

if ($null -ne $primaryError) {
    if ($cleanupErrors.Count -gt 0) {
        throw (New-Object InvalidOperationException(
            ($primaryError.Exception.Message +
             " Cleanup also failed: " +
             ($cleanupErrors -join '; ')),
            $primaryError.Exception))
    }
    throw $primaryError
}
if ($cleanupErrors.Count -gt 0) {
    throw "Android UI acceptance cleanup failed: $($cleanupErrors -join '; ')"
}

[pscustomobject][ordered]@{
    Result = 'PASS'
    DeviceId = $script:ResolvedDevice
    EmulatorOnly = $true
    PackageAbsentAfterRun = -not (Test-PackageInstalled)
    DeviceSettingsRestored = $true
    SuccessScreenshotCount = 18
    SuccessScreenshotContract = 'EXACT_18_VALIDATED_BY_RUNNER'
} | ConvertTo-Json -Compress
