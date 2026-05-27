param(
    [string]$AppExe = 'D:\거래플랜\테스트 시행\실행환경\App\거래플랜.Desktop.App.exe',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\desktop-ui-smoke'),
    [int]$TimeoutSec = 45,
    [switch]$StartServer,
    [string]$DotnetExe = 'D:\.dotnet-sdk\dotnet.exe',
    [string]$ServerDir = 'D:\거래플랜\테스트 시행\실행환경\Server',
    [string]$ServerDataRoot = 'D:\거래플랜\테스트 시행\실행환경\ServerData',
    [string]$AppDataRoot = 'D:\거래플랜\테스트 시행\실행환경\AppData',
    [int]$ServerPort = 19080,
    [switch]$UseInAppSelfTest,
    [string]$InAppSelfTestReportPath,
    [switch]$AttachExisting,
    [switch]$KeepAppOpen
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System.Runtime.InteropServices;
public static class GeoraePlanDesktopUiSmokeMouse {
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, System.UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);
}
"@

function Get-FreePort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Wait-HttpReady {
    param(
        [string]$Url,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
            # wait and retry
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Wait-FileReady {
    param(
        [string]$Path,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            try {
                $content = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
                if (-not [string]::IsNullOrWhiteSpace($content)) {
                    return $true
                }
            }
            catch {
                # 파일 기록 중일 수 있으므로 재시도합니다.
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Start-IsolatedTestServer {
    param(
        [string]$DotnetExe,
        [string]$ServerDir,
        [string]$ServerDataRoot,
        [string]$ServerUrl
    )

    if (-not (Test-Path -LiteralPath $DotnetExe)) {
        throw "dotnet not found: $DotnetExe"
    }

    $serverDll = Get-ChildItem -LiteralPath $ServerDir -Filter '*.Server.Api.dll' -File |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($serverDll) -or -not (Test-Path -LiteralPath $serverDll)) {
        throw "Server dll not found in $ServerDir"
    }

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = $ServerUrl
        'Kestrel__Endpoints__Http__Url' = $ServerUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'SeedUsers__EnableSeedUsers' = 'true'
        'SeedUsers__UsenetUsername' = 'usenet'
        'SeedUsers__UsenetPassword' = '1234'
        'SeedUsers__UpdateExistingUsenetPassword' = 'true'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'FileStorage__RootPath' = (Join-Path $ServerDataRoot 'FileStore')
        'Updates__StorageRoot' = (Join-Path $ServerDataRoot 'updates')
    }

    $previousEnv = @{}
    foreach ($key in $serverEnv.Keys) {
        $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$serverEnv[$key], 'Process')
    }

    try {
        $argumentList = ('"{0}" --environment Development' -f $serverDll.Replace('"', '""'))
        return Start-Process -FilePath $DotnetExe -ArgumentList $argumentList -WorkingDirectory $ServerDir -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $serverEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnv[$key], 'Process')
        }
    }
}

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Convert-OutputText {
    param([object[]]$Output)
    ($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
}

function New-Condition {
    param(
        [System.Windows.Automation.AutomationProperty]$Property,
        [object]$Value
    )
    New-Object System.Windows.Automation.PropertyCondition($Property, $Value)
}

function New-AndCondition {
    param([System.Windows.Automation.Condition[]]$Conditions)
    New-Object System.Windows.Automation.AndCondition(, $Conditions)
}

function Get-ProcessWindow {
    param(
        [int]$ProcessId,
        [string]$Name,
        [switch]$Contains,
        [int]$TimeoutSec = 20
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $condition = New-AndCondition @(
        (New-Condition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) $ProcessId),
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window))
    )

    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)

        foreach ($window in $windows) {
            $currentName = [string]$window.Current.Name
            if ($Contains) {
                if ($currentName.IndexOf($Name, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    return $window
                }
            }
            elseif ([string]::Equals($currentName, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $window
            }
        }

        Start-Sleep -Milliseconds 300
    }

    return $null
}

function Get-ProcessWindowNames {
    param([int]$ProcessId)

    $condition = New-AndCondition @(
        (New-Condition ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) $ProcessId),
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Window))
    )
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)

    $names = @()
    foreach ($window in $windows) {
        $names += [string]$window.Current.Name
    }

    $names
}

function Wait-LoginOrMainWindow {
    param(
        [int]$ProcessId,
        [int]$TimeoutSec = 45
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $mainWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '거래플랜' -Contains -TimeoutSec 1
        if ($null -ne $mainWindow) {
            if (([string]$mainWindow.Current.Name).IndexOf('로그인', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                return [pscustomobject]@{ Kind = 'Main'; Window = $mainWindow }
            }
        }

        $loginWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '로그인' -Contains -TimeoutSec 1
        if ($null -ne $loginWindow) {
            return [pscustomobject]@{ Kind = 'Login'; Window = $loginWindow }
        }

        Start-Sleep -Milliseconds 300
    }

    return [pscustomobject]@{ Kind = 'None'; Window = $null }
}

function Wait-MainWindowOnly {
    param(
        [int]$ProcessId,
        [int]$TimeoutSec = 90
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $mainWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '거래플랜' -Contains -TimeoutSec 1
        if ($null -ne $mainWindow -and ([string]$mainWindow.Current.Name).IndexOf('로그인', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $mainWindow
        }

        Start-Sleep -Milliseconds 300
    }

    return $null
}

function Find-FirstByName {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name))
}

function Find-FirstByNameAndControlType {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-AndCondition @(
            (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name),
            (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType)
        )))
}

function Find-FirstByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $AutomationId))
}

function Find-AllByControlType {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType))
}

function Find-RootByNameAndControlType {
    param(
        [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-AndCondition @(
            (New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name),
            (New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $ControlType)
        )))
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return $false }

    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($null -ne $pattern) {
            $pattern.Invoke()
            return $true
        }
    }
    catch {
        return $false
    }

    return $false
}

function Click-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    if ($null -eq $Element) { return $false }

    if (Invoke-Element $Element) {
        Start-Sleep -Milliseconds 700
        return $true
    }

    try {
        $Element.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 700
        return $true
    }
    catch {
        # 일부 WPF 요소는 포커스 설정이 제한될 수 있으므로 좌표 클릭을 계속 시도합니다.
    }

    try {
        $rect = $Element.Current.BoundingRectangle
        if ($rect.Width -gt 1 -and $rect.Height -gt 1) {
            $x = [int]($rect.Left + ($rect.Width / 2))
            $y = [int]($rect.Top + ($rect.Height / 2))
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
            Start-Sleep -Milliseconds 100
            [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [GeoraePlanDesktopUiSmokeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 500
            return $true
        }
    }
    catch {
        # 좌표 클릭 실패 시 InvokePattern으로 fallback합니다.
    }

    return Invoke-Element $Element
}

function Set-ElementText {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Text
    )

    if ($null -eq $Element) { return $false }

    try {
        $valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($null -ne $valuePattern -and -not $valuePattern.Current.IsReadOnly) {
            $valuePattern.SetValue($Text)
            return $true
        }
    }
    catch {
        # PasswordBox 등 일부 컨트롤은 ValuePattern 입력이 제한됩니다.
    }

    try {
        $Element.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait($Text)
        Start-Sleep -Milliseconds 100
        return $true
    }
    catch {
        return $false
    }
}

function Close-Window {
    param([System.Windows.Automation.AutomationElement]$Window)
    if ($null -eq $Window) { return }

    try {
        $pattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($null -ne $pattern) {
            $pattern.Close()
            Start-Sleep -Milliseconds 500
            return
        }
    }
    catch {
        # fallback below
    }

    try {
        $Window.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('%{F4}')
        Start-Sleep -Milliseconds 500
    }
    catch {
        # ignore close fallback failure; caller will terminate process if needed.
    }
}

function Test-NameExists {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $null -ne (Find-FirstByName -Root $Root -Name $Name)
}

function Get-DescendantNames {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [int]$Limit = 120
    )

    $results = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Root) {
        return @()
    }

    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        $name = [string]$element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name) -and -not $results.Contains($name)) {
            $results.Add($name) | Out-Null
            if ($results.Count -ge $Limit) {
                break
            }
        }
    }

    $results.ToArray()
}

function Add-Step {
    param(
        [System.Collections.Generic.List[object]]$Steps,
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $Steps.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

function Open-And-VerifyChildWindow {
    param(
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [int]$ProcessId,
        [string]$ButtonName,
        [string]$WindowTitle,
        [string[]]$RequiredNames,
        [System.Collections.Generic.List[object]]$Steps
    )

    $button = Find-FirstByNameAndControlType -Root $MainWindow -Name $ButtonName -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $button) {
        $button = Find-FirstByName -Root $MainWindow -Name $ButtonName
    }

    if ($null -eq $button) {
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail 'button not found'
        return $false
    }

    $enabledDeadline = (Get-Date).AddSeconds(30)
    while (-not $button.Current.IsEnabled -and (Get-Date) -lt $enabledDeadline) {
        Start-Sleep -Milliseconds 500
    }

    if (-not $button.Current.IsEnabled) {
        $rect = $button.Current.BoundingRectangle
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail "button disabled; rect=$([int]$rect.Left),$([int]$rect.Top),$([int]$rect.Width),$([int]$rect.Height)"
        return $false
    }

    $buttonRect = $button.Current.BoundingRectangle
    $patternNames = @()
    try {
        foreach ($pattern in $button.GetSupportedPatterns()) {
            $patternNames += $pattern.ProgrammaticName
        }
    }
    catch {
        $patternNames += 'patterns-unavailable'
    }
    Add-Step -Steps $Steps -Name "button-$ButtonName" -Passed $true -Detail "enabled=True; control=$($button.Current.ControlType.ProgrammaticName); aid=$($button.Current.AutomationId); class=$($button.Current.ClassName); framework=$($button.Current.FrameworkId); rect=$([int]$buttonRect.Left),$([int]$buttonRect.Top),$([int]$buttonRect.Width),$([int]$buttonRect.Height); offscreen=$($button.Current.IsOffscreen); patterns=$($patternNames -join '+')"

    try {
        $handle = [IntPtr]$MainWindow.Current.NativeWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [void][GeoraePlanDesktopUiSmokeMouse]::SetForegroundWindow($handle)
            Start-Sleep -Milliseconds 300
        }
        $MainWindow.SetFocus()
    }
    catch {
        # foreground 전환 실패 시에도 UIA/좌표 클릭을 계속 시도합니다.
    }

    if (-not (Click-Element $button)) {
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail 'button click failed'
        return $false
    }

    if ($ButtonName -eq '거래처 관리') {
        $menuItem = Find-RootByNameAndControlType -Name '거래처 관리' -ControlType ([System.Windows.Automation.ControlType]::MenuItem)
        if ($null -ne $menuItem) {
            [void](Click-Element $menuItem)
        }
    }

    $child = Get-ProcessWindow -ProcessId $ProcessId -Name $WindowTitle -Contains -TimeoutSec 25
    if ($null -eq $child) {
        $windowNames = Get-ProcessWindowNames -ProcessId $ProcessId
        Add-Step -Steps $Steps -Name "open-$ButtonName" -Passed $false -Detail "window not found: $WindowTitle; windows=$($windowNames -join ', ')"
        return $false
    }

    $missing = @()
    foreach ($required in $RequiredNames) {
        if (-not (Test-NameExists -Root $child -Name $required)) {
            $missing += $required
        }
    }

    $passed = $missing.Count -eq 0
    Add-Step -Steps $Steps -Name "window-$WindowTitle" -Passed $passed -Detail ($(if ($passed) { 'required controls found' } else { 'missing: ' + ($missing -join ', ') }))
    Close-Window $child
    return $passed
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $EvidenceDirectory "desktop-ui-smoke-$timestamp.md"
$jsonPath = Join-Path $EvidenceDirectory "desktop-ui-smoke-$timestamp.json"
if ($UseInAppSelfTest -and [string]::IsNullOrWhiteSpace($InAppSelfTestReportPath)) {
    $InAppSelfTestReportPath = Join-Path $EvidenceDirectory "desktop-ui-inapp-selftest-$timestamp.md"
}

if (-not (Test-Path -LiteralPath $AppExe)) {
    throw "앱 실행 파일을 찾지 못했습니다: $AppExe"
}

$steps = New-Object System.Collections.Generic.List[object]
$process = $null
$serverProcess = $null
$previousAppEnv = @{}
$passed = $false
$errorText = ''

try {
    $normalizedAppExe = [System.IO.Path]::GetFullPath($AppExe)
    if ($StartServer) {
        $port = Get-FreePort -StartingPort $ServerPort
        $serverUrl = "http://127.0.0.1:$port"
        $appSettings = Join-Path (Split-Path -Parent $AppExe) 'appsettings.json'
        $testRoot = Split-Path -Parent (Split-Path -Parent $AppExe)
        $setApiScript = Join-Path $testRoot 'Set-ApiBaseUrl.ps1'
        if ((Test-Path -LiteralPath $appSettings) -and (Test-Path -LiteralPath $setApiScript)) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $setApiScript -BaseUrl $serverUrl -AppSettingsPaths @($appSettings) | Out-Null
        }

        $serverProcess = Start-IsolatedTestServer -DotnetExe $DotnetExe -ServerDir $ServerDir -ServerDataRoot $ServerDataRoot -ServerUrl $serverUrl
        Add-Step -Steps $steps -Name 'start-server' -Passed $true -Detail "pid=$($serverProcess.Id), url=$serverUrl"
        if (-not (Wait-HttpReady -Url ($serverUrl + '/healthz') -TimeoutSeconds 90)) {
            throw "테스트 서버 healthz 대기 실패: $serverUrl"
        }

        Add-Step -Steps $steps -Name 'server-health' -Passed $true -Detail $serverUrl
    }

    $existingProcesses = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                -not $_.HasExited -and
                -not [string]::IsNullOrWhiteSpace($_.Path) -and
                [string]::Equals([System.IO.Path]::GetFullPath($_.Path), $normalizedAppExe, [System.StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        })

    if ($AttachExisting) {
        $process = $existingProcesses |
            Sort-Object StartTime -Descending -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $process) {
            throw "기존 테스트 앱 프로세스를 찾지 못했습니다: $AppExe"
        }

        Add-Step -Steps $steps -Name 'attach-process' -Passed $true -Detail "pid=$($process.Id)"
    }
    else {
        $existingProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500

        $appEnv = @{
            'GEORAEPLAN_APP_ROOT' = $AppDataRoot
            'GEORAEPLAN_DISABLE_LEGACY_MERGE' = '1'
            'GEORAEPLAN_TEST_MODE' = '1'
        }
        if ($UseInAppSelfTest) {
            $appEnv['GEORAEPLAN_DESKTOP_UI_SMOKE_REPORT'] = $InAppSelfTestReportPath
        }
        foreach ($key in $appEnv.Keys) {
            $previousAppEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, [string]$appEnv[$key], 'Process')
        }

        $process = Start-Process -FilePath $AppExe -WorkingDirectory (Split-Path -Parent $AppExe) -PassThru
        Add-Step -Steps $steps -Name 'start-process' -Passed $true -Detail "pid=$($process.Id)"
    }

    $startupWindow = Wait-LoginOrMainWindow -ProcessId $process.Id -TimeoutSec $TimeoutSec
    $mainWindow = $null
    if ($startupWindow.Kind -eq 'Login') {
        $loginWindow = $startupWindow.Window
        Add-Step -Steps $steps -Name 'login-window' -Passed $true -Detail 'found'

        $editControls = Find-AllByControlType -Root $loginWindow -ControlType ([System.Windows.Automation.ControlType]::Edit)
        $usernameBox = Find-FirstByAutomationId -Root $loginWindow -AutomationId 'UsernameBox'
        if ($null -eq $usernameBox -and $editControls.Count -gt 0) {
            $usernameBox = $editControls[0]
        }

        $passwordBox = Find-FirstByAutomationId -Root $loginWindow -AutomationId 'PasswordBox'
        if ($null -eq $passwordBox -and $editControls.Count -gt 1) {
            $passwordBox = $editControls[1]
        }

        if ($null -eq $usernameBox -or $null -eq $passwordBox) {
            throw "로그인 입력칸을 찾지 못했습니다. editCount=$($editControls.Count)"
        }

        if (-not (Set-ElementText -Element $usernameBox -Text $Username)) {
            throw '아이디 입력 실패'
        }
        if (-not (Set-ElementText -Element $passwordBox -Text $Password)) {
            throw '비밀번호 입력 실패'
        }

        $loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'
        if ($null -eq $loginButton -or -not (Invoke-Element $loginButton)) {
            throw '로그인 버튼 실행 실패'
        }
        Add-Step -Steps $steps -Name 'login-submit' -Passed $true -Detail 'submitted'
        $mainWindow = Wait-MainWindowOnly -ProcessId $process.Id -TimeoutSec 90
    }
    elseif ($startupWindow.Kind -eq 'Main') {
        $mainWindow = $startupWindow.Window
        Add-Step -Steps $steps -Name 'login-window' -Passed $true -Detail 'main window opened directly'
    }
    else {
        $windowNames = Get-ProcessWindowNames -ProcessId $process.Id
        throw "로그인/메인 창을 찾지 못했습니다. windows=$($windowNames -join ', ')"
    }

    if ($null -eq $mainWindow) {
        $windowNames = Get-ProcessWindowNames -ProcessId $process.Id
        $loginWindowAfterFailure = Get-ProcessWindow -ProcessId $process.Id -Name '로그인' -Contains -TimeoutSec 1
        $loginTexts = Get-DescendantNames -Root $loginWindowAfterFailure -Limit 40
        throw "메인 창을 찾지 못했습니다. windows=$($windowNames -join ', '); loginTexts=$($loginTexts -join ', ')"
    }

    Start-Sleep -Seconds 5

    $requiredMainButtons = @(
        '품목/재고 관리',
        '신규 렌탈 등록',
        '거래처 관리',
        '매입/매출 장부',
        '기간별 집계',
        '렌탈 업무',
        '환경설정',
        '휴지통',
        '판매작성',
        '구매작성',
        '수금 입력',
        '전표 인쇄[F9]'
    )
    $missingMainButtons = @()
    foreach ($buttonName in $requiredMainButtons) {
        if (-not (Test-NameExists -Root $mainWindow -Name $buttonName)) {
            $missingMainButtons += $buttonName
        }
    }
    Add-Step -Steps $steps -Name 'main-buttons' -Passed ($missingMainButtons.Count -eq 0) -Detail ($(if ($missingMainButtons.Count -eq 0) { 'all found' } else { 'missing: ' + ($missingMainButtons -join ', ') }))

    if ($UseInAppSelfTest) {
        if (-not (Wait-FileReady -Path $InAppSelfTestReportPath -TimeoutSeconds 160)) {
            Add-Step -Steps $steps -Name 'in-app-self-test' -Passed $false -Detail "report not created: $InAppSelfTestReportPath"
        }
        else {
            $inAppJsonPath = [System.IO.Path]::ChangeExtension($InAppSelfTestReportPath, '.json')
            $inAppPayload = $null
            if (Test-Path -LiteralPath $inAppJsonPath) {
                $inAppPayload = Get-Content -LiteralPath $inAppJsonPath -Raw | ConvertFrom-Json
            }

            $inAppPassed = $null -ne $inAppPayload -and [string]$inAppPayload.Result -eq 'PASS'
            Add-Step -Steps $steps -Name 'in-app-self-test' -Passed $inAppPassed -Detail "result=$($inAppPayload.Result); report=$InAppSelfTestReportPath"
            if ($null -ne $inAppPayload -and $null -ne $inAppPayload.Steps) {
                foreach ($inAppStep in @($inAppPayload.Steps)) {
                    Add-Step -Steps $steps -Name ("in-app-" + [string]$inAppStep.Name) -Passed ([bool]$inAppStep.Passed) -Detail ([string]$inAppStep.Detail)
                }
            }
        }
    }
    else {
        $childResults = @()
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '거래처 관리' -WindowTitle '거래처 관리' -RequiredNames @('새 거래처 등록', '선택 거래처 수정', '선택 거래처 삭제') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '품목/재고 관리' -WindowTitle '품목/재고 관리' -RequiredNames @('신규 품목', '품목 저장', '재고 초기화', '닫기 (F12)') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '판매작성' -WindowTitle '판매(매출)' -RequiredNames @('수금 입력', '항목추가') -Steps $steps
        $childResults += Open-And-VerifyChildWindow -MainWindow $mainWindow -ProcessId $process.Id -ButtonName '구매작성' -WindowTitle '구매(매입)' -RequiredNames @('지급 입력', '항목추가') -Steps $steps
    }

    $passed = ($steps | Where-Object { -not $_.Passed }).Count -eq 0
}
catch {
    $errorText = $_.Exception.Message
    Add-Step -Steps $steps -Name 'exception' -Passed $false -Detail $errorText
}
finally {
    if (-not $KeepAppOpen -and -not $AttachExisting -and $null -ne $process -and -not $process.HasExited) {
        try {
            $process.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 800
            if (-not $process.HasExited) {
                $process.Kill()
            }
        }
        catch {
            # ignore cleanup failure
        }
    }

    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        try {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        }
        catch {
            # ignore cleanup failure
        }
    }

    foreach ($key in $previousAppEnv.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousAppEnv[$key], 'Process')
    }
}

$payload = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('O')
    AppExe = $AppExe
    Result = if ($passed) { 'PASS' } else { 'FAIL' }
    Error = $errorText
    Steps = $steps
}
$payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 Desktop UI Smoke') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 작성시각: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))") | Out-Null
$lines.Add("- AppExe: $AppExe") | Out-Null
$lines.Add("- 결과: **$($payload.Result)**") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($errorText)) {
    $lines.Add("- 오류: $errorText") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add('| 단계 | 결과 | 상세 |') | Out-Null
$lines.Add('|---|---|---|') | Out-Null
foreach ($step in $steps) {
    $lines.Add("| $($step.Name) | $(if ($step.Passed) { 'PASS' } else { 'FAIL' }) | $(([string]$step.Detail).Replace('|', '\|')) |") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add("JSON: $jsonPath") | Out-Null
$lines | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "desktop_ui_smoke_report=$reportPath"
Write-Host "desktop_ui_smoke_json=$jsonPath"
Write-Host "result=$($payload.Result)"

if (-not $passed) {
    throw "Desktop UI smoke failed. Report: $reportPath"
}
