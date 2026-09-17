$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Run only through the prepared Windows Sandbox mappings. No host MSI action.
$inputRoot = 'C:\NativeCrashInput'
$outputRoot = 'C:\NativeCrashOutput'
if (-not (Test-Path "$inputRoot\fixture.json") -or -not (Test-Path $outputRoot)) { throw 'Prepared Sandbox mappings are required.' }
$fixture = Get-Content "$inputRoot\fixture.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if ($fixture.purpose -cne 'native-installer-crash-20260912' -or
    (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine') { throw 'Dedicated Sandbox fixture required.' }
if (Test-Path "$outputRoot\result.json") { throw 'Preserve existing evidence.' }
Add-Type -Path "$inputRoot\NativeMsiRuntime.cs"
$workspace = 'C:\NativeCrashWorkspace'
$target = Join-Path $workspace 'Installed'
if (Test-Path $workspace) { throw 'Fixture workspace already exists.' }
[void](New-Item -ItemType Directory -Path $workspace)
& icacls.exe $workspace /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect the fixture workspace.' }
foreach ($folder in @('Desktop','Programs','CommonDesktopDirectory','CommonPrograms')) {
    [void][Environment]::GetFolderPath([Environment+SpecialFolder]$folder,[Environment+SpecialFolderOption]::Create)
}
$checks = New-Object 'System.Collections.Generic.List[string]'
$snapshots = New-Object 'System.Collections.Generic.List[object]'
$status = [ordered]@{ state='running'; phase='initial'; pid=$PID; startedAt=(Get-Date).ToString('o'); checks=@(); snapshots=@() }
function Save {
    $status.checks=@($checks.ToArray()); $status.snapshots=@($snapshots.ToArray())
    $status | ConvertTo-Json -Depth 10 | Set-Content "$outputRoot\result.json" -Encoding UTF8
}
function Payload {
    return @(Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName.Substring($target.Length+1).Replace('\','/');Length=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
}
function Assert-Payload($Files) {
    [GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($target,
        [string[]]@($Files | ForEach-Object { $_.Path }),
        [long[]]@($Files | ForEach-Object { [long]$_.Length }),
        [string[]]@($Files | ForEach-Object { $_.Sha256 }))
    if (@(Payload).Count -ne @($Files).Count) { throw 'Unexpected installed payload files.' }
}
function Assert-State([string]$Phase,[int]$Version,$Files) {
    $product=[GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot($fixture.upgradeCode,$target)
    $expected=$fixture.products | Where-Object version -eq ($Version.ToString()+'.0.0')
    if ($null -eq $product -or $product.Version -ne $expected.version -or [guid]$product.ProductCode -ne [guid]$expected.productCode) { throw 'Unexpected registered version.' }
    foreach($other in @($fixture.products | Where-Object productCode -ne $expected.productCode)) {
        if($null -ne [GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($other.productCode)) { throw 'Previous product registration remained.' }
    }
    Assert-Payload $Files
    $pending=@(Get-ChildItem -LiteralPath $workspace -Directory -Force -Filter '.tradeplan-update-supervisor-state-*')
    if($pending.Count -ne 0){throw 'Recovery journal remains after successful recovery.'}
    $snapshots.Add([pscustomobject]@{phase=$Phase;product=$product;files=@(Payload);pendingCount=$pending.Count})
    Save
}
function Start-Installer([string]$Package,[string]$Case,[string]$Checkpoint,[switch]$RecoveryOnly) {
    $env:GEORAEPLAN_INSTALLER_TEST_NATIVE_CHECKPOINT=$Checkpoint
    $env:GEORAEPLAN_INSTALLER_TEST_CAPABILITY=[IO.File]::ReadAllText((Join-Path $Package '.georaeplan-installer-test-capability'))
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $Package 'Install-GeoraePlan.ps1')+'" -InstallRoot "'+$target+'" -NoLaunch -SuppressUi -WorkerTimeoutSeconds 600 -LogPath "'+$outputRoot+'\'+$Case+'.log"'
    if ($RecoveryOnly) { $arguments += ' -RecoveryOnly' }
    try {
        $process=Start-Process powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput "$outputRoot\$Case.stdout" -RedirectStandardError "$outputRoot\$Case.stderr"
        [void]$process.Handle
        return $process
    }
    finally {
        Remove-Item Env:\GEORAEPLAN_INSTALLER_TEST_NATIVE_CHECKPOINT -ErrorAction SilentlyContinue
        Remove-Item Env:\GEORAEPLAN_INSTALLER_TEST_CAPABILITY -ErrorAction SilentlyContinue
    }
}
function Wait-Exit($Process,[int]$Expected) {
    if(-not $Process.WaitForExit(600000)){throw ('Installer still running: '+$Process.Id)}
    if($Process.ExitCode -ne $Expected){throw ('Unexpected installer exit: '+$Process.ExitCode+' expected '+$Expected)}
}
function Wait-Checkpoint($Supervisor,[string]$Package,[string]$Phase) {
    $path=Join-Path $Package ('.native-test-'+$Phase+'.json')
    $deadline=[DateTime]::UtcNow.AddSeconds(480)
    do {
        if($Supervisor.HasExited){throw ('Supervisor exited before checkpoint: '+$Supervisor.ExitCode)}
        if(Test-Path $path){
            try{$marker=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json}catch{$marker=$null}
            if($null -ne $marker -and $marker.phase -ceq $Phase -and [int]$marker.supervisorPid -eq $Supervisor.Id){return $marker}
        }
        if([DateTime]::UtcNow -gt $deadline){throw ('Checkpoint not yet reached; supervisor remains live: '+$Supervisor.Id)}
        Start-Sleep -Milliseconds 250
    }while($true)
}
function Run-Crash([string]$Case,[int]$PackageVersion,[string]$Phase,[string]$Victim,[int]$ExpectedVersion,$ExpectedFiles) {
    $status.phase=$Case;Save
    $package=Join-Path $workspace $Case
    Copy-Item -LiteralPath (Join-Path $inputRoot ('package'+$PackageVersion)) -Destination $package -Recurse
    $supervisor=Start-Installer $package $Case $Phase
    $status.supervisorPid=$supervisor.Id;Save
    $marker=Wait-Checkpoint $supervisor $package $Phase
    $worker=Get-Process -Id $marker.pid -ErrorAction Stop
    if($worker.Path -ine $marker.processPath -or $worker.StartTime.ToUniversalTime().Ticks -ne [long]$marker.startUtcTicks){throw 'Worker identity mismatch.'}
    $marker | ConvertTo-Json | Set-Content "$outputRoot\$Case-checkpoint.json" -Encoding UTF8
    if($Victim -eq 'Worker') {
        $worker.Kill();$worker.WaitForExit()
        $expectedExit=if($Phase -eq 'BeforeCommit'){1}else{0}
        Wait-Exit $supervisor $expectedExit
    }
    else {
        # This is the exact Process object created above, not a name-based kill.
        $supervisor.Kill();$supervisor.WaitForExit()
        $recovery=Start-Installer $package ($Case+'-recovery') '' -RecoveryOnly
        Wait-Exit $recovery 0
        $recovery.Dispose()
    }
    $worker.Dispose();$supervisor.Dispose()
    Assert-State $Case $ExpectedVersion $ExpectedFiles
    $checks.Add($Case);Save
}
try {
    Save
    foreach($product in $fixture.products){
        $msi=Join-Path $inputRoot $product.msiPath
        if((Get-FileHash -LiteralPath $msi).Hash -ine $product.msiSha256){throw 'Input MSI hash mismatch.'}
    }
    $oldMsi=Join-Path $inputRoot $fixture.products[0].msiPath
    $initial=Start-Process C:\Windows\System32\msiexec.exe -ArgumentList ('/i "'+$oldMsi+'" /qn /norestart INSTALLFOLDER="'+$target+'" /l*v "'+$outputRoot+'\initial.log"') -WindowStyle Hidden -PassThru -Wait
    if($initial.ExitCode -ne 0){throw 'Initial fixture installation failed.'}
    Assert-State 'initial' 1 $fixture.products[0].files
    # Represent files changed by an earlier ZIP update but not owned by the
    # registered MSI version. Both rollback paths must preserve these bytes.
    [IO.File]::AppendAllText((Join-Path $target 'appsettings.json'), "`r`n ")
    $oldFiles=Payload
    $checks.Add('initial MSI with a preexisting modified payload');Save
    Run-Crash 'worker-before-commit' 2 BeforeCommit Worker 1 $oldFiles
    Run-Crash 'supervisor-before-commit' 2 BeforeCommit Supervisor 1 $oldFiles
    Run-Crash 'worker-after-commit' 2 AfterCommit Worker 2 $fixture.products[1].files
    Run-Crash 'supervisor-after-commit' 3 AfterCommit Supervisor 3 $fixture.products[2].files
    $status.state='completed';$status.phase='finished';$status.finishedAt=(Get-Date).ToString('o');Save
    exit 0
}
catch {
    $status.state='failed';$status.error=$_.ToString();$status.stack=$_.ScriptStackTrace;$status.finishedAt=(Get-Date).ToString('o');Save
    exit 1
}
