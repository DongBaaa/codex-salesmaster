[CmdletBinding()]
param(
    [string]$LinuxSshHost = '192.168.0.199',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshUser = 'itw',
    [string]$LinuxSshKeyPath = (
        Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteOpsDirectory = '/srv/georaeplan/ops',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

function Resolve-SshExecutable {
    $windowsSsh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsSsh -PathType Leaf) {
        return $windowsSsh
    }

    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $ssh) {
        return $ssh.Source
    }

    throw 'ssh executable was not found.'
}

function ConvertTo-Base64Utf8 {
    param([Parameter(Mandatory = $true)][string]$Value)

    return [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(($Value -replace "`r`n", "`n")))
}

function Get-PreparationSnapshotValidationFunctionSource {
    param([Parameter(Mandatory = $true)][string]$PreparationScriptPath)

    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $PreparationScriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw 'The test-environment preparation script cannot be parsed.'
    }

    $requiredFunctions = @(
        'Initialize-TestEnvironmentFinalPathNativeMethods',
        'ConvertTo-NormalizedFullPath',
        'Get-FinalExistingPath',
        'Resolve-PhysicalPathIdentity',
        'Get-SourceUsersSnapshotKnownPermissions',
        'Get-SourceUsersSnapshotTextSha256',
        'Get-SourceUsersSnapshotOrdinalSortKey',
        'ConvertTo-SourceUsersSnapshotCanonicalJsonString',
        'Get-SourceUsersSnapshotCanonicalJson',
        'Get-SourceUsersSnapshotScopeCounts',
        'Get-SourceUsersSnapshotFileSystemAcl',
        'Set-SourceUsersSnapshotDirectoryAcl',
        'Assert-SourceUsersSnapshotAcl',
        'Import-SourceUsersSnapshot'
    )
    $definitions = @(
        $ast.FindAll(
            {
                param($node)
                $node -is
                    [Management.Automation.Language.FunctionDefinitionAst] -and
                $requiredFunctions -contains $node.Name
            },
            $true)
    )
    foreach ($functionName in $requiredFunctions) {
        if (@($definitions | Where-Object Name -eq $functionName).Count -ne 1) {
            throw (
                'A required snapshot validation function is missing or ' +
                'duplicated.')
        }
    }

    $functionSource = (
        $requiredFunctions |
            ForEach-Object {
                $functionName = $_
                (
                    $definitions |
                        Where-Object Name -eq $functionName |
                        Select-Object -First 1
                ).Extent.Text
            }
    ) -join ([Environment]::NewLine + [Environment]::NewLine)
    return $functionSource
}

function Set-NewSnapshotDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $currentSid =
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    $systemSid =
        New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
    $administratorsSid =
        New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $inheritance =
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation =
        [Security.AccessControl.PropagationFlags]::None
    $allow =
        [Security.AccessControl.AccessControlType]::Allow

    $acl = Get-SourceUsersSnapshotFileSystemAcl `
        -Path $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($currentSid, $systemSid, $administratorsSid)) {
        $acl.AddAccessRule(
            (New-Object Security.AccessControl.FileSystemAccessRule(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                $propagation,
                $allow)))
    }
    Set-SourceUsersSnapshotDirectoryAcl `
        -Path $Path `
        -Acl $acl
}

function Assert-SnapshotDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $allowedSids = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    [void]$allowedSids.Add(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    [void]$allowedSids.Add('S-1-5-18')
    [void]$allowedSids.Add('S-1-5-32-544')

    $acl = Get-SourceUsersSnapshotFileSystemAcl `
        -Path $Path
    $writeMask =
        [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Modify -bor
        [Security.AccessControl.FileSystemRights]::FullControl -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($rule in $acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier])) {
        if (
            $rule.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ($rule.FileSystemRights -band $writeMask) -ne 0 -and
            -not $allowedSids.Contains($rule.IdentityReference.Value)
        ) {
            throw (
                'The snapshot directory grants write access to an ' +
                'unsupported identity.')
        }
    }
}

function Initialize-SnapshotDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $created = $false
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -ErrorAction Stop |
            Out-Null
        $created = $true
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw 'The snapshot output root must be a directory.'
    }

    $current = Get-Item -LiteralPath $Path -Force
    $volumeRoot = [IO.Path]::GetPathRoot($current.FullName)
    while (-not [string]::Equals(
            $current.FullName,
            $volumeRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        if (
            ($current.Attributes -band
             [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw 'The snapshot output root cannot traverse a reparse point.'
        }
        $current = $current.Parent
    }

    if ($created) {
        Set-NewSnapshotDirectoryAcl -Path $Path
    }
    Assert-SnapshotDirectoryAcl -Path $Path
}

function Invoke-RemoteSnapshotQuery {
    param(
        [Parameter(Mandatory = $true)][string]$SshExecutable,
        [Parameter(Mandatory = $true)][string]$Sql
    )

    $sqlBase64 = ConvertTo-Base64Utf8 -Value $Sql
    $remoteScript = @"
set -euo pipefail
cd '$RemoteOpsDirectory'
set -a
. ./.env
set +a
DBUSER=`${POSTGRES_USER:-georaeplan}
DBNAME=`${POSTGRES_DB:-georaeplan}
echo '$sqlBase64' | base64 -d | docker compose exec -T postgres psql -X -qAt -v ON_ERROR_STOP=1 -U "`$DBUSER" -d "`$DBNAME" -f -
"@
    $remoteScriptBase64 =
        ConvertTo-Base64Utf8 -Value $remoteScript
    $target = "$LinuxSshUser@$LinuxSshHost"
    $arguments = @(
        '-p',
        [string]$LinuxSshPort,
        '-i',
        $LinuxSshKeyPath,
        '-o',
        'BatchMode=yes',
        '-o',
        'StrictHostKeyChecking=yes',
        '-o',
        'ConnectTimeout=10',
        '-o',
        'LogLevel=ERROR',
        $target,
        "echo $remoteScriptBase64 | base64 -d | bash"
    )

    $quotedArguments = @(
        $arguments |
            ForEach-Object {
                $value = [string]$_
                if ($value -notmatch '[\s"]') {
                    $value
                }
                else {
                    $escaped = [regex]::Replace(
                        $value,
                        '(\\*)"',
                        '$1$1\"')
                    $escaped = [regex]::Replace(
                        $escaped,
                        '(\\+)$',
                        '$1$1')
                    '"' + $escaped + '"'
                }
            }
    ) -join ' '
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $SshExecutable
    $startInfo.Arguments = $quotedArguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $startInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'Read-only user snapshot query process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            try {
                $process.Kill()
            }
            catch {
            }
            throw 'Read-only user snapshot query timed out.'
        }
        $capturedOutput = $stdoutTask.GetAwaiter().GetResult()
        [void]$stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
    if ($exitCode -ne 0) {
        throw "Read-only user snapshot query failed with exit code $exitCode."
    }

    $resultText = [string]$capturedOutput
    if ([string]::IsNullOrWhiteSpace($resultText)) {
        throw 'Read-only user snapshot query returned no data.'
    }
    if ([Text.Encoding]::UTF8.GetByteCount($resultText) -gt 1MB) {
        throw 'Read-only user snapshot query exceeded the size limit.'
    }
    return $resultText.Trim()
}

function Remove-OwnedSnapshotFile {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootFullPath = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
            [IO.Path]::GetDirectoryName($fullPath),
            $rootFullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Snapshot cleanup target escaped the dedicated output root.'
    }
    $leafName = [IO.Path]::GetFileName($fullPath)
    if ($leafName -cnotmatch
        '^source-users-\d{8}-\d{6}-[0-9a-f]{32}\.json(?:\.tmp)?$') {
        throw 'Snapshot cleanup target name is not owned by this exporter.'
    }

    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        if ($item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Snapshot cleanup target is not a regular file.'
        }
        [IO.File]::Delete($fullPath)
    }
    if (Test-Path -LiteralPath $fullPath) {
        throw 'Snapshot cleanup target still exists.'
    }
}

function Get-ReadOnlySnapshotSql {
    return @'
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL lock_timeout = '5s';
COPY (
WITH user_rows AS (
    SELECT
        u."Id",
        u."Username",
        u."Role",
        u."TenantCode",
        u."OfficeCode",
        u."ScopeType",
        u."IsActive",
        COALESCE(
            (
                SELECT jsonb_agg(
                    p."Permission"
                    ORDER BY p."Permission")
                FROM "UserPermissions" p
                WHERE p."UserId" = u."Id"
            ),
            '[]'::jsonb
        ) AS permissions
    FROM "Users" u
    WHERE COALESCE(u."IsDeleted", false) = false
),
scope_counts AS (
    SELECT
        "TenantCode",
        "OfficeCode",
        "Role",
        "ScopeType",
        "IsActive",
        count(*) AS user_count,
        sum(jsonb_array_length(permissions)) AS permission_count
    FROM user_rows
    GROUP BY
        "TenantCode",
        "OfficeCode",
        "Role",
        "ScopeType",
        "IsActive"
)
SELECT jsonb_build_object(
    'userCount',
        (SELECT count(*) FROM user_rows),
    'permissionCount',
        (
            SELECT COALESCE(
                sum(jsonb_array_length(permissions)),
                0)
            FROM user_rows
        ),
    'orphanOrDeletedPermissionCount',
        (
            SELECT count(*)
            FROM "UserPermissions" p
            LEFT JOIN "Users" u
                ON u."Id" = p."UserId"
            WHERE u."Id" IS NULL
               OR COALESCE(u."IsDeleted", false) = true
        ),
    'activeSystemAdminCount',
        (
            SELECT count(*)
            FROM user_rows
            WHERE "IsActive"
              AND "Role" = 'Admin'
              AND "ScopeType" = 'Admin'
        ),
    'scopeCounts',
        (
            SELECT COALESCE(
                jsonb_agg(
                    jsonb_build_object(
                        'tenantCode', "TenantCode",
                        'officeCode', "OfficeCode",
                        'role', "Role",
                        'scopeType', "ScopeType",
                        'isActive', "IsActive",
                        'userCount', user_count,
                        'permissionCount', permission_count)
                    ORDER BY
                        "TenantCode",
                        "OfficeCode",
                        "Role",
                        "ScopeType",
                        "IsActive"),
                '[]'::jsonb)
            FROM scope_counts
        ),
    'users',
        (
            SELECT COALESCE(
                jsonb_agg(
                    jsonb_build_object(
                        'username', "Username",
                        'role', "Role",
                        'tenantCode', "TenantCode",
                        'officeCode', "OfficeCode",
                        'scopeType', "ScopeType",
                        'isActive', "IsActive",
                        'permissions', permissions)
                    ORDER BY "Username"),
                '[]'::jsonb)
            FROM user_rows
        )
)::text
) TO STDOUT;
COMMIT;
'@
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected
    )

    if ($null -eq $Value) {
        throw 'The read-only user snapshot query returned null data.'
    }
    $actual = @($Value.PSObject.Properties.Name)
    if (
        @($Expected | Where-Object { $actual -cnotcontains $_ }).Count -gt 0 -or
        @($actual | Where-Object { $Expected -cnotcontains $_ }).Count -gt 0
    ) {
        throw 'The read-only user snapshot query returned unsupported fields.'
    }
}

$projectRoot = (
    Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')
).Path
$preparationScript = Join-Path `
    $projectRoot `
    '테스트 시행\테스트-환경-준비.ps1'
if (-not (Test-Path -LiteralPath $preparationScript -PathType Leaf)) {
    throw 'The test-environment preparation script was not found.'
}
$validationFunctionSource =
    Get-PreparationSnapshotValidationFunctionSource `
        -PreparationScriptPath $preparationScript
. ([ScriptBlock]::Create($validationFunctionSource))

$expectedOutputDirectory = Join-Path `
    ([IO.Path]::GetPathRoot($projectRoot)) `
    'DevCaches\georaeplan-v1-user-snapshots'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $expectedOutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not [string]::Equals(
        $OutputDirectory.TrimEnd('\'),
        $expectedOutputDirectory.TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'User permission snapshots must remain in the dedicated D cache root.'
}
if (
    $RemoteOpsDirectory -notmatch '^/[A-Za-z0-9/_-]+$' -or
    $LinuxSshHost -notmatch '^[A-Za-z0-9.-]+$' -or
    $LinuxSshUser -notmatch '^[A-Za-z0-9_-]+$'
) {
    throw 'Linux snapshot connection settings contain unsupported characters.'
}
if (-not (Test-Path -LiteralPath $LinuxSshKeyPath -PathType Leaf)) {
    throw 'The Linux PC SSH key was not found.'
}

Initialize-SnapshotDirectory -Path $OutputDirectory
Assert-SourceUsersSnapshotAcl `
    -Path $OutputDirectory `
    -AllowedRoot $OutputDirectory

$health = Invoke-WebRequest `
    -UseBasicParsing `
    -Uri 'https://trade.2884.kr/healthz' `
    -TimeoutSec 30
if ($health.StatusCode -ne 200) {
    throw 'trade.2884.kr preflight health check failed.'
}

$sshExecutable = Resolve-SshExecutable
$transportText = Invoke-RemoteSnapshotQuery `
    -SshExecutable $sshExecutable `
    -Sql (Get-ReadOnlySnapshotSql)
try {
    $transport = $transportText | ConvertFrom-Json
}
catch {
    throw 'The read-only user snapshot query returned invalid JSON.'
}
Assert-ExactProperties `
    -Value $transport `
    -Expected @(
        'userCount',
        'permissionCount',
        'orphanOrDeletedPermissionCount',
        'activeSystemAdminCount',
        'scopeCounts',
        'users'
    )
if (
    [long]$transport.orphanOrDeletedPermissionCount -ne 0 -or
    [long]$transport.activeSystemAdminCount -le 0
) {
    throw 'The live user-permission structure failed completeness checks.'
}

$users = @(
    $transport.users |
        ForEach-Object {
            Assert-ExactProperties `
                -Value $_ `
                -Expected @(
                    'username',
                    'role',
                    'tenantCode',
                    'officeCode',
                    'scopeType',
                    'isActive',
                    'permissions'
                )
            [pscustomobject][ordered]@{
                username = [string]$_.username
                role = [string]$_.role
                tenantCode = [string]$_.tenantCode
                officeCode = [string]$_.officeCode
                scopeType = [string]$_.scopeType
                isActive = [bool]$_.isActive
                permissions = @(
                    $_.permissions |
                        ForEach-Object { [string]$_ } |
                        Sort-Object
                )
            }
        }
)
$permissionCount = @(
    $users |
        ForEach-Object { @($_.permissions).Count } |
        Measure-Object -Sum
)[0].Sum
$computedScopeCounts = @(
    Get-SourceUsersSnapshotScopeCounts -Users $users
)
$transportScopeCounts = @(
    $transport.scopeCounts |
        ForEach-Object {
            Assert-ExactProperties `
                -Value $_ `
                -Expected @(
                    'tenantCode',
                    'officeCode',
                    'role',
                    'scopeType',
                    'isActive',
                    'userCount',
                    'permissionCount'
                )
            [pscustomobject][ordered]@{
                tenantCode = [string]$_.tenantCode
                officeCode = [string]$_.officeCode
                role = [string]$_.role
                scopeType = [string]$_.scopeType
                isActive = [bool]$_.isActive
                userCount = [long]$_.userCount
                permissionCount = [long]$_.permissionCount
            }
        }
)
$computedScopeJson = ConvertTo-Json `
    -InputObject @($computedScopeCounts) `
    -Depth 10 `
    -Compress
$transportScopeJson = ConvertTo-Json `
    -InputObject @($transportScopeCounts) `
    -Depth 10 `
    -Compress
if (
    [long]$transport.userCount -ne $users.Count -or
    [long]$transport.permissionCount -ne [long]$permissionCount -or
    -not [string]::Equals(
        $computedScopeJson,
        $transportScopeJson,
        [StringComparison]::Ordinal)
) {
    throw 'The live user-permission counts were not internally consistent.'
}

$canonicalJson =
    Get-SourceUsersSnapshotCanonicalJson -Users $users
$envelope = [ordered]@{
    schemaVersion = 1
    sourceKind = 'georaeplan-user-permission-snapshot-v1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    isComplete = $true
    userCount = $users.Count
    permissionCount = [long]$permissionCount
    scopeCounts = $computedScopeCounts
    canonicalSha256 =
        Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
    users = $users
}
$snapshotJson = $envelope | ConvertTo-Json -Depth 20
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$snapshotPath = Join-Path `
    $OutputDirectory `
    "source-users-$timestamp-$([Guid]::NewGuid().ToString('N')).json"
$temporaryPath = "$snapshotPath.tmp"
try {
    $encoding = New-Object Text.UTF8Encoding($false)
    $snapshotBytes = $encoding.GetBytes($snapshotJson)
    if ($snapshotBytes.Length -le 0 -or $snapshotBytes.Length -gt 1MB) {
        throw 'The generated user permission snapshot exceeded the size limit.'
    }
    $writeStream = New-Object IO.FileStream(
        $temporaryPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $writeStream.Write(
            $snapshotBytes,
            0,
            $snapshotBytes.Length)
        $writeStream.Flush($true)
    }
    finally {
        $writeStream.Dispose()
    }
    [IO.File]::Move($temporaryPath, $snapshotPath)

    $expectedSnapshotSha256 = (
        Get-FileHash `
            -LiteralPath $snapshotPath `
            -Algorithm SHA256
    ).Hash
    $validated = Import-SourceUsersSnapshot `
        -Path $snapshotPath `
        -AllowedRoot $OutputDirectory `
        -ExpectedSha256 $expectedSnapshotSha256 `
        -RequireProtectedAcl
    if (
        $validated.UserCount -ne $users.Count -or
        $validated.PermissionCount -ne [long]$permissionCount
    ) {
        throw 'The generated user permission snapshot failed final validation.'
    }
}
catch {
    $primaryError = $_
    $cleanupErrors = New-Object 'Collections.Generic.List[string]'
    foreach ($createdPath in @($temporaryPath, $snapshotPath)) {
        try {
            Remove-OwnedSnapshotFile `
                -OutputRoot $OutputDirectory `
                -Path $createdPath
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message) | Out-Null
        }
    }
    if ($cleanupErrors.Count -gt 0) {
        throw (New-Object InvalidOperationException(
            ($primaryError.Exception.Message +
             ' Cleanup verification failed: ' +
             ($cleanupErrors -join '; ')),
            $primaryError.Exception))
    }
    throw $primaryError
}

Write-Host 'Read-only live user permission snapshot created.'
Write-Host "snapshot_path=$snapshotPath"
Write-Host "user_count=$($validated.UserCount)"
Write-Host "permission_count=$($validated.PermissionCount)"
Write-Host "scope_count=$(@($validated.ScopeCounts).Count)"
Write-Host "snapshot_sha256=$($validated.SnapshotSha256)"
Write-Host "canonical_sha256=$($validated.CanonicalSha256)"
