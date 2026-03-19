[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile
)

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Add-CheckResult {
    param(
        [System.Collections.Generic.List[object]]$Results,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    $Results.Add([pscustomobject]@{
        Name   = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    $ProjectFile = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
}

$results = [System.Collections.Generic.List[object]]::new()

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
Add-CheckResult -Results $results -Name 'dotnet' -Passed ($null -ne $dotnetCommand) -Detail ($(if ($dotnetCommand) { $dotnetCommand.Source } else { 'dotnet not found in PATH' }))

$projectExists = Test-Path -LiteralPath $ProjectFile
Add-CheckResult -Results $results -Name 'project-file' -Passed $projectExists -Detail $ProjectFile

$workloadInstalled = $false
$workloadDetail = 'dotnet unavailable'
if ($dotnetCommand) {
    try {
        $workloadOutput = & dotnet workload list 2>&1 | Out-String
        $workloadInstalled = [bool]($workloadOutput | Select-String -Pattern 'maui-android' -SimpleMatch)
        $workloadDetail = $workloadOutput.Trim()
    }
    catch {
        $workloadDetail = $_.Exception.Message
    }
}
Add-CheckResult -Results $results -Name 'maui-android-workload' -Passed $workloadInstalled -Detail $workloadDetail

$javaCommand = Get-Command java -ErrorAction SilentlyContinue
Add-CheckResult -Results $results -Name 'java' -Passed ($null -ne $javaCommand) -Detail ($(if ($javaCommand) { $javaCommand.Source } else { 'java not found in PATH' }))

$keytoolCommand = Get-Command keytool -ErrorAction SilentlyContinue
Add-CheckResult -Results $results -Name 'keytool' -Passed ($null -ne $keytoolCommand) -Detail ($(if ($keytoolCommand) { $keytoolCommand.Source } else { 'keytool not found in PATH' }))

$adbCommand = Get-Command adb -ErrorAction SilentlyContinue
Add-CheckResult -Results $results -Name 'adb' -Passed ($null -ne $adbCommand) -Detail ($(if ($adbCommand) { $adbCommand.Source } else { 'adb not found in PATH (optional unless deploying to device)' }))

$results | Format-Table -AutoSize | Out-Host

$failed = $results | Where-Object { -not $_.Passed }
if ($failed.Count -gt 0) {
    Write-Error ("Android environment check failed: " + (($failed.Name) -join ', '))
}

Write-Host 'android_environment_ready=true'
