[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ExecutionRoot,
    [string]$MultiPcRoot,
    [switch]$ResetClientData,
    [switch]$LaunchServer,
    [switch]$LaunchClients
)

$scriptPath = Join-Path $PSScriptRoot '준비-다중PC-검증.ps1'
& $scriptPath @PSBoundParameters
exit $LASTEXITCODE
