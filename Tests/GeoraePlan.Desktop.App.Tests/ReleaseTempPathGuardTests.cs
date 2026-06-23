using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ReleaseTempPathGuardTests
{
    [Fact]
    public void DesktopAppPaths_PrefersDDriveTempAndOverridesProcessTempVariables()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "AppPaths.cs");

        Assert.Contains("private const string TempRootOverrideEnvironmentKey = \"GEORAEPLAN_TEMP_ROOT\";", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", TempRoot);", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", TempRoot);", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.Combine(_base, \"temp\")");
    }

    [Fact]
    public void DesktopUpdater_UsesAppTempDirectoryForDownloadedAndPreparedArtifacts()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");

        Assert.Contains("directoryPath = AppPaths.TempDir;", source, StringComparison.Ordinal);
        Assert.Contains("var tempRoot = AppPaths.TempDir;", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppPaths.TempDir, \"GeoraePlan\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagingScripts_PreferProjectOrDDriveTempBeforeSystemTempFallback()
    {
        var initializeTempSource = ReadRepositoryFile(
            "tools",
            "common",
            "Initialize-GeoraePlanTemp.ps1");

        Assert.Contains("$env:GEORAEPLAN_TEMP_ROOT = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TEMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$effectiveProjectRoot = $ProjectRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path 'D:\\", initializeTempSource, StringComparison.Ordinal);
        AssertInOrder(
            initializeTempSource,
            "Join-Path $effectiveProjectRoot 'temp'",
            "$env:TEMP");

        var updateAssetsSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", updateAssetsSource, StringComparison.Ordinal);

        var desktopInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopNativeInstallers.ps1");

        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        AssertInOrder(
            desktopInstallerSource,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.GetTempPath()");

        var androidBuildSource = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", androidBuildSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", androidBuildSource, StringComparison.Ordinal);
        AssertInOrder(
            androidBuildSource,
            "$ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "$resolvedDotNetPath = Get-ResolvedDotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath",
            "& $resolvedDotNetPath @arguments");
    }

    [Fact]
    public void AndroidBuildScript_DisableAotOverridesProjectAotDefaults()
    {
        var source = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("$DisableAot.IsPresent", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:RunAOTCompilation=false'", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:AndroidEnableProfiledAot=false'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$shouldEnableAot = $isReleaseBuild -and -not $DisableAot.IsPresent",
            "$arguments += '-p:RunAOTCompilation=true'",
            "elseif ($DisableAot.IsPresent)",
            "$arguments += '-p:RunAOTCompilation=false'",
            "$shouldDisableTrimming = $DisableTrimming.IsPresent");
    }

    [Fact]
    public void OperationalGate_ValidatesUpdatePackageHeadAndGetHeadersWithoutDownloadingPackages()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("function Invoke-UpdatePackageHeaderProbe", source, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead", source, StringComparison.Ordinal);
        Assert.Contains("function Test-UpdatePackageDownloadHeaders", source, StringComparison.Ordinal);
        Assert.Contains("HEAD Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("GET Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("manifest fileSize", source, StringComparison.Ordinal);
        Assert.Contains("update-downloads.md", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentLength.HasValue", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$manifest = Invoke-TextProbe",
            "$updateDownloadReportPath = Join-Path $OutputDirectory 'update-downloads.md'",
            "Add-Check -Checks $checks -Name 'update package downloads'",
            "$liveObservationScript = Join-Path $resolvedRoot");
    }

    [Fact]
    public void OperationalGate_ChecksReadinessBeforeManifestAndDatabaseDependentChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("Add-Check -Checks $checks -Name 'live healthz'", source, StringComparison.Ordinal);
        Assert.Contains("Add-Check -Checks $checks -Name 'live readyz'", source, StringComparison.Ordinal);
        Assert.Contains("readyz status={0} error={1} body={2}", source, StringComparison.Ordinal);
        Assert.Contains("function Test-ReadyProbeSemantic", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-ReadyProbeWithRetry", source, StringComparison.Ordinal);
        Assert.Contains("readyz attempt={0} semantic={1}", source, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds $DelaySec", source, StringComparison.Ordinal);
        Assert.Contains("$status -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("$dbStarted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbCompleted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbFailed -eq $false", source, StringComparison.Ordinal);
        Assert.Contains("200 OK but readiness body is not ready", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$health = Invoke-TextProbe -Uri ($BaseUrl + '/healthz')",
            "$readyProbeResult = Invoke-ReadyProbeWithRetry -Uri ($BaseUrl + '/readyz') -LogPath $logPath",
            "$readySemanticResult = $readyProbeResult.SemanticResult",
            "$manifest = Invoke-TextProbe -Uri ($BaseUrl + \"/updates/manifest?channel=$Channel\")");
    }

    [Fact]
    public void RentalTemplateCandidateExportScript_IsSelectOnlyAndRedactsSensitiveRowsByDefault()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1");

        Assert.Contains("[switch]$IncludeSensitiveCandidateRows", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(") to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifacts\\rental-template-item-reference-candidates", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"ProfileKey\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CustomerName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"DisplayItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"OriginalItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("single_active_item_from_included_assets", source, StringComparison.Ordinal);
        Assert.Contains("ambiguous_multiple_candidates", source, StringComparison.Ordinal);
        Assert.Contains("proposed_item_id as \"ProposedItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_source as \"ProposedSource\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_confidence as \"ProposedConfidence\"", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemCount", source, StringComparison.Ordinal);
        Assert.Contains("review_required_asset_based", source, StringComparison.Ordinal);
        Assert.Contains("At least one database name is required.", source, StringComparison.Ordinal);
        Assert.Contains("([string]$_) -split ','", source, StringComparison.Ordinal);
        Assert.Contains("function Get-ManualReviewDetailSql", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("name_or_identifier_candidate", source, StringComparison.Ordinal);
        Assert.Contains("included_asset_item_candidate", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CandidateItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("CandidateStatus", source, StringComparison.Ordinal);
        Assert.Contains("DistinctCandidateItemCount", source, StringComparison.Ordinal);

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateItemReferenceGate_BlocksUnresolvedCandidatesWithReadOnlyExport()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateItemReferenceGate.ps1");

        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("summary-by-database.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_item_reference_gate_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Unresolved rental billing template item references remain", source, StringComparison.Ordinal);
        Assert.Contains("'-Databases', ($Databases -join ',')", source, StringComparison.Ordinal);
        Assert.Contains("AllowUnresolved", source, StringComparison.Ordinal);

        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxPcReleaseRunsRentalTemplateItemReferenceGateWithOperationalGate()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("Test-GeoraePlanRentalTemplateItemReferenceGate.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-RentalTemplateItemReferenceGate", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_start", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_done", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data_rental_template_item_reference_gate=skipped risk=accepted", source, StringComparison.Ordinal);
        Assert.Contains("known operating data candidates are intentionally excluded", source, StringComparison.Ordinal);
        Assert.Contains("'-RemoteOpsDirectory', $script:LinuxRemoteOpsPath", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$rentalTemplateItemReferenceGateScript = Join-Path $Root 'tools\\linux\\Test-GeoraePlanRentalTemplateItemReferenceGate.ps1'",
            "& powershell @rentalTemplateItemReferenceGateArgs");
        AssertInOrder(
            source,
            "function Invoke-RentalTemplateItemReferenceGate",
            "function Update-PublishedAppSettings");
        AssertInOrder(
            source,
            "$resolvedPreDeploySecretPath =",
            "if ($MirrorToLive -and -not $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "Invoke-RentalTemplateItemReferenceGate `",
            "elseif ($MirrorToLive -and $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent)");
    }

    [Fact]
    public void LinuxPcReleaseSshCommandPreservesRemoteCommandQuotingAndFailsPrunePipelines()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Invoke-SshCommand", source, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.ProcessStartInfo]::new($sshExe)", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.RedirectStandardOutput = $true", source, StringComparison.Ordinal);
        Assert.Contains("$process.Start()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $sshExe -ArgumentList $arguments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$startInfo.ArgumentList.Add($argument)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "function Invoke-SshCommand",
            "[System.Diagnostics.ProcessStartInfo]::new($sshExe)",
            "$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '",
            "$process.Start()");

        Assert.Contains("set -o pipefail", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "set -e",
            "set -o pipefail",
            "find \"`$real_root\" -mindepth 1 -maxdepth 1 -type d -name \"`$pattern\"");
    }

    [Fact]
    public void LinuxPcReleaseChecksDiskFreeSpaceAfterPruneBeforeUpload()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("[int64]$MinimumLinuxFreeBytes", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-LinuxPcDiskPreflight", source, StringComparison.Ordinal);
        Assert.Contains("$minimumFreeKilobytes = [int64][Math]::Ceiling($MinimumFreeBytes / 1024.0)", source, StringComparison.Ordinal);
        Assert.Contains("df -Pk \"`$path\"", source, StringComparison.Ordinal);
        Assert.Contains("minimum_kb=$minimumFreeKilobytes", source, StringComparison.Ordinal);
        Assert.Contains("if [ \"`$available_kb\" -lt \"`$minimum_kb\" ]; then", source, StringComparison.Ordinal);
        Assert.Contains("linux_pc_disk_preflight_ok", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC free disk space is below the required threshold", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups'",
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases'",
            "Invoke-LinuxPcDiskPreflight -Config $linuxConfig -Path $linuxConfig.RemoteRoot -MinimumFreeBytes $MinimumLinuxFreeBytes -Label 'pre-upload'",
            "Write-Host \"linux_pc_upload_start");
    }

    [Fact]
    public void PreLiveVerificationUsesLinuxPcUpdateManifestStepLabels()
    {
        var source = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanPreLiveVerification.ps1");

        Assert.Contains("function Invoke-LinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("SkipLinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-Step -Name 'linux-pc-update-manifest-check'", source, StringComparison.Ordinal);
        Assert.Contains("Add-StepResult -Name 'linux-pc-update-manifest-check' -Passed $true -Detail 'SKIP'", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC update manifest 확인", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nas-update-manifest-check", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullReleaseForwardsExplicitRentalTemplateRiskAcceptanceToLinuxDeploy()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("if ($AcceptRentalTemplateItemReferenceRisk)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AcceptRentalTemplateItemReferenceRisk",
            "if ($AcceptRentalTemplateItemReferenceRisk)",
            "$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'");
    }

    [Fact]
    public void FullReleaseForwardsAndroidAotAndTrimmingOverridesToApkBuild()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$DisableAndroidAot", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$DisableAndroidTrimming", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidAot)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableAot'", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidTrimming)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableTrimming'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$androidArgs = @(",
            "if ($DisableAndroidAot)",
            "$androidArgs += '-DisableAot'",
            "if ($DisableAndroidTrimming)",
            "$androidArgs += '-DisableTrimming'",
            "& powershell @androidArgs");
    }

    [Fact]
    public void RentalTemplateRepairPlanScript_GeneratesRollbackPatchOnlyAfterSelectValidation()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1");

        Assert.Contains("[switch]$ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchMode = 'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("review-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings.normalized.csv", source, StringComparison.Ordinal);
        Assert.Contains("validation-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved_item_not_found", source, StringComparison.Ordinal);
        Assert.Contains("current_reference_is_valid_now", source, StringComparison.Ordinal);
        Assert.Contains("ValidationStatus -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemId = Get-CsvValue -Row $row -Names @('ProposedItemId')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedSource = Get-CsvValue -Row $row -Names @('ProposedSource')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedConfidence = Get-CsvValue -Row $row -Names @('ProposedConfidence')", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = Get-CsvValue -Row $row -Names @('ApprovedItemId', 'NewItemId', 'TargetItemId')", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedApprovedMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedReadyMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Approved mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Ready mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedReadyMappingCount requires -ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("repair_plan_gate_status=$repairPlanGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("Repair plan gate failed", source, StringComparison.Ordinal);
        Assert.Contains("create temporary table \"RentalBillingTemplateItemReferenceRepairCounts\" on commit drop as", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("select * from \"RentalBillingTemplateItemReferenceRepairCounts\"", source, StringComparison.Ordinal);
        Assert.Contains("transaction-time assertions for approved, target profile, backup, and updated profile counts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@('ApprovedItemId', 'ProposedItemId'", source, StringComparison.Ordinal);
        Assert.Contains("repair-<db>-rollback.sql", source, StringComparison.Ordinal);
        Assert.Contains("Run this SQL against a cloned/test database first.", source, StringComparison.Ordinal);
        Assert.Contains("$terminalStatement = if ($Mode -eq 'Commit') { 'commit;' } else { 'rollback;' }", source, StringComparison.Ordinal);
        Assert.Contains("patch_sql=none", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$csvText = Invoke-RemotePsqlCsv -Database $database -Sql $sql",
            "Where-Object { $_.ValidationStatus -eq 'ready' }",
            "$patchSql = New-PatchSql -Database $database");

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateRepairReadinessGate_ChainsApprovalAndSelectOnlyRepairPlanChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateRepairReadiness.ps1");

        Assert.Contains("New-GeoraePlanRentalTemplateApprovalIntakePack.ps1", source, StringComparison.Ordinal);
        Assert.Contains("New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("-RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("-ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedApprovedMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedReadyMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipCurrentCandidateKeyCheck", source, StringComparison.Ordinal);
        Assert.Contains("-PatchMode", source, StringComparison.Ordinal);
        Assert.Contains("'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("candidate-rows.csv", source, StringComparison.Ordinal);
        Assert.Contains("current-candidates", source, StringComparison.Ordinal);
        Assert.Contains("current-candidate-key-mismatches.csv", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-repair-readiness-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("Current unresolved candidate count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Approval mapping keys do not match current unresolved candidate keys", source, StringComparison.Ordinal);
        Assert.Contains("current_candidate_missing_from_approval", source, StringComparison.Ordinal);
        Assert.Contains("approval_key_not_in_current_candidates", source, StringComparison.Ordinal);
        Assert.Contains("Repair readiness gate failed", source, StringComparison.Ordinal);
        Assert.Contains("this script never executes SQL patches", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_repair_readiness_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is not rollback-only", source, StringComparison.Ordinal);
        Assert.Contains("Generated readiness SQL must not contain a standalone commit statement", source, StringComparison.Ordinal);
        Assert.Contains("do $repair_assert$", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is missing required safety assertion fragment", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "approval-intake-require-all",
            "current-candidate-key-check",
            "repair-plan-select-ready");

        Assert.DoesNotContain("PatchMode', 'Commit'", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateManualReviewPackScript_DoesNotPrefillApprovalsAndKeepsOutputLocal()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateManualReviewPack.ps1");

        Assert.Contains("manual-review-decision-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-option-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-decision-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("CandidateOptionCount", source, StringComparison.Ordinal);
        Assert.Contains("Option${optionNumber}ItemId", source, StringComparison.Ordinal);
        Assert.Contains("ManualReviewPriority", source, StringComparison.Ordinal);
        Assert.Contains("P1_asset_multi_small", source, StringComparison.Ordinal);
        Assert.Contains("choose_one_active_asset_item", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateApprovalIntakeScript_ClearsDryRunApprovalsAndValidatesFilledRowsLocally()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateApprovalIntakePack.ps1");

        Assert.Contains("approval-intake-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("proposed_ready_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("manual_review_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);
        Assert.Contains("OriginalReviewDecision", source, StringComparison.Ordinal);
        Assert.Contains("OriginalApprovedItemId", source, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovalDecision", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("validate_existing_approval_intake", source, StringComparison.Ordinal);
        Assert.Contains("Dry-run/system reviewer markers cannot be used as business approval.", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId is not in suggested/candidate option ids.", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Duplicate Database/ProfileId/TemplateOrdinal keys were found in approval intake rows", source, StringComparison.Ordinal);
        Assert.Contains("approved_input_valid", source, StringComparison.Ordinal);
        Assert.Contains("pending_approval", source, StringComparison.Ordinal);
        Assert.Contains("invalid_approval_input", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation-status-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("approval_input_gate_status=$approvalInputGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("valid approved rows for follow-up SELECT-only validation", source, StringComparison.Ordinal);
        Assert.Contains("Approval intake gate failed", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static void AssertInOrder(string source, params string[] tokens)
    {
        var previousIndex = -1;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(token, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Token was not found: {token}");
            Assert.True(index > previousIndex, $"Token was out of order: {token}");
            previousIndex = index;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
