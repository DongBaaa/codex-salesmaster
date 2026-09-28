[CmdletBinding()]
param([string]$PublisherPath, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (-not $PublisherPath) { $PublisherPath = Join-Path $PSScriptRoot '..\linux\Publish-GeoraeplanLinuxPcRelease.ps1' }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $PublisherPath).Path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Publisher parse failed'}
# Execute the actual apply/postflight branch; external commands are fixtures.
$block=$ast.Find({param($n)
    $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Extent.Text.StartsWith('if ($MirrorToLive)') -and
    $n.Extent.Text.Contains('$applyResult = Invoke-SshCommand')
},$true)
if(!$block){throw 'Apply branch missing'}
$allPrunes=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-LinuxPcRemotePrune'},$true))
if($allPrunes.Count -ne 2 -or @($allPrunes|Where-Object {$_.Extent.StartOffset -lt $block.Extent.StartOffset -or $_.Extent.EndOffset -gt $block.Extent.EndOffset}).Count){throw 'Prune exists outside verified apply branch'}
$param=$ast.ParamBlock.Parameters|Where-Object {$_.Name.VariablePath.UserPath -eq 'KeepReleaseCount'}
if($param.DefaultValue.Extent.Text -ne '0'){throw 'Retention must default to disabled'}
function Invoke-LinuxPcDiskPreflight { $script:events.Add('disk') }
function Assert-RemoteReleaseDeadlineSupport {
    $script:events.Add('capability')
    if($script:case -eq 'unsupported-capability'){throw 'fixture legacy script'}
}
function Convert-ToSingleQuotedShellLiteral {param($Value) return "'$Value'"}
function Invoke-SshCommand {
    $script:events.Add('apply')
    if($script:case -eq 'apply-failure'){throw 'fixture apply failed'}
    [pscustomobject]@{StdOut='';StdErr=''}
}
function Invoke-ReleaseOperationalGate {
    $script:events.Add('gate')
    if($script:case -eq 'gate-failure'){throw 'fixture gate failed'}
}
function Invoke-LinuxPcRemotePrune {param($Config,$RelativePath,$Pattern,$KeepCount,$Label)
    if($KeepCount -ne 2){throw 'Unexpected retention request'}
    $script:events.Add('prune:'+ $RelativePath)
}
$MirrorToLive=$true
$linuxConfig=[pscustomobject]@{RemoteRoot='/fixture';RemoteOpsPath='/fixture/ops';Host='fixture';User='fixture';Port=1}
$ReleaseId='fixture'
$ReleaseHealthTimeoutSeconds=900
$RollbackHealthTimeoutSeconds=900
$rows=@()
foreach($script:case in @('default','explicit','gate-skipped','apply-failure','gate-failure','unsupported-capability')){
    $script:events=New-Object System.Collections.Generic.List[string]
    $KeepReleaseCount=if($script:case -eq 'default'){0}else{2}
    $SkipPostDeployOperationalGate=[switch]($script:case -eq 'gate-skipped')
    $failed=$false
    try { & ([scriptblock]::Create($block.Extent.Text)) } catch { $failed=$true }
    $expected=switch($script:case){
        default {@('capability','disk','apply','gate')}
        explicit {@('capability','disk','apply','gate','prune:releases','prune:app/backups')}
        gate-skipped {@('capability','disk','apply')}
        apply-failure {@('capability','disk','apply')}
        gate-failure {@('capability','disk','apply','gate')}
        unsupported-capability {@('capability')}
    }
    $passed=($script:events -join '|') -ceq ($expected -join '|')
    $passed=$passed -and ($failed -eq ($script:case -in @('apply-failure','gate-failure','unsupported-capability')))
    $rows+= [pscustomobject]@{Case=$script:case;Passed=$passed;Events=@($script:events.ToArray());Failed=$failed}
}
$report=[pscustomobject]@{Passed=@($rows|Where-Object Passed).Count;Total=$rows.Count;Cases=$rows;PublisherSha256=(Get-FileHash $PublisherPath).Hash;RealRemoteCommands=0}
if($OutputPath){$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath -Encoding UTF8}
$report|ConvertTo-Json -Depth 6
if(@($rows|Where-Object {-not $_.Passed}).Count){throw 'Retention fixture failed'}
