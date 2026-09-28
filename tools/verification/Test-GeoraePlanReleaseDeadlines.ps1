[CmdletBinding()]
param([string]$ProjectRoot, [string]$OutputPath)
$ErrorActionPreference='Stop'
if(-not $ProjectRoot){$ProjectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path}
$rows=New-Object System.Collections.Generic.List[object]
function Check($Name,$Passed){
    $rows.Add([pscustomobject]@{Name=$Name;Passed=[bool]$Passed})
    if(-not $Passed){throw "Deadline check failed: $Name"}
}
function Parse-Script($Relative){
    $t=$null;$e=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $ProjectRoot $Relative),[ref]$t,[ref]$e)
    if($e.Count){throw "Parse failed: $Relative"}
    return $ast
}
$publisher=Parse-Script 'tools/linux/Publish-GeoraeplanLinuxPcRelease.ps1'
$deploy=Parse-Script '테스트 시행/Deploy-After-Test.ps1'
$wrapper=Parse-Script '테스트 시행/검증완료-반영.ps1'
foreach($pair in @(@('publisher',$publisher),@('deploy',$deploy),@('wrapper',$wrapper))){
    $binding=[scriptblock]::Create($pair[1].ParamBlock.Extent.Text+"`n[pscustomobject]@{Release=`$ReleaseHealthTimeoutSeconds;Rollback=`$RollbackHealthTimeoutSeconds}")
    $value=& $binding
    Check ($pair[0]+'-defaults') ($value.Release -eq 900 -and $value.Rollback -eq 900)
    $value=& $binding -ReleaseHealthTimeoutSeconds 37 -RollbackHealthTimeoutSeconds 81
    Check ($pair[0]+'-independent-budgets') ($value.Release -eq 37 -and $value.Rollback -eq 81)
    foreach($name in @('ReleaseHealthTimeoutSeconds','RollbackHealthTimeoutSeconds')){
        foreach($bad in @(0,3601)){
            $rejected=$false
            try { $values=@{};$values[$name]=$bad; & $binding @values | Out-Null } catch { $rejected=$true }
            Check ($pair[0]+'-'+$name+'-reject-'+$bad) $rejected
        }
    }
}
$functions=$publisher.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Assert-RemoteReleaseDeadlineSupport','Convert-ToSingleQuotedShellLiteral')},$true)
foreach($function in $functions){. ([scriptblock]::Create($function.Extent.Text))}
function Invoke-SshCommand {param($Config,$Command,[switch]$BatchMode)
    $script:lastCommand=$Command
    if($script:response -eq 'connection-failure'){throw 'fixture connection failure'}
    [pscustomobject]@{StdOut=$script:response;StdErr=''}
}
$config=[pscustomobject]@{RemoteOpsPath='/fixture/ops'}
foreach($script:response in @('georaeplan-release-health-deadlines-v1','legacy','', 'connection-failure')){
    $failed=$false
    try{Assert-RemoteReleaseDeadlineSupport -Config $config}catch{$failed=$true}
    Check ('capability-response-'+$script:response) ($failed -eq ($script:response -ne 'georaeplan-release-health-deadlines-v1'))
    Check ('capability-readonly-command-'+$script:response) ($script:lastCommand -ceq "/bin/bash '/fixture/ops/apply-release.sh' --capabilities")
}
$early=@($publisher.EndBlock.Statements|Where-Object {$_.Extent.Text.StartsWith('if ($MirrorToLive)') -and $_.Extent.Text.Contains('Assert-RemoteReleaseDeadlineSupport')})
$envRead=$publisher.EndBlock.Statements|Where-Object {$_.Extent.Text.StartsWith('$remoteEnv = Get-RemoteEnvMap')}
Check 'mandatory-capability-before-env-upload' ($early.Count -eq 1 -and $early[0].Extent.EndOffset -lt $envRead.Extent.StartOffset)
$apply=$publisher.Find({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$applyCommand'},$true)
$quotedOps="'/fixture/ops'";$quotedReleaseId="'reviewed-release'"
$ReleaseHealthTimeoutSeconds=37;$RollbackHealthTimeoutSeconds=81
. ([scriptblock]::Create($apply.Extent.Text))
Check 'actual-apply-command-deadlines' ($applyCommand -ceq "cd '/fixture/ops' && HEALTH_CHECK_TIMEOUT_SECONDS=37 ROLLBACK_HEALTH_TIMEOUT_SECONDS=81 /bin/bash ./apply-release.sh 'reviewed-release'")
$linuxArgs=@()
$forward=$deploy.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$linuxArgs' -and ($n.Extent.Text.Contains("'-ReleaseHealthTimeoutSeconds'") -or $n.Extent.Text.Contains("'-RollbackHealthTimeoutSeconds'"))},$true)
foreach($statement in $forward){. ([scriptblock]::Create($statement.Extent.Text))}
Check 'actual-deploy-argument-forwarding' (($linuxArgs -join '|') -ceq '-ReleaseHealthTimeoutSeconds|37|-RollbackHealthTimeoutSeconds|81')
# Run the real wrapper in an isolated directory with an inert child script.
$temp=Join-Path ([IO.Path]::GetTempPath()) ('georaeplan-deadline-wrapper-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp|Out-Null
try{
    Copy-Item -LiteralPath (Join-Path $ProjectRoot '테스트 시행/검증완료-반영.ps1') -Destination (Join-Path $temp 'wrapper.ps1')
    $stub=$deploy.ParamBlock.Extent.Text+"`n[pscustomobject]@{Release=`$ReleaseHealthTimeoutSeconds;Rollback=`$RollbackHealthTimeoutSeconds}|ConvertTo-Json -Compress`nexit 0"
    [IO.File]::WriteAllText((Join-Path $temp 'Deploy-After-Test.ps1'),$stub,[Text.UTF8Encoding]::new($true))
    $hostExe=(Get-Process -Id $PID).Path
    $result=& $hostExe -NoProfile -File (Join-Path $temp 'wrapper.ps1') -ReleaseHealthTimeoutSeconds 37 -RollbackHealthTimeoutSeconds 81
    if($LASTEXITCODE -ne 0){throw 'Wrapper fixture exited nonzero'}
    $value=$result|ConvertFrom-Json
    Check 'real-wrapper-forwarding' ($value.Release -eq 37 -and $value.Rollback -eq 81)
}finally{
    $resolved=[IO.Path]::GetFullPath($temp)
    $expected=Join-Path ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) 'georaeplan-deadline-wrapper-'
    if(-not $resolved.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe temporary cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$report=[pscustomobject]@{Passed=$rows.Count;Total=$rows.Count;Cases=$rows.ToArray();RealRemoteCommands=0;ProductionChanged=$false;PowerShellVersion=$PSVersionTable.PSVersion.ToString()}
if($OutputPath){$report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath -Encoding UTF8}
$report|ConvertTo-Json -Depth 5
