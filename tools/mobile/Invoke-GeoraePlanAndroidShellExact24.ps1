[CmdletBinding()]
param(
    [string]$AdbPath = 'C:\Users\beene\AppData\Local\Android\Sdk\platform-tools\adb.exe',
    [string]$DeviceId = '',
    [Parameter(Mandatory = $true)][string]$PackageName,
    [string]$LauncherActivity = '',
    [string]$ContractPath = 'D:\DevCaches\temp\georaeplan-window-lifecycle-audit\android-state-contract.json',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [int]$StartupTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$expectedTabs = @(
    [string][char]0xD648,
    ([string][char]0xAC70 + [string][char]0xB798 + [string][char]0xCC98),
    ([string][char]0xD488 + [string][char]0xBAA9),
    ([string][char]0xC804 + [string][char]0xD45C),
    ([string][char]0xB3D9 + [string][char]0xAE30 + [string][char]0xD654)
)

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $AdbPath @Arguments 2>&1
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

function Resolve-Device {
    $lines = (Invoke-Adb -Arguments @('devices')).Output -split "`r?`n"
    $devices = @($lines | ForEach-Object {
        if ($_ -match '^([^\s]+)\s+device$') { $matches[1] }
    })
    if (-not [string]::IsNullOrWhiteSpace($DeviceId)) {
        $matched = @($devices | Where-Object { $_ -ceq $DeviceId })
        if ($matched.Count -ne 1) {
            throw 'The requested Android device is not exactly one ready device.'
        }
        return $DeviceId
    }
    if ($devices.Count -ne 1) {
        throw "Exactly one ready Android device is required. actual=$($devices.Count)"
    }
    return [string]$devices[0]
}

function Resolve-Launcher {
    if (-not [string]::IsNullOrWhiteSpace($LauncherActivity)) {
        return $LauncherActivity
    }
    $result = Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'cmd', 'package', 'resolve-activity', '--brief',
        '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER', $PackageName)
    $activities = @($result.Output -split "`r?`n" |
        Where-Object { $_ -match '^[^/]+/[^\s]+$' } |
        Select-Object -Last 1)
    if ($activities.Count -ne 1) {
        throw 'The launcher activity could not be resolved exactly.'
    }
    return [string]$activities[0]
}

function Get-Setting {
    param([string]$Namespace, [string]$Name)
    return (Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'settings', 'get', $Namespace, $Name)).Output.Trim()
}

function Restore-Setting {
    param([string]$Namespace, [string]$Name, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq 'null') {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'settings', 'delete', $Namespace, $Name) | Out-Null
    }
    else {
        Invoke-Adb -Arguments @(
            '-s', $script:ResolvedDevice, 'shell', 'settings', 'put', $Namespace, $Name, $Value) | Out-Null
    }
}

function Get-DeviceSnapshot {
    $size = (Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'size')).Output
    $density = (Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'density')).Output
    return [pscustomobject]@{
        SizeOverride = if ($size -match 'Override size:\s*([^\r\n]+)') { $matches[1].Trim() } else { '' }
        DensityOverride = if ($density -match 'Override density:\s*([^\r\n]+)') { $matches[1].Trim() } else { '' }
        FontScale = Get-Setting 'system' 'font_scale'
        AccelerometerRotation = Get-Setting 'system' 'accelerometer_rotation'
        UserRotation = Get-Setting 'system' 'user_rotation'
    }
}

function Restore-DeviceSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    if ([string]::IsNullOrWhiteSpace([string]$Snapshot.SizeOverride)) {
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'size', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'size', [string]$Snapshot.SizeOverride) | Out-Null
    }
    if ([string]::IsNullOrWhiteSpace([string]$Snapshot.DensityOverride)) {
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'density', 'reset') | Out-Null
    }
    else {
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'density', [string]$Snapshot.DensityOverride) | Out-Null
    }
    Restore-Setting 'system' 'font_scale' ([string]$Snapshot.FontScale)
    Restore-Setting 'system' 'accelerometer_rotation' ([string]$Snapshot.AccelerometerRotation)
    Restore-Setting 'system' 'user_rotation' ([string]$Snapshot.UserRotation)
}

function Assert-DeviceSnapshotRestored {
    param([Parameter(Mandatory = $true)]$Expected)
    $actual = Get-DeviceSnapshot
    foreach ($property in @(
        'SizeOverride','DensityOverride','FontScale','AccelerometerRotation','UserRotation')) {
        if ([string]$actual.$property -cne [string]$Expected.$property) {
            throw "Android device setting was not restored: $property"
        }
    }
}

function Set-Scenario {
    param([Parameter(Mandatory = $true)]$Scenario)
    $width = [int]$Scenario.Width
    $height = [int]$Scenario.Height
    if ([string]$Scenario.Orientation -eq 'landscape') {
        $rotation = '1'
        $naturalWidth = [Math]::Min($width, $height)
        $naturalHeight = [Math]::Max($width, $height)
    }
    else {
        $rotation = '0'
        $naturalWidth = [Math]::Min($width, $height)
        $naturalHeight = [Math]::Max($width, $height)
    }
    Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'wm', 'size', ("{0}x{1}" -f $naturalWidth, $naturalHeight)) | Out-Null
    Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'wm', 'density', '160') | Out-Null
    Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'settings', 'put', 'system', 'font_scale',
        ([string]$Scenario.FontScale)) | Out-Null
    Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'settings', 'put', 'system', 'accelerometer_rotation', '0') | Out-Null
    Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'shell', 'settings', 'put', 'system', 'user_rotation', $rotation) | Out-Null
}

function Get-Bounds {
    param([Parameter(Mandatory = $true)][string]$Text)
    if ($Text -notmatch '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') {
        throw "Android bounds are malformed: $Text"
    }
    return [pscustomobject]@{
        Left = [int]$matches[1]
        Top = [int]$matches[2]
        Right = [int]$matches[3]
        Bottom = [int]$matches[4]
    }
}

function Get-ShellNodes {
    param([Parameter(Mandatory = $true)][xml]$Document)
    $results = [Collections.Generic.List[object]]::new()
    foreach ($tab in $expectedTabs) {
        $matches = @($Document.SelectNodes('//node') | Where-Object {
            [string]$_.text -ceq $tab -or [string]$_.'content-desc' -ceq $tab
        })
        if ($matches.Count -ne 1) {
            throw "Shell tab must appear exactly once. tab=$tab actual=$($matches.Count)"
        }
        $bounds = Get-Bounds ([string]$matches[0].bounds)
        if ($bounds.Right -le $bounds.Left -or $bounds.Bottom -le $bounds.Top) {
            throw "Shell tab bounds are empty. tab=$tab"
        }
        $results.Add([pscustomobject]@{
            Tab = $tab
            Left = $bounds.Left
            Top = $bounds.Top
            Right = $bounds.Right
            Bottom = $bounds.Bottom
            CenterX = [int](($bounds.Left + $bounds.Right) / 2)
            CenterY = [int](($bounds.Top + $bounds.Bottom) / 2)
            Selected = ([string]$matches[0].selected -ceq 'true')
            Enabled = ([string]$matches[0].enabled -ceq 'true')
        })
    }
    return @($results)
}

function Assert-ShellGeometry {
    param([Parameter(Mandatory = $true)][object[]]$Nodes, [int]$Width, [int]$Height)
    if ($Nodes.Count -ne 5 -or @($Nodes.Tab | Sort-Object -Unique).Count -ne 5) {
        throw 'Shell tab identity contract is not exact.'
    }
    foreach ($node in $Nodes) {
        if (-not $node.Enabled -or $node.Left -lt 0 -or $node.Top -lt 0 -or
            $node.Right -gt $Width -or $node.Bottom -gt $Height) {
            throw "Shell tab is disabled or outside the viewport. tab=$($node.Tab)"
        }
    }
    $ordered = @($Nodes | Sort-Object Left)
    if ($ordered[0].Left -gt 2 -or $ordered[-1].Right -lt ($Width - 2)) {
        throw 'Shell tabs do not span the viewport width.'
    }
    for ($left = 0; $left -lt $Nodes.Count; $left++) {
        for ($right = $left + 1; $right -lt $Nodes.Count; $right++) {
            $a = $Nodes[$left]
            $b = $Nodes[$right]
            $overlapWidth = [Math]::Min($a.Right, $b.Right) - [Math]::Max($a.Left, $b.Left)
            $overlapHeight = [Math]::Min($a.Bottom, $b.Bottom) - [Math]::Max($a.Top, $b.Top)
            if ($overlapWidth -gt 0 -and $overlapHeight -gt 0) {
                throw "Shell tabs overlap. left=$($a.Tab) right=$($b.Tab)"
            }
        }
    }
}

function Get-UiDocument {
    param([Parameter(Mandatory = $true)][string]$LocalPath)
    $remote = '/data/local/tmp/georaeplan-shell.xml'
    Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'uiautomator', 'dump', $remote) | Out-Null
    $xmlText = (Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'exec-out', 'cat', $remote)).Output
    Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'rm', '-f', $remote) -AllowFailure | Out-Null
    [IO.File]::WriteAllText($LocalPath, $xmlText, [Text.UTF8Encoding]::new($false))
    return [xml]$xmlText
}

function Wait-ForShell {
    param([int]$Width, [int]$Height, [string]$XmlPath)
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        try {
            $document = Get-UiDocument $XmlPath
            $nodes = @(Get-ShellNodes $document)
            Assert-ShellGeometry $nodes $Width $Height
            return $nodes
        }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 350
        }
    } while ($true)
}

function Save-Screenshot {
    param([Parameter(Mandatory = $true)][string]$Path)
    $arguments = @('-s', $script:ResolvedDevice, 'exec-out', 'screencap', '-p')
    $process = Start-Process `
        -FilePath $AdbPath `
        -ArgumentList $arguments `
        -RedirectStandardOutput $Path `
        -NoNewWindow `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Item -LiteralPath $Path).Length -le 8) {
        throw 'Android screenshot capture failed.'
    }
    $stream = [IO.File]::OpenRead($Path)
    try {
        $signature = [byte[]]::new(8)
        $expectedSignature = [byte[]](137,80,78,71,13,10,26,10)
        if ($stream.Read($signature, 0, $signature.Length) -ne $signature.Length) {
            throw 'Android screenshot is not a PNG file.'
        }
        for ($index = 0; $index -lt $signature.Length; $index++) {
            if ($signature[$index] -ne $expectedSignature[$index]) {
                throw 'Android screenshot is not a PNG file.'
            }
        }
    }
    finally {
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
    throw "adb is missing: $AdbPath"
}
if (-not (Test-Path -LiteralPath $ContractPath -PathType Leaf)) {
    throw "Android state contract is missing: $ContractPath"
}
if ($PackageName -cne 'kr.georaeplan.mobile') {
    throw 'The Shell audit requires the exact production application id.'
}
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'OutputDirectory must not already exist.'
}

$contract = Get-Content -LiteralPath $ContractPath -Raw -Encoding UTF8 | ConvertFrom-Json
$scenarios = @($contract.BaseScenarios)
if ([int]$contract.BaseScenarioCount -ne 24 -or $scenarios.Count -ne 24 -or
    @($scenarios.Name | Sort-Object -Unique).Count -ne 24) {
    throw 'The Shell audit requires the exact 24 base scenario contract.'
}

$script:ResolvedDevice = Resolve-Device
$qemu = (Invoke-Adb -Arguments @(
    '-s', $script:ResolvedDevice,
    'shell', 'getprop', 'ro.kernel.qemu')).Output.Trim()
if ($qemu -cne '1') {
    throw 'The Shell audit is restricted to an Android emulator.'
}
$installed = Invoke-Adb -Arguments @(
    '-s', $script:ResolvedDevice,
    'shell', 'pm', 'path', $PackageName) -AllowFailure
if ($installed.ExitCode -ne 0 -or
    $installed.Output -notmatch '(?m)^package:') {
    throw 'The exact Shell audit package is not installed on the emulator.'
}
$resolvedLauncher = Resolve-Launcher
$snapshot = Get-DeviceSnapshot
$results = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
try {
    foreach ($scenario in $scenarios) {
        Set-Scenario $scenario
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'am', 'force-stop', $PackageName) | Out-Null
        Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'am', 'start', '-W', '-n', $resolvedLauncher) | Out-Null
        $safeName = ([string]$scenario.Name -replace '[^A-Za-z0-9._-]', '_')
        $initialXml = Join-Path $OutputDirectory ($safeName + '-initial.xml')
        $nodes = @(Wait-ForShell ([int]$scenario.Width) ([int]$scenario.Height) $initialXml)
        foreach ($tab in $expectedTabs) {
            $node = @($nodes | Where-Object Tab -CEQ $tab)
            if ($node.Count -ne 1) { throw "Shell tab lookup is ambiguous: $tab" }
            Invoke-Adb -Arguments @(
                '-s', $script:ResolvedDevice, 'shell', 'input', 'tap',
                [string]$node[0].CenterX, [string]$node[0].CenterY) | Out-Null
            Start-Sleep -Milliseconds 500
            $leaf = $safeName + '-' + ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($tab)) -replace '[^A-Za-z0-9]', '')
            $xmlPath = Join-Path $OutputDirectory ($leaf + '.xml')
            $current = @(Wait-ForShell ([int]$scenario.Width) ([int]$scenario.Height) $xmlPath)
            $selected = @($current | Where-Object Selected)
            if ($selected.Count -ne 1 -or [string]$selected[0].Tab -cne $tab) {
                throw "Shell tab selection did not move exactly. expected=$tab"
            }
            $pngPath = Join-Path $OutputDirectory ($leaf + '.png')
            Save-Screenshot $pngPath
            $results.Add([pscustomobject][ordered]@{
                Scenario = [string]$scenario.Name
                Tab = $tab
                Width = [int]$scenario.Width
                Height = [int]$scenario.Height
                FontScale = [double]$scenario.FontScale
                Bounds = @($current | Select-Object Tab,Left,Top,Right,Bottom,Selected,Enabled)
                XmlPath = $xmlPath
                ScreenshotPath = $pngPath
            })
        }
    }
    $fatal = (Invoke-Adb -Arguments @(
        '-s', $script:ResolvedDevice, 'logcat', '-d', '-b', 'crash') -AllowFailure).Output
    if ($fatal -match [regex]::Escape($PackageName)) {
        throw 'The Shell audit observed a package crash.'
    }
    if ($results.Count -ne 120) {
        throw "The exact Shell matrix is incomplete. actual=$($results.Count)"
    }
}
finally {
    Invoke-Adb -Arguments @('-s', $script:ResolvedDevice, 'shell', 'am', 'force-stop', $PackageName) -AllowFailure | Out-Null
    Restore-DeviceSnapshot $snapshot
    Assert-DeviceSnapshotRestored $snapshot
}

$report = [pscustomobject][ordered]@{
    SchemaVersion = 1
    Result = 'PASS'
    Device = $script:ResolvedDevice
    PackageName = $PackageName
    ScenarioCount = 24
    TabCount = 5
    MeasurementCount = 120
    Tabs = $expectedTabs
    Measurements = @($results)
}
$reportPath = Join-Path $OutputDirectory 'android-shell-exact24-result.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))
[pscustomobject][ordered]@{
    SchemaVersion = 1
    Result = 'PASS'
    Device = $script:ResolvedDevice
    PackageName = $PackageName
    ScenarioCount = 24
    TabCount = 5
    MeasurementCount = 120
    Tabs = $expectedTabs
    ReportPath = $reportPath
} | ConvertTo-Json -Compress | Write-Output
