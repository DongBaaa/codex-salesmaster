[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ChecklistPath,
    [string]$ChangedFilesPath,
    [string]$LogRoot,
    [string]$CommitMessage,
    [string]$Remote = 'origin',
    [string[]]$IncludeUntrackedPaths = @(),
    [switch]$SkipNas,
    [switch]$SkipGit,
    [switch]$SkipPush,
    [switch]$DryRun,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [string]$NasSshHost,
    [string]$NasSshUser,
    [int]$NasSshPort = 0,
    [string]$NasSshKeyPath,
    [string]$NasRemoteOpsPath
)

$scriptPath = Join-Path $PSScriptRoot 'Deploy-After-Test.ps1'
& $scriptPath @PSBoundParameters
exit $LASTEXITCODE

