[CmdletBinding()]
param(
    [string]$SourceApiBaseUrl = 'https://trade.2884.kr',
    [string]$SourceAppRoot = (
        Join-Path `
            ([Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) `
            '거래플랜'),
    [switch]$PromptForSystemAdminCredential,
    [switch]$AllowLoopbackSourceApiForTesting
)

$ErrorActionPreference = 'Stop'

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
        'Assert-SafeSourceApiBaseUrl',
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
            throw 'A required source snapshot validation function is unavailable.'
        }
    }

    return (
        $requiredFunctions |
            ForEach-Object {
                $name = $_
                ($definitions | Where-Object Name -eq $name |
                    Select-Object -First 1).Extent.Text
            }
    ) -join ([Environment]::NewLine + [Environment]::NewLine)
}

function Resolve-SourceApiOrigin {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [switch]$AllowLoopbackForTesting
    )

    $testEnvironmentEnabled =
        $env:GEORAEPLAN_SOURCE_USERS_EXPORT_TEST_MODE -ceq '1'
    if ($AllowLoopbackForTesting -xor $testEnvironmentEnabled) {
        throw 'Loopback Source API testing requires both explicit test gates.'
    }

    $normalized = Assert-SafeSourceApiBaseUrl `
        -BaseUrl $BaseUrl `
        -AllowRemote
    $uri = New-Object Uri($normalized)
    if (
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        $uri.AbsolutePath -cne '/'
    ) {
        throw 'SourceApiBaseUrl must be an exact origin without a path.'
    }

    if ($AllowLoopbackForTesting) {
        if (-not $uri.IsLoopback) {
            throw 'The test-only Source API origin must be loopback.'
        }
        return $uri.AbsoluteUri.TrimEnd('/')
    }

    if ($normalized -cne 'https://trade.2884.kr') {
        throw 'Production user export is restricted to https://trade.2884.kr.'
    }
    return 'https://trade.2884.kr'
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$RequireLeaf
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw 'A required local path does not exist.'
    }
    if ($RequireLeaf -and
        -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw 'A required local file does not exist.'
    }
    $current = Get-Item -LiteralPath $fullPath -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A guarded local path cannot traverse a reparse point.'
        }
        $current = if ($current -is [IO.FileInfo]) {
            $current.Directory
        }
        else {
            $current.Parent
        }
    }
    return $fullPath
}

function Get-TrustedSnapshotSids {
    return @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544'
    )
}

function Set-PrivateDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $acl = New-Object Security.AccessControl.DirectorySecurity
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($owner)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sidText in Get-TrustedSnapshotSids) {
        $sid = New-Object Security.Principal.SecurityIdentifier $sidText
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
             [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Assert-ExactProtectedDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    [void](Assert-NoReparsePath -Path $Path)
    $allowed = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::Ordinal)
    foreach ($sidText in Get-TrustedSnapshotSids) {
        [void]$allowed.Add($sidText)
    }
    $acl = Get-Acl -LiteralPath $Path
    $ownerSid = (New-Object Security.Principal.NTAccount($acl.Owner)).
        Translate([Security.Principal.SecurityIdentifier]).Value
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected -or
        -not $allowed.Contains($ownerSid) -or
        $rules.Count -ne $allowed.Count) {
        throw 'The guarded directory ACL is not the required protected ACL.'
    }
    foreach ($rule in $rules) {
        if (
            $rule.IsInherited -or
            $rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne
                [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne
                ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                 [Security.AccessControl.InheritanceFlags]::ObjectInherit) -or
            $rule.PropagationFlags -ne
                [Security.AccessControl.PropagationFlags]::None -or
            -not $allowed.Remove($rule.IdentityReference.Value)
        ) {
            throw 'The guarded directory ACL contains an unsupported rule.'
        }
    }
    if ($allowed.Count -ne 0) {
        throw 'The guarded directory ACL is incomplete.'
    }
}

function ConvertTo-WindowsProcessArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-BoundedChildProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [hashtable]$EnvironmentVariables = @{},
        [int]$TimeoutMilliseconds = 60000,
        [int]$MaximumStdoutBytes = 393216,
        [int]$MaximumStderrBytes = 8192
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (@(
        $Arguments | ForEach-Object {
            ConvertTo-WindowsProcessArgument -Value ([string]$_)
        }) -join ' ')
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $startInfo.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    foreach ($key in $EnvironmentVariables.Keys) {
        $startInfo.EnvironmentVariables[[string]$key] =
            [string]$EnvironmentVariables[$key]
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw 'The required local helper process did not start.'
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try { $process.Kill() } catch {}
            [void]$process.WaitForExit(5000)
            throw 'The required local helper process timed out.'
        }
        $stdout = [string]$stdoutTask.GetAwaiter().GetResult()
        $stderr = [string]$stderrTask.GetAwaiter().GetResult()
        if ([Text.Encoding]::UTF8.GetByteCount($stdout) -gt $MaximumStdoutBytes -or
            [Text.Encoding]::UTF8.GetByteCount($stderr) -gt $MaximumStderrBytes) {
            throw 'The required local helper process exceeded its capture limit.'
        }
        return [pscustomobject]@{
            ExitCode = [int]$process.ExitCode
            Stdout = $stdout
            HasStderr = -not [string]::IsNullOrEmpty($stderr)
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected
    )

    if ($null -eq $Value) { throw 'A local helper returned null data.' }
    $actual = @($Value.PSObject.Properties.Name)
    if (@($Expected | Where-Object { $actual -cnotcontains $_ }).Count -gt 0 -or
        @($actual | Where-Object { $Expected -cnotcontains $_ }).Count -gt 0) {
        throw 'A local helper returned unsupported fields.'
    }
}

function ConvertFrom-StoredCredentialEnvelopeOutput {
    param([Parameter(Mandatory = $true)][string]$Stdout)

    if ([string]::IsNullOrWhiteSpace($Stdout)) {
        throw 'The local credential reader returned no envelope.'
    }
    $line = $Stdout.TrimEnd([char[]]@("`r", "`n"))
    if ($line.IndexOfAny([char[]]@("`r", "`n")) -ge 0) {
        throw 'The local credential reader must return exactly one JSON line.'
    }
    try {
        Initialize-TestEnvironmentFinalPathNativeMethods
        [GeoraePlan.TestEnvironment.FinalPathNativeMethods]::
            AssertNoDuplicateJsonObjectPropertiesAndDepth($line, 12)
        $envelope = $line | ConvertFrom-Json
    }
    catch {
        throw 'The local credential reader returned invalid JSON.'
    }
    Assert-ExactProperties -Value $envelope -Expected @(
        'schemaVersion', 'protection', 'credentials')
    if (($envelope.schemaVersion -isnot [int] -and
         $envelope.schemaVersion -isnot [long]) -or
        [long]$envelope.schemaVersion -ne 1 -or
        $envelope.protection -isnot [string] -or
        [string]$envelope.protection -cne 'DPAPI-CurrentUser' -or
        $envelope.credentials -isnot [Array]) {
        throw 'The local credential envelope contract is unsupported.'
    }

    $credentials = @($envelope.credentials | ForEach-Object { $_ })
    if ($credentials.Count -lt 1 -or $credentials.Count -gt 16) {
        throw 'The local credential envelope count is invalid.'
    }
    foreach ($credential in $credentials) {
        $protectedBytes = $null
        Assert-ExactProperties -Value $credential -Expected @(
            'OfficeCode',
            'TenantCode',
            'Username',
            'PasswordProtected',
            'SavedAtUtc')
        if ($credential.Username -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$credential.Username) -or
            ([string]$credential.Username).Length -gt 256 -or
            $credential.OfficeCode -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$credential.OfficeCode) -or
            ([string]$credential.OfficeCode).Length -gt 64 -or
            $credential.TenantCode -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$credential.TenantCode) -or
            ([string]$credential.TenantCode).Length -gt 64 -or
            $credential.PasswordProtected -isnot [string] -or
            ([string]$credential.PasswordProtected).Length -gt 24576 -or
            $credential.SavedAtUtc -isnot [string] -or
            ([string]$credential.SavedAtUtc).Length -gt 64) {
            throw 'The local credential envelope contains invalid values.'
        }
        $savedAt = [DateTimeOffset]::MinValue
        if (-not ([string]$credential.SavedAtUtc).EndsWith(
                'Z',
                [StringComparison]::Ordinal) -or
            -not [DateTimeOffset]::TryParse(
                [string]$credential.SavedAtUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$savedAt) -or
            $savedAt.Offset -ne [TimeSpan]::Zero) {
            throw 'The local credential envelope timestamp is invalid.'
        }
        try {
            $protectedBytes = [Convert]::FromBase64String(
                [string]$credential.PasswordProtected)
            if ($protectedBytes.Length -eq 0 -or
                [Convert]::ToBase64String($protectedBytes) -cne
                    [string]$credential.PasswordProtected) {
                throw 'invalid'
            }
        }
        catch {
            throw 'The local credential envelope contains invalid protected data.'
        }
        finally {
            if ($null -ne $protectedBytes) {
                [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
                $protectedBytes = $null
            }
        }
    }
    return $credentials
}

function Get-PromptedSystemAdminCredential {
    $credential = Get-Credential `
        -Message '거래플랜 시스템 관리자 계정을 입력하세요.'
    if ($null -eq $credential -or
        [string]::IsNullOrWhiteSpace([string]$credential.UserName)) {
        throw 'The local system-administrator credential prompt was cancelled.'
    }
    if ($null -eq $credential.Password -or $credential.Password.Length -eq 0) {
        if ($null -ne $credential.Password) {
            $credential.Password.Dispose()
        }
        throw 'The local system-administrator password cannot be empty.'
    }
    return $credential
}

function Get-SourceUsersViaApi {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [object[]]$CredentialEnvelopes = @(),
        [Management.Automation.PSCredential]$PromptedCredential
    )

    Add-Type -AssemblyName System.Security -ErrorAction Stop
    if ($null -ne $PromptedCredential -and $CredentialEnvelopes.Count -gt 0) {
        throw 'Prompted and stored credentials cannot be combined.'
    }
    if ($null -eq $PromptedCredential -and $CredentialEnvelopes.Count -eq 0) {
        throw 'No Source API credential was supplied.'
    }
    $credentialCandidates = if ($null -ne $PromptedCredential) {
        @([pscustomobject]@{
            Username = [string]$PromptedCredential.UserName
            Prompted = $true
            PasswordProtected = $null
        })
    }
    else {
        @($CredentialEnvelopes | ForEach-Object {
            [pscustomobject]@{
                Username = [string]$_.Username
                Prompted = $false
                PasswordProtected = [string]$_.PasswordProtected
            }
        })
    }
    $attemptCount = 0
    $decryptFailureCount = 0
    $loginFailureCount = 0
    $scopeFailureCount = 0
    $usersFailureCount = 0
    foreach ($credential in $credentialCandidates) {
        $attemptCount++
        $bstr = [IntPtr]::Zero
        $protectedBytes = $null
        $plainBytes = $null
        $password = $null
        $body = $null
        $token = $null
        $phase = 'decrypt'
        try {
            if ($credential.Prompted) {
                $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                    $PromptedCredential.Password)
                $password = [Runtime.InteropServices.Marshal]::
                    PtrToStringBSTR($bstr)
            }
            else {
                $protectedBytes = [Convert]::FromBase64String(
                    [string]$credential.PasswordProtected)
                $plainBytes = [Security.Cryptography.ProtectedData]::Unprotect(
                    $protectedBytes,
                    $null,
                    [Security.Cryptography.DataProtectionScope]::CurrentUser)
                $password = (New-Object Text.UTF8Encoding($false, $true)).
                    GetString($plainBytes)
            }
            if ([string]::IsNullOrEmpty($password)) { continue }
            $body = @{
                username = [string]$credential.Username
                password = $password
            } | ConvertTo-Json -Compress
            $phase = 'login'
            $login = Invoke-RestMethod `
                -Method Post `
                -Uri ($BaseUrl + '/auth/login') `
                -ContentType 'application/json' `
                -Body $body `
                -TimeoutSec 20 `
                -MaximumRedirection 0 `
                -ErrorAction Stop
            $token = if ($login.token) {
                [string]$login.token
            }
            elseif ($login.accessToken) {
                [string]$login.accessToken
            }
            else { '' }
            if ([string]::IsNullOrWhiteSpace($token)) {
                $loginFailureCount++
                continue
            }
            if (-not [string]::Equals(
                    [string]$login.user.role,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals(
                    [string]$login.user.scopeType,
                    'Admin',
                    [StringComparison]::OrdinalIgnoreCase)) {
                $scopeFailureCount++
                continue
            }
            $phase = 'users'
            $response = Invoke-RestMethod `
                -Method Get `
                -Uri ($BaseUrl + '/users') `
                -Headers @{ Authorization = 'Bearer ' + $token } `
                -TimeoutSec 20 `
                -MaximumRedirection 0 `
                -ErrorAction Stop
            $users = @(
                $response | ForEach-Object {
                    if ($_.username -isnot [string] -or
                        $_.role -isnot [string] -or
                        $_.tenantCode -isnot [string] -or
                        $_.officeCode -isnot [string] -or
                        $_.scopeType -isnot [string] -or
                        $_.isActive -isnot [bool] -or
                        $_.permissions -isnot [Array]) {
                        throw 'The Source API returned an invalid user contract.'
                    }
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
                                Sort-Object)
                    }
                }
            )
            if ($users.Count -gt 0) { return $users }
            $usersFailureCount++
        }
        catch {
            # Try the next locally stored credential without exposing details.
            if ($phase -ceq 'decrypt') {
                $decryptFailureCount++
            }
            elseif ($phase -ceq 'login') {
                $loginFailureCount++
            }
            else {
                $usersFailureCount++
            }
        }
        finally {
            $login = $null
            $response = $null
            $body = $null
            $token = $null
            $password = $null
            if ($null -ne $plainBytes) {
                [Array]::Clear($plainBytes, 0, $plainBytes.Length)
            }
            if ($null -ne $protectedBytes) {
                [Array]::Clear($protectedBytes, 0, $protectedBytes.Length)
            }
            if ($bstr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            }
        }
    }
    $credentialKind = if ($null -ne $PromptedCredential) {
        'prompted'
    }
    else {
        'stored'
    }
    throw (
        "No $credentialKind system-administrator credential completed the Source API " +
        'export. ' +
        "attempt_count=$attemptCount " +
        "decrypt_failure_count=$decryptFailureCount " +
        "login_failure_count=$loginFailureCount " +
        "scope_failure_count=$scopeFailureCount " +
        "users_failure_count=$usersFailureCount")
}

function Write-CreateNewUtf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )

    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Text)
    $stream = New-Object IO.FileStream(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Remove-OwnedExportArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [string[]]$Paths
    )

    $errors = @()
    $root = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    foreach ($candidate in @($Paths | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) })) {
        try {
            $full = [IO.Path]::GetFullPath($candidate)
            if (-not $full.StartsWith(
                    $prefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Cleanup target escaped the dedicated output root.'
            }
            if (Test-Path -LiteralPath $full) {
                Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
            }
            if (Test-Path -LiteralPath $full) {
                throw 'Cleanup target still exists.'
            }
        }
        catch { $errors += $_.Exception.Message }
    }
    return @($errors)
}

function Invoke-GeoraePlanSourceUsersSnapshotExport {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [switch]$PromptForCredential,
        [switch]$AllowLoopbackForTesting
    )

    $projectRoot = (Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot '..\..')).Path
    $preparationScript = Join-Path `
        $projectRoot `
        '테스트 시행\테스트-환경-준비.ps1'
    $validationSource =
        Get-PreparationSnapshotValidationFunctionSource `
            -PreparationScriptPath $preparationScript
    . ([ScriptBlock]::Create($validationSource))

    $origin = Resolve-SourceApiOrigin `
        -BaseUrl $BaseUrl `
        -AllowLoopbackForTesting:$AllowLoopbackForTesting
    $appRootFull = $null
    $sourceDatabase = $null
    if (-not $PromptForCredential) {
        $expectedAppRoot = Join-Path `
            ([Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) `
            '거래플랜'
        $appRootFull = [IO.Path]::GetFullPath($AppRoot).TrimEnd('\')
        if (-not [string]::Equals(
                $appRootFull,
                [IO.Path]::GetFullPath($expectedAppRoot).TrimEnd('\'),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'SourceAppRoot must be the normal current-user TradePlan root.'
        }
        [void](Assert-NoReparsePath -Path $appRootFull)
        $sourceDatabase = Join-Path $appRootFull 'data\거래플랜.db'
        [void](Assert-NoReparsePath -Path $sourceDatabase -RequireLeaf)
    }

    $outputRoot = 'D:\DevCaches\georaeplan-v1-user-snapshots'
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw 'The dedicated protected snapshot root does not exist.'
    }
    Assert-ExactProtectedDirectoryAcl -Path $outputRoot

    $syncDiagDirectory = $null
    $syncDiagDll = $null
    $dotnetPath = $null
    $artifactHashBefore = $null
    if (-not $PromptForCredential) {
        $syncDiagDirectory = Join-Path `
            $projectRoot `
            'tools\SyncDiag\bin\Release\net8.0-windows'
        $syncDiagDll = Join-Path $syncDiagDirectory 'SyncDiag.dll'
        [void](Assert-NoReparsePath -Path $syncDiagDll -RequireLeaf)
        $dotnetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        [void](Assert-NoReparsePath -Path $dotnetPath -RequireLeaf)
        $dotnetSignature = Get-AuthenticodeSignature -LiteralPath $dotnetPath
        if ($dotnetSignature.Status -ne 'Valid' -or
            $null -eq $dotnetSignature.SignerCertificate -or
            $dotnetSignature.SignerCertificate.Subject -notmatch
                '(^|, )O=Microsoft Corporation(,|$)') {
            throw 'The trusted Microsoft .NET host signature is invalid.'
        }
        $artifactHashBefore = (Get-FileHash `
            -LiteralPath $syncDiagDll `
            -Algorithm SHA256).Hash
    }

    $operationId = [Guid]::NewGuid().ToString('N')
    $stagingRoot = Join-Path `
        $outputRoot `
        ('.source-users-export-staging-' + $operationId)
    $stagingAppRoot = Join-Path $stagingRoot 'AppData'
    $stagingData = Join-Path $stagingAppRoot 'data'
    $stagingTemp = Join-Path $stagingAppRoot 'temp'
    $stagingDatabase = Join-Path $stagingData '거래플랜.db'
    $markerPath = Join-Path `
        $stagingAppRoot `
        '.georaeplan-isolated-seed-root'
    $preparationLeasePath = Join-Path `
        $stagingRoot `
        '.georaeplan-prepare.lock'
    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $finalPath = Join-Path `
        $outputRoot `
        ('source-users-' + $timestamp + '-' + $operationId + '.json')
    $temporaryPath = Join-Path `
        $outputRoot `
        ('.source-users-' + $operationId + '.json.tmp')

    $primaryError = $null
    $validated = $null
    $preparationLease = $null
    $promptedCredential = $null
    try {
        if ($PromptForCredential) {
            $promptedCredential = Get-PromptedSystemAdminCredential
            try {
                $users = @(Get-SourceUsersViaApi `
                    -BaseUrl $origin `
                    -PromptedCredential $promptedCredential)
            }
            finally {
                if ($null -ne $promptedCredential -and
                    $null -ne $promptedCredential.Password) {
                    $promptedCredential.Password.Dispose()
                }
                $promptedCredential = $null
            }
        }
        else {
            [void](New-Item -ItemType Directory -Path $stagingRoot)
            Set-PrivateDirectoryAcl -Path $stagingRoot
            Assert-ExactProtectedDirectoryAcl -Path $stagingRoot
            [void](New-Item -ItemType Directory -Path $stagingAppRoot)
            Set-PrivateDirectoryAcl -Path $stagingAppRoot
            Assert-ExactProtectedDirectoryAcl -Path $stagingAppRoot
            [void](New-Item -ItemType Directory -Path $stagingData)
            Write-CreateNewUtf8File `
                -Path $markerPath `
                -Text $stagingAppRoot

            $snapshotProcess = Invoke-BoundedChildProcess `
                -FileName $dotnetPath `
                -Arguments @(
                    $syncDiagDll,
                    'snapshot-sqlite',
                    $sourceDatabase,
                    $stagingDatabase) `
                -WorkingDirectory $syncDiagDirectory `
                -EnvironmentVariables @{
                    GEORAEPLAN_TEST_MODE = '1'
                    GEORAEPLAN_SOURCE_SNAPSHOT_ROOT = $appRootFull
                    GEORAEPLAN_TARGET_SNAPSHOT_ROOT = $stagingAppRoot
                }
            if ($snapshotProcess.ExitCode -ne 0 -or
                $snapshotProcess.HasStderr -or
                $snapshotProcess.Stdout -notmatch '(?m)^snapshot_succeeded=True\r?$' -or
                $snapshotProcess.Stdout -notmatch '(?m)^quick_check=ok\r?$' -or
                $snapshotProcess.Stdout -notmatch '(?m)^sidecar_count=0\r?$') {
                throw 'The verified local SQLite snapshot step failed.'
            }
            [void](Assert-NoReparsePath -Path $stagingDatabase -RequireLeaf)

            $preparationLease = [IO.File]::Open(
                $preparationLeasePath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::Read)

            $credentialProcess = Invoke-BoundedChildProcess `
                -FileName $dotnetPath `
                -Arguments @($syncDiagDll, 'source-credential-envelopes') `
                -WorkingDirectory $syncDiagDirectory `
                -EnvironmentVariables @{
                    GEORAEPLAN_APP_ROOT = $stagingAppRoot
                    GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
                    GEORAEPLAN_TEST_MODE = '1'
                    GEORAEPLAN_TEST_SEED_MODE = '1'
                    GEORAEPLAN_TEST_SEED_ROOT = $stagingAppRoot
                    GEORAEPLAN_TEMP_ROOT = $stagingTemp
                    TEMP = $stagingTemp
                    TMP = $stagingTemp
                }
            if ($credentialProcess.ExitCode -ne 0 -or
                $credentialProcess.HasStderr) {
                throw 'The isolated local credential reader failed.'
            }
            $credentials = @(
                ConvertFrom-StoredCredentialEnvelopeOutput `
                    -Stdout $credentialProcess.Stdout)
            $credentialProcess = $null
            $users = @(Get-SourceUsersViaApi `
                -BaseUrl $origin `
                -CredentialEnvelopes $credentials)
            $credentials = $null
        }

        $permissionCount = @(
            $users | ForEach-Object { @($_.permissions).Count } |
                Measure-Object -Sum)[0].Sum
        $scopeCounts = @(
            Get-SourceUsersSnapshotScopeCounts -Users $users)
        $canonicalJson =
            Get-SourceUsersSnapshotCanonicalJson -Users $users
        $envelope = [ordered]@{
            schemaVersion = 1
            sourceKind = 'georaeplan-user-permission-snapshot-v1'
            generatedAtUtc = [DateTime]::UtcNow.ToString('O')
            isComplete = $true
            userCount = $users.Count
            permissionCount = [long]$permissionCount
            scopeCounts = $scopeCounts
            canonicalSha256 =
                Get-SourceUsersSnapshotTextSha256 -Text $canonicalJson
            users = $users
        }
        $snapshotJson = $envelope | ConvertTo-Json -Depth 20
        if ([Text.Encoding]::UTF8.GetByteCount($snapshotJson) -gt 1MB) {
            throw 'The generated snapshot exceeded the size limit.'
        }
        Write-CreateNewUtf8File -Path $temporaryPath -Text $snapshotJson
        [IO.File]::Move($temporaryPath, $finalPath)

        $snapshotSha256 = (Get-FileHash `
            -LiteralPath $finalPath `
            -Algorithm SHA256).Hash
        $validated = Import-SourceUsersSnapshot `
            -Path $finalPath `
            -AllowedRoot $outputRoot `
            -ExpectedSha256 $snapshotSha256 `
            -RequireProtectedAcl `
            -MaximumAgeHours 24
        if ($validated.UserCount -ne $users.Count -or
            $validated.PermissionCount -ne [long]$permissionCount) {
            throw 'The generated snapshot failed final count validation.'
        }
        if (-not $PromptForCredential -and
            (Get-FileHash -LiteralPath $syncDiagDll -Algorithm SHA256).Hash -cne
                $artifactHashBefore) {
            throw 'The existing Release SyncDiag artifact changed during export.'
        }
    }
    catch { $primaryError = $_ }

    if ($null -ne $preparationLease) {
        try {
            $preparationLease.Dispose()
        }
        catch {
            if ($null -eq $primaryError) {
                $primaryError = $_
            }
        }
        finally {
            $preparationLease = $null
        }
    }

    $cleanupTargets = @($temporaryPath, $stagingRoot)
    if ($null -ne $primaryError) { $cleanupTargets += $finalPath }
    $cleanupErrors = @(Remove-OwnedExportArtifacts `
        -AllowedRoot $outputRoot `
        -Paths $cleanupTargets)
    if ($cleanupErrors.Count -gt 0) {
        if ($null -eq $primaryError) {
            $finalCleanupErrors = @(Remove-OwnedExportArtifacts `
                -AllowedRoot $outputRoot `
                -Paths @($finalPath))
            $cleanupErrors += $finalCleanupErrors
        }
        $cleanupMessage = $cleanupErrors -join '; '
        if ($null -ne $primaryError) {
            throw (New-Object InvalidOperationException(
                ($primaryError.Exception.Message +
                 ' Cleanup verification failed: ' + $cleanupMessage),
                $primaryError.Exception))
        }
        throw ('Cleanup verification failed: ' + $cleanupMessage)
    }
    if ($null -ne $primaryError) { throw $primaryError }

    return [pscustomobject]@{
        Path = $finalPath
        UserCount = $validated.UserCount
        PermissionCount = $validated.PermissionCount
        Sha256 = $validated.SnapshotSha256
    }
}

$result = Invoke-GeoraePlanSourceUsersSnapshotExport `
    -BaseUrl $SourceApiBaseUrl `
    -AppRoot $SourceAppRoot `
    -PromptForCredential:$PromptForSystemAdminCredential `
    -AllowLoopbackForTesting:$AllowLoopbackSourceApiForTesting
Write-Output ('snapshot_path=' + $result.Path)
Write-Output ('user_count=' + $result.UserCount)
Write-Output ('permission_count=' + $result.PermissionCount)
Write-Output ('snapshot_sha256=' + $result.Sha256)
