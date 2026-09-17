$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$inputRoot='C:\FullNativeInput';$outputRoot='C:\FullNativeOutput';$packageInput='C:\FullNativePackage'
if(-not(Test-Path "$inputRoot\fixture.json") -or -not(Test-Path $outputRoot)){throw 'Dedicated Sandbox mappings required.'}
$fixture=Get-Content "$inputRoot\fixture.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$sha=[Security.Cryptography.SHA256]::Create()
try{$hostHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
if($hostHash -eq $fixture.hostComputerSha256 -or $fixture.purpose -cne 'full-native-product-20260912' -or
    (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine' -or
    @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count){throw 'Isolated network-disabled guest required.'}
if((Get-Process -Id $PID).SessionId -eq 0 -or -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Connect the Sandbox desktop, then execute as its ExistingLogin administrator.'}
$target='C:\Program Files (x86)\tradeplan';$work='C:\FullNativeWork';$data='C:\FullNativeUserData'
if((Test-Path $target) -or (Test-Path $work) -or (Test-Path $data) -or (Test-Path "$outputRoot\result.json")){throw 'Clean guest and fresh output required.'}
Add-Type -Path "$inputRoot\NativeMsiRuntime.cs"
foreach($folder in @('Desktop','Programs','CommonDesktopDirectory','CommonPrograms')){[void][Environment]::GetFolderPath([Environment+SpecialFolder]$folder,[Environment+SpecialFolderOption]::Create)}
[void](New-Item -ItemType Directory $work)
[void](New-Item -ItemType Directory "$data\data")
[void](New-Item -ItemType Directory "$data\attachments")
Copy-Item "$inputRoot\app-before.db" "$data\data\거래플랜.db"
[IO.File]::WriteAllText("$data\attachments\audit-preservation.txt",'isolated attachment preservation fixture')
$dataHash=(Get-FileHash "$data\data\거래플랜.db").Hash
$attachmentHash=(Get-FileHash "$data\attachments\audit-preservation.txt").Hash
$checks=New-Object 'System.Collections.Generic.List[string]'
$snapshots=New-Object 'System.Collections.Generic.List[object]'
$status=[ordered]@{state='running';phase='initial';pid=$PID;startedAt=(Get-Date).ToString('o');checks=@();snapshots=@();appInitiatedUpdate=$false;testHooks=$false}
function Save {$status.checks=@($checks.ToArray());$status.snapshots=@($snapshots.ToArray());$status | ConvertTo-Json -Depth 9 | Set-Content "$outputRoot\result.json" -Encoding UTF8}
function Assert-Data {
    if((Get-FileHash "$data\data\거래플랜.db").Hash -ne $dataHash -or (Get-FileHash "$data\attachments\audit-preservation.txt").Hash -ne $attachmentHash){throw 'Installer changed external business data.'}
}
function Assert-Payload($Files){
    [GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($target,[string[]]@($Files | ForEach-Object {$_.Path}),[long[]]@($Files | ForEach-Object {$_.Length}),[string[]]@($Files | ForEach-Object {$_.Sha256}))
    if(@(Get-ChildItem $target -File -Recurse).Count -ne @($Files).Count){throw 'Unexpected installed payload files.'}
}
function Assert-State([string]$Phase,[string]$Version,[string]$ProductCode,$Files){
    $product=[GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot($fixture.upgradeCode,$target)
    if($null -eq $product -or $product.Version -ne $Version -or [guid]$product.ProductCode -ne [guid]$ProductCode){throw 'Unexpected registered product.'}
    Assert-Payload $Files;Assert-Data
    $snapshots.Add([pscustomobject]@{phase=$Phase;product=$product;filesVerified=@($Files).Count;externalDbHash=$dataHash;attachmentHash=$attachmentHash});Save
}
function Run-Msi([string]$Arguments,[string]$Phase){
    $status.phase=$Phase;Save
    $p=Start-Process C:\Windows\System32\msiexec.exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    [void]$p.Handle;$status.msiPid=$p.Id;Save
    $p.WaitForExit()
    if($p.ExitCode -ne 0){throw ($Phase+' MSI returned '+$p.ExitCode)}
    $p.Dispose()
}
try {
    Save
    if((Get-FileHash "$inputRoot\old.msi").Hash -ine $fixture.oldMsiSha256){throw 'Old MSI input mismatch.'}
    if((Get-FileHash "$packageInput\Install-GeoraePlan.ps1").Hash -ine $fixture.installerSha256){throw 'Generated installer input mismatch.'}
    Run-Msi ('/i "'+$inputRoot+'\old.msi" /qn /norestart /l*v "'+$outputRoot+'\initial.log"') 'old-install'
    Assert-State 'old-installed' '1.1.713' $fixture.oldProductCode $fixture.oldFiles
    $checks.Add('full old product installed with original payload and registration');Save
    Copy-Item -LiteralPath $packageInput -Destination "$work\Package" -Recurse
    $package="$work\Package"
    $manifest=Get-Content "$package\desktop-payload.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $native=Get-Content "$package\Native\installer.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.Version -ne '1.1.720' -or $native.Version -ne '1.1.720' -or (Get-FileHash "$package\Native\installer.msi").Hash -ine $native.Sha256){throw 'New full product mismatch.'}
    $status.phase='generated-installer-upgrade';Save
    $args='-NoProfile -ExecutionPolicy Bypass -File "'+$package+'\Install-GeoraePlan.ps1" -InstallRoot "'+$target+'" -NoLaunch -SuppressUi -WorkerTimeoutSeconds 600 -LogPath "'+$outputRoot+'\upgrade.log"'
    $installer=Start-Process powershell.exe -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput "$outputRoot\upgrade.stdout" -RedirectStandardError "$outputRoot\upgrade.stderr"
    [void]$installer.Handle;$status.installerPid=$installer.Id;Save
    $installer.WaitForExit();$status.installerExit=$installer.ExitCode;Save
    if($installer.ExitCode -ne 0){throw ('Generated installer returned '+$installer.ExitCode)}
    $installer.Dispose()
    Assert-State 'upgraded' '1.1.720' $native.ProductCode $manifest.Files
    if($null -ne [GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($fixture.oldProductCode)){throw 'Old MSI registration remains.'}
    $pending=@(Get-ChildItem (Split-Path -Parent $target) -Directory -Force -Filter '.tradeplan-update-supervisor-state-*')
    if($pending.Count){throw 'Successful upgrade retained a pending journal.'}
    $checks.Add('generated installer upgraded full payload and MSI registration together');Save
    # The updater removes its extracted package after success. Retire this
    # source before repair so a surviving test directory cannot hide a broken
    # Windows Installer source list.
    $sourcePackage=Join-Path $work 'Package'
    $retiredPackage=Join-Path $work 'RetiredPackage'
    if([IO.Path]::GetFullPath($sourcePackage) -ne 'C:\FullNativeWork\Package' -or
        [IO.Path]::GetFullPath($retiredPackage) -ne 'C:\FullNativeWork\RetiredPackage' -or
        (Test-Path -LiteralPath $retiredPackage)){throw 'Unexpected source retirement paths.'}
    Move-Item -LiteralPath $sourcePackage -Destination $retiredPackage
    if(Test-Path -LiteralPath $sourcePackage){throw 'Original native source remains available.'}
    $status.originalNativeSourceRetired=$true;Save
    # A missing installed executable must be restored by the newly registered MSI.
    Move-Item -LiteralPath "$target\거래플랜.exe" -Destination "$work\preserved-720.exe"
    Run-Msi ('/fomus '+$native.ProductCode+' /qn /norestart /l*v "'+$outputRoot+'\repair.log"') 'cached-msi-repair'
    Assert-State 'repaired' '1.1.720' $native.ProductCode $manifest.Files
    if((Get-FileHash "$work\preserved-720.exe").Hash -ne (Get-FileHash "$target\거래플랜.exe").Hash){throw 'Repair restored an older executable.'}
    $checks.Add('cached MSI repair restored the new full executable without downgrade');Save
    $shell=New-Object -ComObject WScript.Shell
    $programs=[Environment]::GetFolderPath('CommonPrograms')
    $uninstallLink=Join-Path $programs '거래플랜\거래플랜 제거.lnk'
    if(-not(Test-Path $uninstallLink)){throw 'MSI removal shortcut missing.'}
    $shortcut=$shell.CreateShortcut($uninstallLink)
    if([IO.Path]::GetFileName($shortcut.TargetPath) -ine 'msiexec.exe' -or $shortcut.Arguments -notmatch [regex]::Escape($native.ProductCode)){throw 'Removal shortcut is not bound to the new MSI.'}
    $status.removalShortcut=@{target=$shortcut.TargetPath;arguments=$shortcut.Arguments};Save
    $env:GEORAEPLAN_APP_ROOT=$data;$env:GEORAEPLAN_TEMP_ROOT="$work\Temp";$env:GEORAEPLAN_DISABLE_LEGACY_MERGE='1'
    Copy-Item "$data\data\거래플랜.db" "$outputRoot\before-startup.db"
    $status.phase='new-app-startup';Save
    $app=Start-Process -FilePath "$target\거래플랜.exe" -WorkingDirectory $target -PassThru
    [void]$app.Handle;$status.appPid=$app.Id;$status.appStartUtc=$app.StartTime.ToUniversalTime().ToString('o');Save
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do {
        if($app.HasExited){throw ('Full new app exited during startup: '+$app.ExitCode)}
        $app.Refresh()
        if($app.MainWindowHandle -ne [IntPtr]::Zero -and $app.MainWindowTitle -eq '거래플랜 - 로그인'){break}
        if([DateTime]::UtcNow -ge $deadline){throw 'New app window was not observed; process remains live.'}
        Start-Sleep -Seconds 1
    }while($true)
    $status.appWindowTitle=$app.MainWindowTitle;$status.appPath=$app.Path;Save
    if(-not $app.CloseMainWindow() -or -not $app.WaitForExit(30000)){throw 'Owned test app did not close normally.'}
    $app.Dispose();$status.appClosedNormally=$true
    Copy-Item "$data\data\거래플랜.db" "$outputRoot\after-startup.db"
    foreach($suffix in @('-wal','-shm')){if(Test-Path "$data\data\거래플랜.db$suffix"){Copy-Item "$data\data\거래플랜.db$suffix" "$outputRoot\after-startup.db$suffix"}}
    if(Test-Path "$data\logs"){Copy-Item "$data\logs" "$outputRoot\app-logs" -Recurse}
    if(Test-Path "$data\backup"){Copy-Item "$data\backup" "$outputRoot\startup-backups" -Recurse}
    $checks.Add('full new app created a window and closed normally with isolated business data');Save
    $dataHash=(Get-FileHash "$data\data\거래플랜.db").Hash
    Run-Msi ('/x '+$native.ProductCode+' /qn /norestart /l*v "'+$outputRoot+'\uninstall.log"') 'uninstall'
    if($null -ne [GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot($fixture.upgradeCode,$target)){throw 'MSI registration survived removal.'}
    foreach($file in $manifest.Files){if(Test-Path (Join-Path $target $file.Path)){throw 'MSI owned payload survived removal.'}}
    if(Test-Path $uninstallLink){throw 'MSI removal shortcut survived removal.'}
    Assert-Data
    $checks.Add('new MSI uninstall removed registered payload and preserved external DB and attachment');Save
    $status.state='completed';$status.phase='finished';$status.finishedAt=(Get-Date).ToString('o');Save
    exit 0
}
catch {$status.state='failed';$status.error=$_.Exception.ToString();$status.finishedAt=(Get-Date).ToString('o');Save;exit 1}
