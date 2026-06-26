[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ChecklistPath,
    [string]$ChangedFilesPath,
    [string]$LogRoot,
    [string]$CommitMessage,
    [string]$Remote = 'origin',
    [string[]]$IncludeUntrackedPaths = @(),
    [switch]$SkipLinuxPc,
    [switch]$SkipGit,
    [switch]$SkipPush,
    [switch]$DryRun,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [string]$LinuxSshHost,
    [string]$LinuxSshUser,
    [int]$LinuxSshPort = 0,
    [string]$LinuxSshKeyPath,
    [string]$LinuxRemoteOpsPath,
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [switch]$AcceptRentalTemplateItemReferenceRisk,
    [string]$PreDeployBaseUrl = '',
    [string]$PreDeploySecretPath = '',
    [string]$PreDeployOutputDirectory = '',
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = '',
    [string]$PostDeploySecretPath = '',
    [string]$PostDeployOutputDirectory = '',
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @()
)

$scriptPath = Join-Path $PSScriptRoot 'Deploy-After-Test.ps1'
& $scriptPath @PSBoundParameters
exit $LASTEXITCODE
