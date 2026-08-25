[CmdletBinding()]
param(
    [string]$ProjectRoot = $PWD.Path,
    [Parameter(Mandatory = $true)][string]$StateContractPath,
    [string]$HistoricalAuditRoot =
        'D:\DevCaches\temp\georaeplan-v6-recovery-local\nested',
    [string]$DotnetPath = 'D:\.dotnet-sdk\dotnet.exe',
    [string]$AdbPath = '',
    [string]$ApkPath = '',
    [string]$ResultRoot = '',
    [string]$StagingParent =
        'D:\DevCaches\temp\georaeplan-restricted-android-current-staging',
    [switch]$ValidateOnly,
    [switch]$ExecuteLocalEmulatorAcceptance
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$expectedHistoricalAcceptanceSha256 =
    '990A1E52929ECF993E747AAE8B3646E2D6AF9677FE6B9C3ED451C722E4AA73B3'
$expectedPermissionBindingSha256 =
    '9EB35465E25B73EF6D4326E4F4C6E8869114357788591637C36DF25DA1A6367C'
$expectedShellSafeAcceptanceSha256 =
    '2554961F5150F17D2185D6D5556F5BB88BBA8B98FB5D9DADC5501E8A4A65E226'
$expectedShellExact24Sha256 =
    'AF8CB7DA72887A03A2B95ADB525B273752FB420409312FEDB52B53E8B237C4AB'
$expectedCurrentSmokeSha256 =
    'D6A0E2BD2BA51F996C11D54F4DA375216F93C93540AE8350A3AB7393478E71DB'
$expectedCurrentStateContractSha256 =
    'B9B12048ADEA6F70A29C9FAA0DD039596A1095A03A268CAB7D3C88F922209736'
$historicalSmokeSha256 =
    '31B4071AEF938BF252BDF6B3E262322AE0DCABCE2D4BFA5E26722F14D8A56DE4'
$historicalStateContractSha256 =
    'A05B0E23A0FF0CCC764BA496AC31ED44519B12FE81181D26DA9CE401331609B4'

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

function Replace-ExactOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$OldValue,
        [Parameter(Mandatory = $true)][string]$NewValue,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $first = $Source.IndexOf($OldValue, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "$Label historical value is missing."
    }
    if ($Source.IndexOf(
            $OldValue,
            $first + $OldValue.Length,
            [StringComparison]::Ordinal) -ge 0) {
        throw "$Label historical value is not unique."
    }
    if ($Source.IndexOf($NewValue, [StringComparison]::Ordinal) -ge 0) {
        throw "$Label current value already exists before rebinding."
    }
    return $Source.Remove($first, $OldValue.Length).Insert($first, $NewValue)
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)][string]$Text)

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        $sha256.Dispose()
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or
        [string]::Join("`n", $actual) -cne [string]::Join("`n", $wanted)) {
        throw "$Label properties changed."
    }
}

function ConvertFrom-ExactTerminalJsonOutput {
    param(
        [Parameter(Mandatory = $true)][object[]]$Output,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Output.Count -lt 1) {
        throw "$Label returned no output."
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

function New-PrivateDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([IO.Directory]::Exists($Path) -or [IO.File]::Exists($Path)) {
        throw 'Private directory target already exists.'
    }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $fullControl = [Security.AccessControl.FileSystemRights]::FullControl
    foreach ($sid in @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            $fullControl,
            $inheritance,
            $propagation,
            [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    [IO.Directory]::CreateDirectory($Path, $security) | Out-Null
    return [IO.Path]::GetFullPath($Path)
}

$project = Resolve-RegularDirectory -Path $ProjectRoot -Label 'ProjectRoot'
$historicalRoot = Resolve-RegularDirectory `
    -Path $HistoricalAuditRoot `
    -Label 'HistoricalAuditRoot'
$historicalAcceptance = Resolve-RegularLeaf `
    -Path (Join-Path $historicalRoot 'Invoke-GeoraePlanRestrictedScopeAndroidUiAcceptance.ps1') `
    -Label 'historical restricted Android acceptance' `
    -ExpectedSha256 $expectedHistoricalAcceptanceSha256
$permissionBinding = Resolve-RegularLeaf `
    -Path (Join-Path $historicalRoot 'Assert-GeoraePlanRestrictedScopeAndroidPermissionBindingContract.ps1') `
    -Label 'restricted Android permission-binding contract' `
    -ExpectedSha256 $expectedPermissionBindingSha256
$shellSafe = Resolve-RegularLeaf `
    -Path (Join-Path $historicalRoot 'Invoke-GeoraePlanAndroidShellSafeAcceptance.ps1') `
    -Label 'Android Shell safe acceptance' `
    -ExpectedSha256 $expectedShellSafeAcceptanceSha256
$shellExact = Resolve-RegularLeaf `
    -Path (Join-Path $project 'tools\mobile\Invoke-GeoraePlanAndroidShellExact24.ps1') `
    -Label 'current Android Shell exact120 oracle' `
    -ExpectedSha256 $expectedShellExact24Sha256
$smoke = Resolve-RegularLeaf `
    -Path (Join-Path $project 'tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1') `
    -Label 'current Android smoke runner' `
    -ExpectedSha256 $expectedCurrentSmokeSha256
$stateContract = Resolve-RegularLeaf `
    -Path $StateContractPath `
    -Label 'current Android state contract' `
    -ExpectedSha256 $expectedCurrentStateContractSha256

$bindingResult = & $permissionBinding -RepositoryRoot $project | ConvertFrom-Json
if ([string]$bindingResult.Result -cne 'PASS' -or
    [int]$bindingResult.AssignedPermissionCount -ne 8 -or
    [int]$bindingResult.MobileRelevantAssignedPermissionCount -ne 7 -or
    [int]$bindingResult.DesktopOnlyAssignedPermissionCount -ne 1 -or
    [int]$bindingResult.DeniedPermissionCount -ne 5 -or
    [int]$bindingResult.PermissionBindingFileCount -ne 15 -or
    [int]$bindingResult.PermissionBindingTokenCount -ne 81 -or
    -not [bool]$bindingResult.RealAndroidUiStillRequired -or
    [bool]$bindingResult.LiveDataUsed) {
    throw 'The current restricted Android permission-binding contract did not pass.'
}

$state = Get-Content -LiteralPath $stateContract -Raw -Encoding UTF8 |
    ConvertFrom-Json
Assert-ExactProperties $state @(
    'SchemaVersion','PageCount','ActionCount','InitialStateCount',
    'NonInitialStateCount','StateVariantCount','KeyboardStateCount',
    'BaseScenarioCount','KeyboardScenarioCount','ExactMeasurementCount',
    'StateRequirementSummaries','BaseScenarios','KeyboardScenarios','StateVariants') `
    'current Android state contract'
if ([int]$state.SchemaVersion -ne 1 -or
    [int]$state.PageCount -ne 18 -or
    [int]$state.ActionCount -ne 117 -or
    [int]$state.StateVariantCount -ne 33 -or
    [int]$state.KeyboardStateCount -ne 24 -or
    [int]$state.BaseScenarioCount -ne 24 -or
    [int]$state.KeyboardScenarioCount -ne 12 -or
    [int]$state.ExactMeasurementCount -ne 1080 -or
    @($state.StateVariants).Count -ne 33 -or
    @($state.BaseScenarios).Count -ne 24 -or
    @($state.KeyboardScenarios).Count -ne 12) {
    throw 'The current Android state contract is not exact 18/117/33/24/1080.'
}

$historicalSource = [IO.File]::ReadAllText(
    $historicalAcceptance,
    [Text.UTF8Encoding]::new($true))
$patchedSource = Replace-ExactOnce `
    -Source $historicalSource `
    -OldValue $historicalSmokeSha256 `
    -NewValue $expectedCurrentSmokeSha256 `
    -Label 'Android smoke runner binding'
$patchedSource = Replace-ExactOnce `
    -Source $patchedSource `
    -OldValue $historicalStateContractSha256 `
    -NewValue $expectedCurrentStateContractSha256 `
    -Label 'Android state contract binding'
$patchedAcceptanceSha256 = Get-TextSha256 -Text $patchedSource

if ($ValidateOnly) {
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'CURRENT_SOURCE_VALIDATE_ONLY'
        HistoricalAcceptanceSha256 = $expectedHistoricalAcceptanceSha256
        PatchedAcceptanceSha256 = $patchedAcceptanceSha256
        CurrentSmokeSha256 = $expectedCurrentSmokeSha256
        CurrentStateContractSha256 = $expectedCurrentStateContractSha256
        ShellExact24Sha256 = $expectedShellExact24Sha256
        PageCount = 18
        ActionCount = 117
        StateCount = 33
        KeyboardStateCount = 24
        PageMeasurementCount = 1080
        ShellScenarioCount = 24
        ShellTabCount = 5
        ShellMeasurementCount = 120
        AssignedPermissionCount = 8
        MobileRelevantAssignedPermissionCount = 7
        DesktopOnlyAssignedPermissionCount = 1
        DeniedPermissionCount = 5
        PermissionBindingFileCount = 15
        PermissionBindingTokenCount = 81
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

$parent = [IO.Path]::GetFullPath($StagingParent)
if (-not [IO.Path]::IsPathRooted($StagingParent)) {
    throw 'StagingParent path must be absolute.'
}
if (-not [IO.Directory]::Exists($parent)) {
    [IO.Directory]::CreateDirectory($parent) | Out-Null
}
$parent = Resolve-RegularDirectory -Path $parent -Label 'StagingParent'
$stagingRoot = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
$stagingRoot = New-PrivateDirectory -Path $stagingRoot
$primaryError = $null
$cleanupError = $null
$finalOutput = $null
try {
    $stagedAcceptance = Join-Path $stagingRoot 'Invoke-GeoraePlanRestrictedScopeAndroidUiAcceptance.ps1'
    $stagedBinding = Join-Path $stagingRoot 'Assert-GeoraePlanRestrictedScopeAndroidPermissionBindingContract.ps1'
    $stagedShellSafe = Join-Path $stagingRoot 'Invoke-GeoraePlanAndroidShellSafeAcceptance.ps1'
    $stagedShellExact = Join-Path $stagingRoot 'Invoke-GeoraePlanAndroidShellExact24.ps1'
    $stagedState = Join-Path $stagingRoot 'android-state-contract.json'

    [IO.File]::WriteAllText(
        $stagedAcceptance,
        $patchedSource,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Copy($permissionBinding, $stagedBinding, $false)
    [IO.File]::Copy($shellSafe, $stagedShellSafe, $false)
    [IO.File]::Copy($shellExact, $stagedShellExact, $false)
    [IO.File]::Copy($stateContract, $stagedState, $false)

    if ((Get-FileHash -LiteralPath $stagedAcceptance -Algorithm SHA256).Hash -cne
            $patchedAcceptanceSha256 -or
        (Get-FileHash -LiteralPath $stagedBinding -Algorithm SHA256).Hash -cne
            $expectedPermissionBindingSha256 -or
        (Get-FileHash -LiteralPath $stagedShellSafe -Algorithm SHA256).Hash -cne
            $expectedShellSafeAcceptanceSha256 -or
        (Get-FileHash -LiteralPath $stagedShellExact -Algorithm SHA256).Hash -cne
            $expectedShellExact24Sha256 -or
        (Get-FileHash -LiteralPath $stagedState -Algorithm SHA256).Hash -cne
            $expectedCurrentStateContractSha256) {
        throw 'The current restricted Android staging closure changed.'
    }

    $arguments = @{
        RepositoryRoot = $project
        AuditRoot = $stagingRoot
        DotnetPath = $DotnetPath
        ApkPath = $ApkPath
        ExecuteLocalEmulatorAcceptance = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($AdbPath)) {
        $arguments.AdbPath = $AdbPath
    }
    if (-not [string]::IsNullOrWhiteSpace($ResultRoot)) {
        $arguments.ResultRoot = $ResultRoot
    }
    $output = @(& $stagedAcceptance @arguments)
    $actual = ConvertFrom-ExactTerminalJsonOutput `
        -Output $output `
        -Label 'current restricted Android acceptance'
    if ([string]$actual.Result -cne 'PASS' -or
        [string]$actual.PermissionRevision -cne 'PASS' -or
        [string]$actual.TokenRefresh -cne 'PASS' -or
        [string]$actual.OfflineReturn -cne 'PASS' -or
        [string]$actual.IsolatedAdminShell -cne 'PASS' -or
        [int]$actual.IsolatedAdminShellScenarioCount -ne 24 -or
        [int]$actual.IsolatedAdminShellMeasurementCount -ne 120 -or
        [int]$actual.ScreenshotCount -ne 2 -or
        [bool]$actual.LiveDataUsed) {
        throw 'The current restricted Android acceptance did not satisfy the exact contract.'
    }
    $finalOutput = [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'CURRENT_SOURCE_ACTUAL_ISOLATED_EMULATOR'
        PatchedAcceptanceSha256 = $patchedAcceptanceSha256
        SummaryPath = [string]$actual.SummaryPath
        SummarySha256 = [string]$actual.SummarySha256
        PermissionRevision = 'PASS'
        TokenRefresh = 'PASS'
        OfflineReturn = 'PASS'
        IsolatedAdminShell = 'PASS'
        IsolatedAdminShellScenarioCount = 24
        IsolatedAdminShellMeasurementCount = 120
        ScreenshotCount = 2
        LiveDataUsed = $false
    }
}
catch {
    $primaryError = $_.Exception
}
finally {
    try {
        $resolvedStage = [IO.Path]::GetFullPath($stagingRoot)
        $parentPrefix = $parent.TrimEnd([IO.Path]::DirectorySeparatorChar) +
            [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith(
                $parentPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Restricted Android staging cleanup target escaped its parent.'
        }
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        if ([IO.Directory]::Exists($stagingRoot) -or [IO.File]::Exists($stagingRoot)) {
            throw 'Restricted Android staging cleanup did not remove the target.'
        }
    }
    catch {
        $cleanupError = $_.Exception
    }
}

if ($null -ne $primaryError) {
    if ($null -ne $cleanupError) {
        throw [InvalidOperationException]::new(
            ($primaryError.Message + ' Cleanup also failed: ' + $cleanupError.Message),
            $primaryError)
    }
    throw $primaryError
}
if ($null -ne $cleanupError) {
    throw $cleanupError
}
if ($null -eq $finalOutput) {
    throw 'Current restricted Android acceptance produced no final result.'
}
$finalOutput | ConvertTo-Json -Compress
