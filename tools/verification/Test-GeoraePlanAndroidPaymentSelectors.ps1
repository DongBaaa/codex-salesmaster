param(
    [string]$ScriptPath = (Join-Path $PSScriptRoot '..\mobile\Invoke-GeoraePlanAndroidPaymentE2E.ps1'),
    [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $ScriptPath).Path, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Payment E2E script does not parse.' }
# Import only pure selectors and the two mocked UI workflows. Never run the
# script entry point, authentication, API writes, adb, or process management.
foreach ($name in @('Get-NodeCenterByText', 'Get-EditTextNodeByHint',
        'Open-BottomTabAndAssert', 'Select-PdfAttachmentFromDevice', 'New-TestAttachmentPdf',
        'Test-AndroidExternalActivityFocus')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $name
    }, $false))
    if ($definitions.Count -ne 1) { throw "Expected one function: $name" }
    Invoke-Expression $definitions[0].Extent.Text
}

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $true; Error = '' })
    }
    catch {
        $results.Add([pscustomobject]@{ Name = $Name; Passed = $false; Error = $_.Exception.Message })
    }
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Select-Field([string]$Xml, [string]$Hint, [string]$Value = '', [switch]$Focused) {
    $parameters = @{ Content = $Xml; Hint = $Hint; RequireFocused = $Focused }
    if ((Get-Command Get-EditTextNodeByHint).Parameters.ContainsKey('ExpectedText')) {
        $parameters.ExpectedText = $Value
    }
    Get-EditTextNodeByHint @parameters
}
function Field([string]$Text, [string]$HintAttribute = '', [string]$Focused = 'true') {
    '<node text="' + $Text + '" ' + $HintAttribute + ' class="android.widget.AutoCompleteTextView" focused="' + $Focused + '" bounds="[168,315][1017,438]" />'
}

$hint = '거래처명 / 전표번호 / 메모'
Check 'explicit hint' {
    $node = Select-Field (Field 'fixture' ('hint="' + $hint + '"')) $hint 'fixture' -Focused
    Require ($node.Text -eq 'fixture' -and $node.Point.Y -eq 376) 'Explicit hint not selected.'
}
Check 'Android placeholder exposed as text without hint' {
    $node = Select-Field (Field $hint) $hint
    Require ($null -ne $node) 'Observed Android search placeholder was missed.'
}
Check 'typed search without hint and confirmed focus' {
    $node = Select-Field (Field 'mobileecustfixture') $hint 'mobileecustfixture' -Focused
    Require ($node.Text -eq 'mobileecustfixture') 'Typed value was not verified.'
}
Check 'customer search without hint' {
    $node = Select-Field (Field '거래처명 / 전화 / 사업자번호') '거래처명 / 전화 / 사업자번호'
    Require ($null -ne $node) 'Customer search placeholder was missed.'
}
Check 'HTML encoded placeholder and typed value' {
    $node = Select-Field (Field 'A &amp; B') 'search' 'A & B' -Focused
    Require ($node.Text -ceq 'A & B') 'Encoded text was not decoded.'
}
Check 'wrong explicit hint is not overridden by matching value' {
    Require ($null -eq (Select-Field (Field 'fixture' 'hint="unrelated"') $hint 'fixture')) 'Wrong input selected.'
}
Check 'unfocused field rejected before typing' {
    Require ($null -eq (Select-Field (Field 'fixture' ('hint="' + $hint + '"') 'false') $hint 'fixture' -Focused)) 'Focus requirement bypassed.'
}
Check 'duplicate matching fields fail closed' {
    $xml = (Field $hint ('hint="' + $hint + '"')) * 2
    $rejected = $false
    try { Select-Field $xml $hint | Out-Null } catch { $rejected = $_.Exception.Message -like 'multiple Android text fields*' }
    Require $rejected 'Ambiguous fields did not stop input.'
}
Check 'non-editable label rejected' {
    $xml = (Field $hint ('hint="' + $hint + '"')).Replace('android.widget.AutoCompleteTextView', 'android.widget.TextView')
    Require ($null -eq (Select-Field $xml $hint)) 'A label was treated as editable.'
}

# UI workflows use deterministic screens based on the Android 14 failure:
# tab label at y=2210..2258, and the PDF filename below the initial viewport.
function Start-Sleep { param($Seconds, $Milliseconds) }
function Get-ScreenSize { param($AdbPath, $DeviceId)
    @{ Width = 1080; Height = 2400 }
}
function Tap-BottomTab { throw 'Unobserved ratio-based tab click attempted.' }
function Tap-Point { param($AdbPath, $DeviceId, $X, $Y)
    $script:Point = @($X, $Y)
}
function Tap-UiText { param($AdbPath, $DeviceId, $Content, $Text, $ClassName, $StepName) }
function Invoke-Adb { param($AdbPath, $Arguments)
    if ($Arguments -contains 'swipe') { $script:Scrolled = $true }
    else { throw 'Unexpected adb action in selector test.' }
}
function Get-UiDump { param($AdbPath, $DeviceId, $EvidenceDirectory, $Name)
    $content = $script:TabXml
    if ($Name -like '*attachment-button*') {
        $content = '<node text="내역 첨부하기" class="android.widget.Button" bounds="[10,100][100,200]" />'
    }
    elseif ($Name -like '*file-picker*') { $content = 'fixture.pdf' }
    [pscustomobject]@{ Content = $content; Path = 'mock.xml' }
}
function Wait-UiContainsAll { param($AdbPath, $DeviceId, $EvidenceDirectory, $Name, $Needles, $StepName, $TimeoutSeconds)
    if ($Name -like '*attachment-selected*') {
        Require $script:Scrolled 'Filename remains below the viewport.'
        Require ($Needles -contains 'fixture.pdf' -and $Needles -contains '첨부 1건') 'Attachment checks were weakened.'
    }
    [pscustomobject]@{ Content = ($Needles -join ' '); Path = 'mock.xml' }
}
function Run-Tab {
    $steps = New-Object System.Collections.Generic.List[object]
    Open-BottomTabAndAssert -Screen @{ Width = 1080; Height = 2400 } -TabText '전표' -FallbackXRatio 0.7 -Needles @('조회') -Steps $steps | Out-Null
}
Check 'tab uses observed lower label, not upper title or ratio' {
    $script:Point = $null
    $script:TabXml = '<node text="전표" class="android.widget.TextView" bounds="[0,100][100,200]" /><node text="전표" class="android.widget.TextView" bounds="[727,2210][785,2258]" />'
    Run-Tab
    Require ($script:Point[0] -eq 756 -and $script:Point[1] -eq 2234) 'Wrong tab location used.'
}
Check 'missing observed tab stops without clicking' {
    $script:Point = $null
    $script:TabXml = '<node text="홈" class="android.widget.TextView" bounds="[0,2210][100,2258]" />'
    $rejected = $false
    try { Run-Tab } catch { $rejected = $_.Exception.Message -like 'bottom tab not found*' }
    Require ($rejected -and $null -eq $script:Point) 'Missing tab did not fail closed.'
}
Check 'PDF selection scrolls and still checks filename plus count' {
    $script:Scrolled = $false
    Select-PdfAttachmentFromDevice -Attachment @{ FileName = 'fixture.pdf' } | Out-Null
    Require $script:Scrolled 'Selected PDF was not exposed before verification.'
}

$fixtureDirectory = if ($ResultPath) {
    [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ResultPath))
} else {
    Join-Path ([IO.Path]::GetTempPath()) ('georaeplan-selector-' + [guid]::NewGuid().ToString('N'))
}
[void][IO.Directory]::CreateDirectory($fixtureDirectory)
$fixtureStamp = if ($ResultPath) { [IO.Path]::GetFileNameWithoutExtension($ResultPath) } else { 'structure-check' }
$script:FixturePdf = $null
Check 'PDF fixture byte offsets and declared stream length are valid' {
    $fixture = New-TestAttachmentPdf -EvidenceDirectory $fixtureDirectory -Timestamp $fixtureStamp
    $script:FixturePdf = $fixture.LocalPath
    $bytes = [IO.File]::ReadAllBytes($fixture.LocalPath)
    $pdf = [Text.Encoding]::ASCII.GetString($bytes)
    $start = [regex]::Match($pdf, 'startxref\s+(\d+)')
    Require $start.Success 'Missing PDF startxref.'
    $xrefOffset = [int]$start.Groups[1].Value
    Require ($pdf.Substring($xrefOffset).StartsWith('xref')) 'Incorrect PDF cross-reference offset.'
    $rows = [regex]::Matches($pdf.Substring($xrefOffset), '(?m)^(\d{10}) 00000 n')
    Require ($rows.Count -eq 5) 'Unexpected PDF object count.'
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $position = [int]$rows[$i].Groups[1].Value
        Require ($pdf.Substring($position).StartsWith(($i + 1).ToString() + ' 0 obj')) 'Incorrect PDF object offset.'
    }
    $declared = [regex]::Match($pdf, '/Length (\d+)')
    $stream = [regex]::Match($pdf, '(?s)stream\r?\n(.*?)endstream')
    Require ($declared.Success -and $stream.Success) 'Missing PDF content stream.'
    Require ([int]$declared.Groups[1].Value -eq [Text.Encoding]::ASCII.GetByteCount($stream.Groups[1].Value)) 'Incorrect PDF stream length.'
}

foreach ($sample in @(
    @{ Name = 'actual PDF viewer'; Focus = 'mCurrentFocus=Window{217f9f u0 com.google.android.apps.docs/com.google.android.apps.viewer.PdfViewerActivity}'; Expected = $true },
    @{ Name = 'empty focus'; Focus = ''; Expected = $false },
    @{ Name = 'focus lookup error'; Focus = 'focus-unavailable: adb disconnected'; Expected = $false },
    @{ Name = 'null window with stale external focused app'; Focus = 'mCurrentFocus=null | mFocusedApp=ActivityRecord{abc u0 com.google.android.apps.docs/.PdfViewerActivity}'; Expected = $false },
    @{ Name = 'own app'; Focus = 'mCurrentFocus=Window{abc u0 kr.georaeplan.mobile/.MainActivity}'; Expected = $false },
    @{ Name = 'launcher'; Focus = 'mCurrentFocus=Window{abc u0 com.google.android.apps.nexuslauncher/.NexusLauncherActivity}'; Expected = $false },
    @{ Name = 'permission dialog'; Focus = 'mCurrentFocus=Window{abc u0 com.google.android.permissioncontroller/.GrantPermissionsActivity}'; Expected = $false },
    @{ Name = 'resolver needs separate visible UI evidence'; Focus = 'mCurrentFocus=Window{abc u0 android/com.android.internal.app.ResolverActivity}'; Expected = $false }
)) {
    Check ('external activity: ' + $sample.Name) {
        $actual = Test-AndroidExternalActivityFocus -FocusSummary $sample.Focus -PackageName 'kr.georaeplan.mobile'
        Require ($actual -eq $sample.Expected) 'External activity classification was incorrect.'
    }
}

$summary = [pscustomobject]@{
    Script = (Resolve-Path -LiteralPath $ScriptPath).Path
    Passed = @($results | Where-Object Passed).Count
    Failed = @($results | Where-Object { -not $_.Passed }).Count
    FixturePdf = $script:FixturePdf
    Cases = $results
}
if ($ResultPath) { $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
$summary | Select-Object Passed, Failed | ConvertTo-Json -Compress
if ($summary.Failed) { exit 1 }
