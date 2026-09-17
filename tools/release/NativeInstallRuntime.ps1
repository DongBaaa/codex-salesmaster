# Embedded by the package builder; no runtime dot-sourcing from writable media.
function Initialize-NativeInstallRuntime {
    if (-not ('GeoraePlanInstaller.NativeMsiRuntime' -as [type])) {
        Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('__NATIVE_MSI_RUNTIME_B64__'))) -ErrorAction Stop
    }
}

function Get-NativeInstalledProduct {
    param([string]$Root)
    Initialize-NativeInstallRuntime
    return [GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot(
        '0E5C8E78-44C0-4585-A2E9-5E74071A3A11', $Root)
}

function Enter-NativeEngineBarrier {
    Initialize-NativeInstallRuntime
    while ($true) {
        $transaction = [GeoraePlanInstaller.NativeMsiRuntime]::TryBegin()
        if ($null -ne $transaction) { return $transaction }
        Write-InstallLog 'Windows Installer is busy; retaining the installation gate and recovery journal.'
        Start-Sleep -Seconds 5
    }
}

function Assert-NativePayloadDescriptor {
    param($Files, [string]$Root)
    if (@($Files).Count -eq 0) { throw 'Native payload manifest is empty.' }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $canonical = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($file in @($Files)) {
        $relative = [string]$file.Path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Contains(':') -or $relative.Contains('"') -or
            -not $seen.Add($relative.Replace('\', '/')) -or
            ([string]$file.Sha256 -notmatch '^[a-fA-F0-9]{64}$') -or [long]$file.Length -lt 0) {
            throw 'Invalid native payload descriptor.'
        }
        foreach ($segment in $relative.Replace('\', '/').Split('/')) {
            if ($segment -in @('', '.', '..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ')) {
                throw 'Invalid native payload path segment.'
            }
        }
        if (-not [IO.Path]::GetFullPath((Join-Path $Root $relative)).StartsWith($canonical, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Native payload path escapes the installation root.'
        }
    }
}

function Assert-NativeInstalledPayload {
    param($Files, [string]$Root)
    Assert-NativePayloadDescriptor -Files $Files -Root $Root
    Initialize-NativeInstallRuntime
    [GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($Root,
        [string[]]@($Files | ForEach-Object { [string]$_.Path }),
        [long[]]@($Files | ForEach-Object { [long]$_.Length }),
        [string[]]@($Files | ForEach-Object { [string]$_.Sha256 }))
}

function Assert-NativeJournalBinding {
    param($Journal)
    if ([int]$Journal.FormatVersion -ne 3) {
        if ($Journal.PSObject.Properties.Name -contains 'NativeInstall' -and $null -ne $Journal.NativeInstall) {
            throw 'Native installation cannot use a file-only recovery journal.'
        }
        return
    }
    $native = $Journal.NativeInstall
    if ($null -eq $native -or [int]$native.SchemaVersion -ne 1 -or
        [guid]$native.UpgradeCode -ne [guid]'0E5C8E78-44C0-4585-A2E9-5E74071A3A11' -or
        [guid]$native.OldProductCode -eq [guid]::Empty -or [guid]$native.NewProductCode -eq [guid]::Empty -or
        -not (Test-SameSupervisorPath -Left $native.InstallRoot -Right $Journal.InstallRoot) -or
        [version]$native.NewVersion -lt [version]$native.OldVersion -or
        [string]$native.MsiSha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$native.MsiLength -le 0) {
        throw 'Native journal product binding is invalid.'
    }
    if ([guid]$native.OldProductCode -eq [guid]$native.NewProductCode) {
        throw 'Native upgrade journal requires distinct product identities.'
    }
    if ([bool]$Journal.ShortcutRepair.LegacyBridgeCopy -or [bool]$Journal.ShortcutRepair.RemoveLegacyApplicationShortcuts) {
        $legacySnapshots = @($Journal.Snapshots | Where-Object { $_.Label -ceq 'legacy' })
        if ($legacySnapshots.Count -ne 1 -or
            -not (Test-SameSupervisorPath -Left $legacySnapshots[0].Path -Right $Journal.ShortcutRepair.LegacyInstallRoot) -or
            (Test-SameSupervisorPath -Left $Journal.InstallRoot -Right $Journal.ShortcutRepair.LegacyInstallRoot)) {
            throw 'Native legacy mutation lacks an independent, bound rollback snapshot.'
        }
    }
    Assert-NativePayloadDescriptor -Files $native.Files -Root $Journal.InstallRoot
}

function Get-NativeInstallPlan {
    param([string]$PackageRoot, [string]$Root)
    $installed = Get-NativeInstalledProduct -Root $Root
    if ($null -eq $installed) { return $null }
    $nativePath = Join-Path $PackageRoot 'Native\installer.json'
    $msiPath = Join-Path $PackageRoot 'Native\installer.msi'
    $payloadPath = Join-Path $PackageRoot 'desktop-payload.json'
    foreach ($path in @($nativePath, $msiPath, $payloadPath)) { Assert-NoReparsePoints -Path $path }
    $lease = [IO.File]::Open($msiPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $metadata = Get-Content -LiteralPath $nativePath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $payload = Get-Content -LiteralPath $payloadPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json
        $identity = Get-DesktopMsiIdentity -Path $msiPath
        if ([int]$metadata.SchemaVersion -ne 1 -or [int]$payload.SchemaVersion -ne 1 -or
            $metadata.Path -cne 'Native/installer.msi' -or $metadata.Version -cne $ExpectedVersion -or
            $payload.Version -cne $ExpectedVersion -or $identity.ProductVersion -cne $ExpectedVersion -or
            [guid]$identity.UpgradeCode -ne [guid]'0E5C8E78-44C0-4585-A2E9-5E74071A3A11' -or
            [guid]$identity.ProductCode -ne [guid]$metadata.ProductCode -or
            [guid]$identity.UpgradeCode -ne [guid]$metadata.UpgradeCode -or
            $lease.Length -ne [long]$metadata.Length -or
            (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash -ne $metadata.Sha256 -or
            (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash -ne $metadata.PayloadManifestSha256) {
            throw 'Native package identity or content differs from its manifest.'
        }
        Assert-NativeInstalledPayload -Files $payload.Files -Root (Join-Path $PackageRoot 'App')
        # These modes require an explicit native contract before enabling them.
        # Fail before snapshots or worker mutations instead of silently using ZIP.
        if ($NoShortcuts) { throw 'Native NoShortcuts preservation is not yet supported by this installer.' }
        if ([guid]$installed.ProductCode -eq [guid]$identity.ProductCode) {
            throw 'Same-product native repair is not yet supported by this installer.'
        }
        $plan = [pscustomobject]@{
            SchemaVersion = 1; UpgradeCode = $identity.UpgradeCode; InstallRoot = $Root
            OldProductCode = $installed.ProductCode; OldVersion = $installed.Version
            NewProductCode = $identity.ProductCode; NewVersion = $identity.ProductVersion
            MsiSha256 = $metadata.Sha256; MsiLength = $metadata.Length
            Files = @($payload.Files)
        }
        # Product and payload checks precede snapshot creation. Legacy binding
        # is checked by the complete journal after its descriptors exist.
        if ([version]$plan.NewVersion -lt [version]$plan.OldVersion) { throw 'Native downgrade is not permitted.' }
        return $plan
    }
    finally { $lease.Dispose() }
}

function Get-NativeRecoveryOutcome {
    param($Native)
    $old = [GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($Native.OldProductCode)
    $new = [GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($Native.NewProductCode)
    $atRoot = Get-NativeInstalledProduct -Root $Native.InstallRoot
    if ($null -ne $new -and $null -eq $old -and $null -ne $atRoot -and
        [guid]$atRoot.ProductCode -eq [guid]$Native.NewProductCode -and $new.Version -ceq $Native.NewVersion -and
        (Test-SameSupervisorPath -Left $new.InstallRoot -Right $Native.InstallRoot)) { return 'Committed' }
    if ($null -ne $old -and $null -eq $new -and $null -ne $atRoot -and
        [guid]$atRoot.ProductCode -eq [guid]$Native.OldProductCode -and $old.Version -ceq $Native.OldVersion -and
        (Test-SameSupervisorPath -Left $old.InstallRoot -Right $Native.InstallRoot)) { return 'RolledBack' }
    throw 'Native product state is indeterminate; preserving files, journal and installation gate.'
}

function Assert-NativeLegacyState {
    param($Journal)
    if ([bool]$Journal.ShortcutRepair.LegacyBridgeCopy) {
        Assert-NativeInstalledPayload -Files $Journal.NativeInstall.Files -Root $Journal.ShortcutRepair.LegacyInstallRoot
    }
    elseif ([bool]$Journal.ShortcutRepair.RemoveLegacyApplicationShortcuts -and
        (Test-SupervisorPathExistsFailClosed -Path $Journal.ShortcutRepair.LegacyInstallRoot)) {
        throw 'Native migration still contains the previous legacy installation.'
    }
}

function Invoke-NativeJournalRecovery {
    param($Journal, [string]$JournalPath)
    Assert-NativeJournalBinding -Journal $Journal
    $barrier = Enter-NativeEngineBarrier
    try {
        $outcome = Get-NativeRecoveryOutcome -Native $Journal.NativeInstall
        if ($outcome -eq 'Committed') {
            Assert-NativeInstalledPayload -Files $Journal.NativeInstall.Files -Root $Journal.InstallRoot
            Assert-NativeLegacyState -Journal $Journal
            $Journal.Phase = 'ShortcutRepairPending'
            Write-SupervisorJournal -Journal $Journal -JournalPath $JournalPath
            try {
                Invoke-PendingShortcutRepair -Repair $Journal.ShortcutRepair -NativeProductCode $Journal.NativeInstall.NewProductCode
            }
            catch {
                throw (New-ShortcutRepairPendingException -Message 'Committed native installation needs shortcut repair.' -InnerException $_.Exception)
            }
            $Journal.Phase = 'CommittedCleanupPending'
            Write-SupervisorJournal -Journal $Journal -JournalPath $JournalPath
            Remove-CompletedSupervisorState -Journal $Journal -JournalPath $JournalPath
            if (Test-SameSupervisorPath -Left $Journal.InstallRoot -Right $InstallRoot) {
                $script:NativeRecoveryCommitted = $true
            }
        }
        else {
            if ($Journal.Phase -in @('ShortcutRepairPending', 'CommittedCleanupPending')) {
                throw 'A committed native journal no longer matches MSI registration; refusing file rollback.'
            }
            if ($Journal.Phase -ne 'RestoredCleanupPending') {
                $Journal.Phase = 'Recovering'
                Write-SupervisorJournal -Journal $Journal -JournalPath $JournalPath
                foreach ($snapshot in @($Journal.Snapshots)) { Restore-InstallRollbackSnapshot -Snapshot $snapshot }
            }
            foreach ($snapshot in @($Journal.Snapshots)) { Assert-RestoredInstallRollbackSnapshot -Snapshot $snapshot }
            $Journal.Phase = 'RestoredCleanupPending'
            Write-SupervisorJournal -Journal $Journal -JournalPath $JournalPath
            Remove-CompletedSupervisorState -Journal $Journal -JournalPath $JournalPath
        }
    }
    finally { $barrier.Dispose() }
}

function Invoke-NativeInstallerTestCheckpoint {
    param([ValidateSet('BeforeCommit','AfterCommit')][string]$Phase, [string]$PackageRoot)
    if ($env:GEORAEPLAN_INSTALLER_TEST_NATIVE_CHECKPOINT -cne $Phase) { return }
    # Revalidate the package nonce and unprotected fixture root at the actual
    # mutation boundary; a production package cannot activate this checkpoint.
    Assert-InstallerTestHooksAllowed
    $process = [Diagnostics.Process]::GetCurrentProcess()
    $marker = Join-Path $PackageRoot ('.native-test-' + $Phase + '.json')
    $bytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        phase=$Phase; pid=$PID; processPath=$process.MainModule.FileName
        startUtcTicks=$process.StartTime.ToUniversalTime().Ticks
        supervisorPid=$WorkerStartServerProcessId
    } | ConvertTo-Json))
    $stream = [IO.FileStream]::new($marker, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose(); $process.Dispose() }
    Write-InstallLog ('Native test checkpoint: ' + $Phase)
    while ($true) { Start-Sleep -Milliseconds 250 }
}

function Invoke-NativeInstallWorker {
    param($Journal, [string]$PackageRoot)
    Assert-NativeJournalBinding -Journal $Journal
    # Workers run in a fresh process; supervisor-loaded types are not inherited.
    Initialize-NativeInstallRuntime
    $native = $Journal.NativeInstall
    $path = Join-Path $PackageRoot 'Native\installer.msi'
    Assert-NoReparsePoints -Path $path
    $lease = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $transaction = $null; $previousUI = $null; $cacheLease = $null
    try {
        if ($lease.Length -ne [long]$native.MsiLength -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $native.MsiSha256) {
            throw 'Native MSI changed after journal creation.'
        }
        $cacheLease = [GeoraePlanInstaller.NativeMsiRuntime]::AcquireCachedPackage(
            $path, $native.NewProductCode, $native.MsiSha256, [long]$native.MsiLength)
        $cachedPath = $cacheLease.Path
        if ($cacheLease.Length -ne [long]$native.MsiLength -or
            (Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash -ne $native.MsiSha256) {
            throw 'Durable MSI source changed before installation.'
        }
        Write-InstallLog ('Durable native repair source verified: ' + $cachedPath)
        $transaction = Enter-NativeEngineBarrier
        if ((Get-NativeRecoveryOutcome -Native $native) -ne 'RolledBack') { throw 'Native registration changed before worker installation.' }
        $previousUI = [GeoraePlanInstaller.NativeMsiRuntime]::SetSilentUI()
        $transaction.Install($cachedPath, $Journal.InstallRoot, $true)
        Assert-NativeInstalledPayload -Files $native.Files -Root $Journal.InstallRoot
        if ((Get-NativeRecoveryOutcome -Native $native) -ne 'Committed') { throw 'Native installation did not register the expected product.' }
        if ([bool]$Journal.ShortcutRepair.LegacyBridgeCopy) {
            Invoke-RobocopyMirror -Source (Join-Path $PackageRoot 'App') -Destination $Journal.ShortcutRepair.LegacyInstallRoot
        }
        elseif ([bool]$Journal.ShortcutRepair.RemoveLegacyApplicationShortcuts -and
            (Test-SupervisorPathExistsFailClosed -Path $Journal.ShortcutRepair.LegacyInstallRoot)) {
            # The validated legacy snapshot is still durable. This mutation is
            # inside the MSI transaction, before its commit decision.
            Assert-NoReparsePoints -Path $Journal.ShortcutRepair.LegacyInstallRoot
            [void](Get-InstallTreeManifest -Path $Journal.ShortcutRepair.LegacyInstallRoot)
            Remove-Item -LiteralPath $Journal.ShortcutRepair.LegacyInstallRoot -Recurse -Force -ErrorAction Stop
        }
        Assert-NativeLegacyState -Journal $Journal
        Invoke-NativeInstallerTestCheckpoint -Phase BeforeCommit -PackageRoot $PackageRoot
        $transaction.Commit()
        Invoke-NativeInstallerTestCheckpoint -Phase AfterCommit -PackageRoot $PackageRoot
    }
    finally {
        try { if ($null -ne $transaction) { $transaction.Dispose() } }
        finally {
            if ($null -ne $previousUI) { [GeoraePlanInstaller.NativeMsiRuntime]::RestoreUI($previousUI) }
            if ($null -ne $cacheLease) { $cacheLease.Dispose() }
            $lease.Dispose()
        }
    }
}
