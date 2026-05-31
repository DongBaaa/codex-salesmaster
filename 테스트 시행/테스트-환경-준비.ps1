[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$Configuration = 'Debug',
    [string]$OutputRoot,
    [string]$SourceAppRoot,
    [switch]$SkipBuild,
    [switch]$SkipDataCopy,
    [switch]$SkipServerSeed,
    [switch]$AllowDirtySeedFailure,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        catch {
            continue
        }
    }

    throw "테스트 환경 준비용 dotnet 실행 파일을 찾지 못했습니다. ProjectRoot=$ProjectRoot"
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function New-Utf8BomEncoding {
    return New-Object System.Text.UTF8Encoding($true)
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content,
        [switch]$WithBom
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $encoding = if ($WithBom) { New-Utf8BomEncoding } else { New-Utf8NoBomEncoding }
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $DotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 명령이 실패했습니다. args=$($Arguments -join ' ')"
    }
}

function Invoke-DotnetWithOutput {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $DotnetExe @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = (@($output) | ForEach-Object { $_.ToString() })
        Text = ((@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        if ($AllowFailure) { return '' }
        throw 'git 명령을 찾지 못했습니다.'
    }

    Push-Location $ProjectRoot
    try {
        $output = & $git.Source @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "git 명령이 실패했습니다. args=$($Arguments -join ' ')"
    }

    return (@($output) | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
}

function Find-FirstFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Filter
    )

    $match = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Filter | Select-Object -First 1
    if ($null -eq $match) {
        throw "필수 파일을 찾지 못했습니다. Filter=$Filter Root=$Root"
    }

    return $match.FullName
}

function Find-DeploymentRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw '배포 실행 스크립트 루트를 찾지 못했습니다.'
    }

    return $candidate.FullName
}

function Build-ChangedFilesMarkdown {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$GeneratedAt,
        [Parameter(Mandatory = $true)][string]$Branch,
        [Parameter(Mandatory = $true)][string]$Commit
    )

    $statusText = Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('-c', 'core.quotepath=false', 'status', '--short') -AllowFailure
    $lines = @($statusText -split "`r?`n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('# 최근 수정 파일')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("- 생성 시각: $GeneratedAt")
    [void]$builder.AppendLine(('- Git 브랜치: `{0}`' -f $Branch))
    [void]$builder.AppendLine(('- Git 커밋: `{0}`' -f $Commit))
    [void]$builder.AppendLine()

    if ($lines.Count -eq 0) {
        [void]$builder.AppendLine('현재 Git 기준 수정/추가 파일이 없습니다.')
        return $builder.ToString().TrimEnd()
    }

    [void]$builder.AppendLine('## Git status --short')
    [void]$builder.AppendLine()
    foreach ($line in $lines) {
        [void]$builder.AppendLine(('- `{0}`' -f $line))
    }
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('## 확인 메모')
    [void]$builder.AppendLine()
    [void]$builder.AppendLine('- 위 파일이 이번 테스트 대상입니다.')
    [void]$builder.AppendLine('- 테스트 완료 전에는 NAS/Git 반영을 진행하지 않습니다.')
    return $builder.ToString().TrimEnd()
}

function Build-ChecklistContent {
    param(
        [Parameter(Mandatory = $true)][string]$TemplatePath,
        [Parameter(Mandatory = $true)][hashtable]$Tokens
    )

    $content = Get-Content -LiteralPath $TemplatePath -Raw
    foreach ($key in $Tokens.Keys) {
        $token = ('{{{{{0}}}}}' -f $key)
        $content = $content.Replace($token, $Tokens[$key])
    }

    return $content
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludeDirectories = @(),
        [string[]]$ExcludeFiles = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $arguments = @(
        $Source,
        $Destination,
        '/MIR',
        '/R:2',
        '/W:2',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP'
    )

    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludeDirectories
    }

    if ($ExcludeFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludeFiles
    }

    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination"
    }
}

function Reset-TransientAppDataDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    foreach ($child in @('backup', 'diagnostics', 'logs', 'temp')) {
        $path = Join-Path $Root $child
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function Invoke-WithProcessEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Variables,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $backup = @{}
    foreach ($key in $Variables.Keys) {
        $backup[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Variables[$key], 'Process')
    }

    try {
        return & $Action
    }
    finally {
        foreach ($key in $backup.Keys) {
            [Environment]::SetEnvironmentVariable($key, $backup[$key], 'Process')
        }
    }
}

function Get-FreeTcpPort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSeconds = 40
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Get-StoredSyncCredentialsFromLocalState {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $result = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_APP_ROOT = $AppRoot
        GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
    } -Action {
        Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'stored-credentials')
    }

    if ($result.ExitCode -ne 0) {
        Write-Utf8File -Path $LogPath -Content (@(
            "stored_credentials_exit_code=$($result.ExitCode)",
            "stored_credentials_error=failed"
        ) -join [Environment]::NewLine)
        throw "저장된 동기화 로그인 조회 실패`n$($result.Text)"
    }

    $jsonText = @($result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        return @()
    }

    $parsed = $jsonText | ConvertFrom-Json
    if ($null -eq $parsed) {
        return @()
    }

    $credentials = @($parsed)
    $sanitized = @(
        $credentials | ForEach-Object {
            [pscustomobject]@{
                OfficeCode = [string]$_.OfficeCode
                TenantCode = [string]$_.TenantCode
                Username = [string]$_.Username
                SavedAtUtc = [string]$_.SavedAtUtc
            }
        }
    )
    $sanitizedJson = if ($sanitized.Count -gt 0) {
        $sanitized | ConvertTo-Json -Depth 10
    }
    else {
        '[]'
    }

    Write-Utf8File -Path $LogPath -Content $sanitizedJson
    return $credentials
}

function Get-SourceUsersFromApi {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [object[]]$StoredCredentials = @(),
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $trimmedBaseUrl = $BaseUrl.TrimEnd('/')
    $attempts = @()

    foreach ($credential in $StoredCredentials) {
        $username = [string]$credential.Username
        $password = [string]$credential.Password
        if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrEmpty($password)) {
            continue
        }

        try {
            $login = Invoke-RestMethod `
                -Method Post `
                -Uri ($trimmedBaseUrl + '/auth/login') `
                -ContentType 'application/json' `
                -Body (@{ username = $username; password = $password } | ConvertTo-Json) `
                -TimeoutSec 20

            $token = if ($login.token) { [string]$login.token } elseif ($login.accessToken) { [string]$login.accessToken } else { '' }
            if ([string]::IsNullOrWhiteSpace($token)) {
                throw "token missing for $username"
            }

            $users = @(
                Invoke-RestMethod `
                    -Method Get `
                    -Uri ($trimmedBaseUrl + '/users') `
                    -Headers @{ Authorization = "Bearer $token" } `
                    -TimeoutSec 20 |
                    ForEach-Object { $_ }
            )

            $payload = [pscustomobject]@{
                sourceBaseUrl = $trimmedBaseUrl
                loginUsername = $username
                userCount = $users.Count
                users = $users
            }

            Write-Utf8File -Path $LogPath -Content ($payload | ConvertTo-Json -Depth 30)
            return [pscustomobject]@{
                LoginUsername = $username
                Users = $users
            }
        }
        catch {
            $attempts += [pscustomobject]@{
                username = $username
                error = $_.Exception.Message
            }
        }
    }

    $failurePayload = [pscustomobject]@{
        sourceBaseUrl = $trimmedBaseUrl
        attempts = $attempts
        users = @()
    }
    Write-Utf8File -Path $LogPath -Content ($failurePayload | ConvertTo-Json -Depth 20)
    return $null
}

function Get-FallbackOperationalUsers {
    $adminPermissions = @(
        'Amount.ViewPurchase',
        'Amount.ViewSales',
        'CompanyProfile.Edit',
        'Data.BackupRestore',
        'Delivery.ViewAll',
        'Rental.EditAll',
        'Rental.Import',
        'Rental.SettingsEdit',
        'Rental.ViewAll',
        'Settings.Edit'
    )

    return @(
        [pscustomobject]@{
            username = 'admin'
            role = 'Admin'
            officeCode = 'USENET'
            tenantCode = 'USENET_GROUP'
            scopeType = 'Admin'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'usenet'
            role = 'Admin'
            officeCode = 'USENET'
            tenantCode = 'USENET_GROUP'
            scopeType = 'TenantAll'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'itworld'
            role = 'Admin'
            officeCode = 'ITWORLD'
            tenantCode = 'ITWORLD'
            scopeType = 'TenantAll'
            isActive = $true
            permissions = $adminPermissions
        },
        [pscustomobject]@{
            username = 'yeonsu'
            role = 'User'
            officeCode = 'YEONSU'
            tenantCode = 'USENET_GROUP'
            scopeType = 'OfficeOnly'
            isActive = $true
            permissions = @()
        }
    )
}

function Resolve-IsolatedUserDefinitions {
    param(
        [object[]]$SourceUsers = @(),
        [object[]]$StoredCredentials = @()
    )

    $passwordMap = @{}
    foreach ($credential in $StoredCredentials) {
        $username = [string]$credential.Username
        $password = [string]$credential.Password
        if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrEmpty($password)) {
            continue
        }

        $passwordMap[$username] = $password
    }

    foreach ($fallback in @(
        @{ Username = 'usenet'; Password = '1234' },
        @{ Username = 'itworld'; Password = '1234' },
        @{ Username = 'yeonsu'; Password = '1234' }
    )) {
        if (-not $passwordMap.ContainsKey($fallback.Username)) {
            $passwordMap[$fallback.Username] = $fallback.Password
        }
    }

    $resolvedUsers = @()
    foreach ($sourceUser in $SourceUsers) {
        $username = [string]$sourceUser.username
        if ([string]::IsNullOrWhiteSpace($username)) {
            continue
        }

        $permissions = @()
        if ($null -ne $sourceUser.permissions) {
            $permissions = @($sourceUser.permissions | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        }

        $resolvedUsers += [pscustomobject]@{
            Username = $username
            Role = if ([string]::IsNullOrWhiteSpace([string]$sourceUser.role)) { 'User' } else { [string]$sourceUser.role }
            OfficeCode = [string]$sourceUser.officeCode
            TenantCode = [string]$sourceUser.tenantCode
            ScopeType = [string]$sourceUser.scopeType
            IsActive = [bool]$sourceUser.isActive
            Permissions = $permissions
            Password = if ($passwordMap.ContainsKey($username)) { [string]$passwordMap[$username] } else { $null }
        }
    }

    return $resolvedUsers
}

function Sync-IsolatedServerUsers {
    param(
        [Parameter(Mandatory = $true)][string]$TargetBaseUrl,
        [Parameter(Mandatory = $true)][object[]]$Users,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $trimmedTargetBaseUrl = $TargetBaseUrl.TrimEnd('/')
    $actions = @()
    $verifications = @()

    $adminLogin = Invoke-RestMethod `
        -Method Post `
        -Uri ($trimmedTargetBaseUrl + '/auth/login') `
        -ContentType 'application/json' `
        -Body (@{ username = 'admin'; password = 'CHANGE_THIS_ADMIN_PASSWORD' } | ConvertTo-Json) `
        -TimeoutSec 20

    $adminToken = if ($adminLogin.token) { [string]$adminLogin.token } elseif ($adminLogin.accessToken) { [string]$adminLogin.accessToken } else { '' }
    if ([string]::IsNullOrWhiteSpace($adminToken)) {
        throw '격리 테스트 서버 admin 로그인 토큰을 가져오지 못했습니다.'
    }

    $headers = @{ Authorization = "Bearer $adminToken" }
    $existingUsers = @(
        Invoke-RestMethod -Method Get -Uri ($trimmedTargetBaseUrl + '/users') -Headers $headers -TimeoutSec 20 |
            ForEach-Object { $_ }
    )
    $existingByUsername = @{}
    foreach ($existingUser in $existingUsers) {
        $existingByUsername[[string]$existingUser.username] = $existingUser
    }

    $desiredUsernames = @($Users | ForEach-Object { [string]$_.Username })
    foreach ($existingUser in $existingUsers) {
        $existingUsername = [string]$existingUser.username
        if ($desiredUsernames -contains $existingUsername) {
            continue
        }

        if ($existingUsername -in @('user', 'itw')) {
            Invoke-RestMethod -Method Delete -Uri ($trimmedTargetBaseUrl + '/users/' + [string]$existingUser.id) -Headers $headers -TimeoutSec 20 | Out-Null
            $actions += [pscustomobject]@{
                action = 'delete'
                username = $existingUsername
            }
        }
    }

    foreach ($user in $Users) {
        $permissions = @($user.Permissions | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        $payload = @{
            username = [string]$user.Username
            role = [string]$user.Role
            tenantCode = [string]$user.TenantCode
            officeCode = [string]$user.OfficeCode
            scopeType = [string]$user.ScopeType
            isActive = [bool]$user.IsActive
            permissions = $permissions
        }

        $existingUser = $null
        if ($existingByUsername.ContainsKey([string]$user.Username)) {
            $existingUser = $existingByUsername[[string]$user.Username]
        }

        if ($null -ne $existingUser) {
            Invoke-RestMethod `
                -Method Put `
                -Uri ($trimmedTargetBaseUrl + '/users/' + [string]$existingUser.id) `
                -Headers $headers `
                -ContentType 'application/json' `
                -Body ($payload | ConvertTo-Json -Depth 10) `
                -TimeoutSec 20 | Out-Null

            if (-not [string]::IsNullOrWhiteSpace([string]$user.Password)) {
                Invoke-RestMethod `
                    -Method Put `
                    -Uri ($trimmedTargetBaseUrl + '/users/' + [string]$existingUser.id + '/password') `
                    -Headers $headers `
                    -ContentType 'application/json' `
                    -Body (@{ password = [string]$user.Password } | ConvertTo-Json) `
                    -TimeoutSec 20 | Out-Null
            }

            $actions += [pscustomobject]@{
                action = 'update'
                username = [string]$user.Username
                passwordUpdated = -not [string]::IsNullOrWhiteSpace([string]$user.Password)
            }
            continue
        }

        if ([string]::IsNullOrWhiteSpace([string]$user.Password)) {
            $actions += [pscustomobject]@{
                action = 'skip-create'
                username = [string]$user.Username
                reason = 'password-unresolved'
            }
            continue
        }

        $createPayload = @{
            username = [string]$user.Username
            password = [string]$user.Password
            role = [string]$user.Role
            tenantCode = [string]$user.TenantCode
            officeCode = [string]$user.OfficeCode
            scopeType = [string]$user.ScopeType
            isActive = [bool]$user.IsActive
            permissions = $permissions
        }

        Invoke-RestMethod `
            -Method Post `
            -Uri ($trimmedTargetBaseUrl + '/users') `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($createPayload | ConvertTo-Json -Depth 10) `
            -TimeoutSec 20 | Out-Null

        $actions += [pscustomobject]@{
            action = 'create'
            username = [string]$user.Username
        }
    }

    foreach ($user in $Users | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Password) }) {
        try {
            $login = Invoke-RestMethod `
                -Method Post `
                -Uri ($trimmedTargetBaseUrl + '/auth/login') `
                -ContentType 'application/json' `
                -Body (@{ username = [string]$user.Username; password = [string]$user.Password } | ConvertTo-Json) `
                -TimeoutSec 20

            $verifications += [pscustomobject]@{
                username = [string]$user.Username
                ok = $true
                role = [string]$login.user.role
                officeCode = [string]$login.user.officeCode
            }
        }
        catch {
            $statusCode = -1
            if ($_.Exception.Response) {
                $statusCode = [int]$_.Exception.Response.StatusCode
            }

            $verifications += [pscustomobject]@{
                username = [string]$user.Username
                ok = $false
                status = $statusCode
                error = $_.Exception.Message
            }
        }
    }

    $logPayload = [pscustomobject]@{
        targetBaseUrl = $trimmedTargetBaseUrl
        actions = $actions
        verifications = $verifications
    }
    Write-Utf8File -Path $LogPath -Content ($logPayload | ConvertTo-Json -Depth 30)

    $requiredFailures = @(
        $verifications |
            Where-Object {
                (-not $_.ok) -and
                (@('usenet', 'itworld', 'yeonsu') -contains [string]$_.username)
            }
    )

    if ($requiredFailures.Count -gt 0) {
        $failedUsers = $requiredFailures | ForEach-Object { [string]$_.username }
        throw "격리 테스트 서버 계정 부트스트랩 실패: $($failedUsers -join ', ')"
    }
}

function Copy-CurrentAppSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$TargetRoot
    )

    if (-not (Test-Path -LiteralPath $SourceRoot)) {
        New-Item -ItemType Directory -Force -Path $TargetRoot | Out-Null
        foreach ($child in @('data', 'attachments', 'backup', 'diagnostics', 'logs', 'temp')) {
            New-Item -ItemType Directory -Force -Path (Join-Path $TargetRoot $child) | Out-Null
        }

        return [pscustomobject]@{
            SourceExists = $false
            DatabaseSource = ''
            UsedBackupFallback = $false
        }
    }

    Invoke-RobocopyMirror `
        -Source $SourceRoot `
        -Destination $TargetRoot `
        -ExcludeDirectories @('backup', 'diagnostics', 'logs', 'temp') `
        -ExcludeFiles @('거래플랜.db', '거래플랜.db-shm', '거래플랜.db-wal', '*.db-shm', '*.db-wal')
    Reset-TransientAppDataDirectories -Root $TargetRoot

    foreach ($child in @('data', 'attachments', 'backup', 'diagnostics', 'logs', 'temp')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $TargetRoot $child) | Out-Null
    }

    $sourceDb = Join-Path $SourceRoot 'data\거래플랜.db'
    $targetDb = Join-Path $TargetRoot 'data\거래플랜.db'
    $databaseSource = $sourceDb
    $usedBackupFallback = $false

    foreach ($sqliteSidecar in @(
        (Join-Path $TargetRoot 'data\거래플랜.db-shm'),
        (Join-Path $TargetRoot 'data\거래플랜.db-wal')
    )) {
        Remove-Item -LiteralPath $sqliteSidecar -Force -ErrorAction SilentlyContinue
    }

    try {
        if (-not (Test-Path -LiteralPath $sourceDb)) {
            throw 'source db missing'
        }

        Copy-Item -LiteralPath $sourceDb -Destination $targetDb -Force
    }
    catch {
        $backupDb = Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'backup') -File -Filter '거래플랜*.db' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($null -eq $backupDb) {
            throw "현재 로컬 DB 복사에 실패했고 대체 백업도 찾지 못했습니다. source=$sourceDb"
        }

        Copy-Item -LiteralPath $backupDb.FullName -Destination $targetDb -Force
        $databaseSource = $backupDb.FullName
        $usedBackupFallback = $true
    }

    foreach ($sqliteSidecar in @(
        (Join-Path $TargetRoot 'data\거래플랜.db-shm'),
        (Join-Path $TargetRoot 'data\거래플랜.db-wal')
    )) {
        Remove-Item -LiteralPath $sqliteSidecar -Force -ErrorAction SilentlyContinue
    }

    return [pscustomobject]@{
        SourceExists = $true
        DatabaseSource = $databaseSource
        UsedBackupFallback = $usedBackupFallback
    }
}

function Reset-IsolatedServerStorage {
    param(
        [Parameter(Mandatory = $true)][string]$ServerOutput,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot
    )

    foreach ($path in @(
        (Join-Path $ServerOutput '거래플랜-local.db'),
        (Join-Path $ServerOutput 'salesmaster-local.db'),
        (Join-Path $ServerOutput 'App_Data'),
        $ServerDataRoot
    )) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Repair-ProcessPathEnvironmentForChildProcess {
    $pathValue = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if ([string]::IsNullOrWhiteSpace($pathValue)) {
        $pathValue = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    }

    if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
        [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
        [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
    }
}

function Start-IsolatedServerProcess {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$FileStorageRoot,
        [Parameter(Mandatory = $true)][string]$UpdatesRoot
    )

    $serverUrl = "http://127.0.0.1:$Port"

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = $serverUrl
        'Kestrel__Endpoints__Http__Url' = $serverUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'FileStorage__RootPath' = $FileStorageRoot
        'Updates__StorageRoot' = $UpdatesRoot
    }

    $previousEnv = @{}
    foreach ($key in $serverEnv.Keys) {
        $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$serverEnv[$key], 'Process')
    }

    try {
        Repair-ProcessPathEnvironmentForChildProcess
        $argumentList = ('"{0}" --environment Development' -f $ServerDll.Replace('"', '""'))
        $process = Start-Process -FilePath $DotnetExe -ArgumentList $argumentList -WorkingDirectory $ServerWorkingDirectory -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $serverEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnv[$key], 'Process')
        }
    }

    return [pscustomobject]@{
        Process = $process
        ServerUrl = $serverUrl
    }
}

function Stop-IsolatedServerProcess {
    param($State)

    if ($null -eq $State) {
        return
    }

    if ($State.Process) {
        try {
            & taskkill /PID $State.Process.Id /T /F > $null 2>&1
        }
        catch {
        }

        try {
            if (-not $State.Process.HasExited) {
                $State.Process.Kill()
            }
        }
        catch {
        }
    }

}

function Stop-IsolatedRuntimeProcesses {
    param([Parameter(Mandatory = $true)][string]$OutputRoot)

    $normalizedOutputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    $targets = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $executablePath = $_.ExecutablePath
            $commandLine = $_.CommandLine
            ($executablePath -and $executablePath.StartsWith($normalizedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) -or
            ($commandLine -and $commandLine.IndexOf($normalizedOutputRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        }

    foreach ($target in $targets) {
        try {
            Stop-Process -Id $target.ProcessId -Force -ErrorAction Stop
            Start-Sleep -Milliseconds 200
        }
        catch {
        }
    }
}

function Write-TestRunScripts {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$DefaultBaseUrl,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $runAppContent = @"
@echo off
setlocal EnableExtensions
set "APP_ROOT=%~dp0AppData"
set "APP_EXE="
for %%I in ("%~dp0App\*.Desktop.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
for %%I in ("%~dp0App\*.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
if not defined APP_EXE (
  echo [GeoraePlan] App exe not found in %~dp0App
  pause
  exit /b 1
)
set "GEORAEPLAN_APP_ROOT=%APP_ROOT%"
set "GEORAEPLAN_DISABLE_LEGACY_MERGE=1"
set "GEORAEPLAN_TEST_MODE=1"
start "GeoraePlan Test App" /D "%~dp0App" "%APP_EXE%"
endlocal
"@

    $runServerContent = @"
@echo off
setlocal EnableExtensions
set "DOTNET_EXE=$DotnetExe"
set "SERVER_DLL="
for %%I in ("%~dp0Server\*.Server.Api.dll") do if not defined SERVER_DLL set "SERVER_DLL=%%~fI"
set "APP_SETTINGS=%~dp0App\appsettings.json"
set "SET_API_SCRIPT=%~dp0Set-ApiBaseUrl.ps1"
set "SCAN_PORT=19080"
set "SERVER_DATA_ROOT=%~dp0ServerData"
if not exist "%DOTNET_EXE%" (
  echo [GeoraePlan] dotnet not found: %DOTNET_EXE%
  pause
  exit /b 1
)
if not exist "%SERVER_DLL%" (
  echo [GeoraePlan] Server dll not found: %SERVER_DLL%
  pause
  exit /b 1
)
call :FIND_FREE_PORT %SCAN_PORT%
if not defined SERVER_PORT set "SERVER_PORT=19080"
set "SERVER_URL=http://127.0.0.1:%SERVER_PORT%"
if exist "%APP_SETTINGS%" if exist "%SET_API_SCRIPT%" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%SET_API_SCRIPT%" -BaseUrl "%SERVER_URL%" -AppSettingsPaths "%APP_SETTINGS%"
)
set "ASPNETCORE_ENVIRONMENT=Development"
set "DOTNET_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=%SERVER_URL%"
set "Kestrel__Endpoints__Http__Url=%SERVER_URL%"
set "ERP_DB_FALLBACK_SQLITE=1"
set "SeedUsers__EnableSeedUsers=true"
set "SeedUsers__UsenetUsername=usenet"
set "SeedUsers__UsenetPassword=CHANGE_THIS_USENET_PASSWORD"
set "SeedUsers__UpdateExistingUsenetPassword=true"
set "Logging__LogLevel__Default=Warning"
set "Logging__LogLevel__Microsoft=Warning"
set "Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning"
set "FileStorage__RootPath=%SERVER_DATA_ROOT%\FileStore"
set "Updates__StorageRoot=%SERVER_DATA_ROOT%\updates"
pushd "%~dp0Server"
echo [GeoraePlan] Starting isolated test server on %SERVER_URL%
"%DOTNET_EXE%" "%SERVER_DLL%" --environment Development
popd
echo.
echo GeoraePlan test server has stopped.
pause
exit /b 0

:FIND_FREE_PORT
set "SERVER_PORT=%~1"
if "%SERVER_PORT%"=="" set "SERVER_PORT=19080"
:FIND_FREE_PORT_LOOP
netstat -ano | findstr /R /C:":%SERVER_PORT% .*LISTENING" >nul
if not errorlevel 1 (
  set /a SERVER_PORT+=1
  goto :FIND_FREE_PORT_LOOP
)
exit /b 0
"@

    $runAllPsContent = @'
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-FreePort {
    param([int]$StartingPort = 19080)

    $port = $StartingPort
    while ($true) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
            $listener.Start()
            return $port
        }
        catch {
            $port++
        }
        finally {
            if ($null -ne $listener) {
                try { $listener.Stop() } catch { }
            }
        }
    }
}

function Wait-HttpReady {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSeconds = 40
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function Repair-ProcessPathEnvironmentForChildProcess {
    $pathValue = [Environment]::GetEnvironmentVariable('Path', 'Process')
    if ([string]::IsNullOrWhiteSpace($pathValue)) {
        $pathValue = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    }

    if (-not [string]::IsNullOrWhiteSpace($pathValue)) {
        [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
        [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
    }
}

function Start-HiddenServerProcess {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$ServerDir,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerUrl,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot
    )

    $serverEnv = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'DOTNET_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = $ServerUrl
        'Kestrel__Endpoints__Http__Url' = $ServerUrl
        'ERP_DB_FALLBACK_SQLITE' = '1'
        'SeedUsers__EnableSeedUsers' = 'true'
        'SeedUsers__UsenetUsername' = 'usenet'
        'SeedUsers__UsenetPassword' = '1234'
        'SeedUsers__UpdateExistingUsenetPassword' = 'true'
        'Logging__LogLevel__Default' = 'Warning'
        'Logging__LogLevel__Microsoft' = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore' = 'Warning'
        'FileStorage__RootPath' = (Join-Path $ServerDataRoot 'FileStore')
        'Updates__StorageRoot' = (Join-Path $ServerDataRoot 'updates')
    }

    $previousEnv = @{}
    foreach ($key in $serverEnv.Keys) {
        $previousEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$serverEnv[$key], 'Process')
    }

    try {
        Repair-ProcessPathEnvironmentForChildProcess
        $argumentList = ('"{0}" --environment Development' -f $ServerDll.Replace('"', '""'))
        return Start-Process -FilePath $DotnetExe -ArgumentList $argumentList -WorkingDirectory $ServerDir -WindowStyle Hidden -PassThru
    }
    finally {
        foreach ($key in $serverEnv.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previousEnv[$key], 'Process')
        }
    }
}

$dotnetExe = '__DOTNET_EXE__'
$serverDir = Join-Path $PSScriptRoot 'Server'
$appDir = Join-Path $PSScriptRoot 'App'
$appSettings = Join-Path $appDir 'appsettings.json'
$setApiScript = Join-Path $PSScriptRoot 'Set-ApiBaseUrl.ps1'
$appRoot = Join-Path $PSScriptRoot 'AppData'
$serverDataRoot = Join-Path $PSScriptRoot 'ServerData'
$errorLogPath = Join-Path $PSScriptRoot 'Run-All-error.log'
$traceLogPath = Join-Path $PSScriptRoot 'Run-All.log'
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\GeoraePlan_Test_RunAll_Launcher', [ref]$createdNew)
$serverProcess = $null
$appProcess = $null

function Write-Log {
    param([string]$Message)
    Add-Content -LiteralPath $traceLogPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -Encoding UTF8
}

function Initialize-TestUpdateManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ServerDataRoot
    )

    $mobileDir = Join-Path $PSScriptRoot 'Mobile'
    $apkSource = Join-Path $mobileDir '거래플랜-Mobile-Test-Debug.apk'
    if (-not (Test-Path -LiteralPath $apkSource)) {
        $apkSource = Join-Path $mobileDir 'kr.georaeplan.mobile-Signed.apk'
    }

    if (-not (Test-Path -LiteralPath $apkSource)) {
        Write-Log 'Mobile APK not found. Skipping test update manifest.'
        return
    }

    $updatesRoot = Join-Path $ServerDataRoot 'updates'
    $manifestRoot = Join-Path $updatesRoot 'manifest'
    $downloadRoot = Join-Path $updatesRoot 'downloads\android'
    New-Item -ItemType Directory -Force -Path $manifestRoot, $downloadRoot | Out-Null

    $apkTargetName = '거래플랜-Mobile-Test-Debug.apk'
    $apkTarget = Join-Path $downloadRoot $apkTargetName
    Copy-Item -LiteralPath $apkSource -Destination $apkTarget -Force

    $apkFile = Get-Item -LiteralPath $apkTarget
    $hash = Get-FileHash -LiteralPath $apkTarget -Algorithm SHA256
    $stableManifestPath = Join-Path $manifestRoot 'stable.json'
    $existingDesktopManifest = $null
    if (Test-Path -LiteralPath $stableManifestPath) {
        try {
            $existingManifest = Get-Content -LiteralPath $stableManifestPath -Raw | ConvertFrom-Json
            if ($null -ne $existingManifest.desktop) {
                $existingDesktopManifest = $existingManifest.desktop
            }
        }
        catch {
            Write-Log ("Existing test update manifest could not be read. Rebuilding mobile entry only first. error={0}" -f $_.Exception.Message)
        }
    }

    $manifest = [ordered]@{
        channel = 'stable'
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        android = [ordered]@{
            platform = 'android'
            version = '0.2.18'
            mandatory = $false
            minimumSupportedVersion = '0.2.18'
            fileName = $apkTargetName
            packageUrl = ''
            sha256 = $hash.Hash
            fileSize = $apkFile.Length
            notes = '테스트 실행환경 모바일 APK입니다.'
            releasedAtUtc = [DateTime]::UtcNow.ToString('O')
        }
    }

    if ($null -ne $existingDesktopManifest) {
        $manifest.desktop = $existingDesktopManifest
    }
    else {
        $desktopDownloadRoot = Join-Path $updatesRoot 'downloads\desktop'
        $desktopPackage = Get-ChildItem -LiteralPath $desktopDownloadRoot -File -Filter 'tradeplan-pc-installer-*.zip' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $desktopPackage) {
            $desktopVersion = 'latest'
            $versionMatch = [regex]::Match($desktopPackage.Name, 'v(?<version>\d+(?:\.\d+)+)')
            if ($versionMatch.Success) {
                $desktopVersion = $versionMatch.Groups['version'].Value
            }

            $desktopHash = Get-FileHash -LiteralPath $desktopPackage.FullName -Algorithm SHA256
            $manifest.desktop = [ordered]@{
                platform = 'desktop'
                version = $desktopVersion
                mandatory = $false
                minimumSupportedVersion = $desktopVersion
                fileName = $desktopPackage.Name
                packageUrl = ''
                sha256 = $desktopHash.Hash
                fileSize = $desktopPackage.Length
                notes = '테스트 실행환경 PC 업데이트 패키지입니다.'
                releasedAtUtc = [DateTime]::UtcNow.ToString('O')
            }
        }
        else {
            $desktopPackage = Join-Path $desktopDownloadRoot 'tradeplan-pc-installer-vtest.zip'
            $desktopAppRoot = Join-Path $PSScriptRoot 'App'
            if (Test-Path -LiteralPath $desktopAppRoot) {
                New-Item -ItemType Directory -Force -Path $desktopDownloadRoot | Out-Null
                Remove-Item -LiteralPath $desktopPackage -Force -ErrorAction SilentlyContinue
                Compress-Archive -Path (Join-Path $desktopAppRoot '*') -DestinationPath $desktopPackage -Force
                $desktopPackage = Get-Item -LiteralPath $desktopPackage
                Write-Log ("Desktop update package was not found. Created test package from App folder: {0}" -f $desktopPackage.FullName)

                $desktopHash = Get-FileHash -LiteralPath $desktopPackage.FullName -Algorithm SHA256
                $manifest.desktop = [ordered]@{
                    platform = 'desktop'
                    version = 'test'
                    mandatory = $false
                    minimumSupportedVersion = 'test'
                    fileName = $desktopPackage.Name
                    packageUrl = ''
                    sha256 = $desktopHash.Hash
                    fileSize = $desktopPackage.Length
                    notes = '테스트 실행환경 PC 앱 패키지입니다.'
                    releasedAtUtc = [DateTime]::UtcNow.ToString('O')
                }
            }
            else {
                Write-Log 'Desktop update package not found and App folder is missing. Test manifest will contain android entry only.'
            }
        }
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath $stableManifestPath -Value $manifestJson -Encoding UTF8
    Write-Log ("Test update manifest prepared. apk={0}; desktopPreserved={1}" -f $apkTarget, ($null -ne $manifest.desktop))
}

Set-Content -LiteralPath $traceLogPath -Value @() -Encoding UTF8
Remove-Item -LiteralPath $errorLogPath -Force -ErrorAction SilentlyContinue
Write-Log 'Run-All.ps1 started.'

if (-not $createdNew) {
    Write-Log 'Another Run-All launcher is already active. Exiting.'
    exit 0
}

try {
    Write-Log 'Resolving app/server files.'
    $serverDll = Get-ChildItem -LiteralPath $serverDir -Filter '*.Server.Api.dll' -File | Select-Object -First 1 -ExpandProperty FullName
    $appExe = Get-ChildItem -LiteralPath $appDir -Filter '*.Desktop.App.exe' -File | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($appExe)) {
        $appExe = Get-ChildItem -LiteralPath $appDir -Filter '*.App.exe' -File | Select-Object -First 1 -ExpandProperty FullName
    }

    if (-not (Test-Path -LiteralPath $dotnetExe)) {
        throw "dotnet not found: $dotnetExe"
    }

    if ([string]::IsNullOrWhiteSpace($serverDll) -or -not (Test-Path -LiteralPath $serverDll)) {
        throw "Server dll not found in $serverDir"
    }

    if ([string]::IsNullOrWhiteSpace($appExe) -or -not (Test-Path -LiteralPath $appExe)) {
        throw "App exe not found in $appDir"
    }

    Write-Log 'App/server files resolved.'
    Initialize-TestUpdateManifest -ServerDataRoot $serverDataRoot
    $scanPort = 19080
    $serverReady = $false
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        $port = Get-FreePort -StartingPort $scanPort
        $serverUrl = "http://127.0.0.1:$port"

        Write-Log ("Server start attempt #{0} on {1}" -f $attempt, $serverUrl)
        if ((Test-Path -LiteralPath $appSettings) -and (Test-Path -LiteralPath $setApiScript)) {
            Write-Log 'Updating appsettings Api.BaseUrl.'
            & powershell -NoProfile -ExecutionPolicy Bypass -File $setApiScript -BaseUrl $serverUrl -AppSettingsPaths @($appSettings) | Out-Null
        }

        Write-Log 'Launching hidden test server process.'
        $serverProcess = Start-HiddenServerProcess -DotnetExe $dotnetExe -ServerDir $serverDir -ServerDll $serverDll -ServerUrl $serverUrl -ServerDataRoot $serverDataRoot
        Write-Log ("Hidden test server process started. pid={0}" -f $serverProcess.Id)
        Start-Sleep -Milliseconds 300
        if ($serverProcess.HasExited) {
            Write-Log ("Server exited before health check. exitCode={0}" -f $serverProcess.ExitCode)
            $serverProcess = $null
            $scanPort = $port + 1
            continue
        }

        if (Wait-HttpReady -Url ($serverUrl + '/healthz')) {
            Write-Log 'Test server reported healthy.'
            $serverReady = $true
            break
        }

        if ($serverProcess -and $serverProcess.HasExited) {
            Write-Log ("Server exited during health check wait. exitCode={0}" -f $serverProcess.ExitCode)
        }
        else {
            Write-Log 'Server health check failed while process was still running. Stopping process and retrying.'
        }

        if ($serverProcess -and -not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 300
        }

        $serverProcess = $null
        $scanPort = $port + 1
    }

    if (-not $serverReady) {
        throw 'Failed to start isolated test server. Use Run-Server.cmd for details.'
    }

    Write-Log 'Launching test app.'
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_APP_ROOT', $appRoot, 'Process')
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_MODE', '1', 'Process')
    $appProcess = Start-Process -FilePath $appExe -WorkingDirectory $appDir -PassThru
    Write-Log ("Test app started. pid={0}" -f $appProcess.Id)
    Wait-Process -Id $appProcess.Id
    Write-Log 'Test app exited. Cleaning up server.'
}
catch {
    $message = $_.Exception.ToString()
    Write-Log ("ERROR: {0}" -f $message)
    Set-Content -LiteralPath $errorLogPath -Value $message -Encoding UTF8

    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show(
            "테스트 실행에 실패했습니다.`r`n`r`nRun-Server.cmd 로 서버 로그를 확인해주세요.`r`n오류 로그: $errorLogPath",
            '거래플랜 테스트 실행 오류') | Out-Null
    }
    catch {
    }

    Start-Process -FilePath (Join-Path $PSScriptRoot 'Run-Server.cmd')
}
finally {
    Write-Log 'Run-All.ps1 entering cleanup.'
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
        Write-Log 'Hidden test server process stopped.'
    }

    if ($mutex) {
        try {
            if ($createdNew) {
                $mutex.ReleaseMutex()
            }
        }
        catch {
        }

        $mutex.Dispose()
    }
}
'@

    $runAllContent = @"
@echo off
setlocal EnableExtensions
set "RUN_ALL_PS=%~dp0Run-All.ps1"
if not exist "%RUN_ALL_PS%" (
  echo [GeoraePlan] Run-All.ps1 not found: %RUN_ALL_PS%
  pause
  exit /b 1
)
start "" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%RUN_ALL_PS%"
exit /b 0
"@

    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-App.cmd') -Content $runAppContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-Server.cmd') -Content $runServerContent.Trim()
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-All.ps1') -Content ($runAllPsContent.Replace('__DOTNET_EXE__', $DotnetExe)) -WithBom
    Write-Utf8File -Path (Join-Path $OutputRoot 'Run-All.cmd') -Content $runAllContent.Trim()
}

function Initialize-IsolatedServerData {
    param(
        [Parameter(Mandatory = $true)][string]$DotnetExe,
        [Parameter(Mandatory = $true)][string]$SyncDiagProject,
        [Parameter(Mandatory = $true)][string]$TestAppRoot,
        [Parameter(Mandatory = $true)][string]$ServerDll,
        [Parameter(Mandatory = $true)][string]$ServerWorkingDirectory,
        [Parameter(Mandatory = $true)][string]$SeedLogRoot,
        [Parameter(Mandatory = $true)][string]$ServerDataRoot,
        [Parameter(Mandatory = $true)][string]$SourceApiBaseUrl,
        [switch]$AllowDirtySeedFailure
    )

    if (-not (Test-Path -LiteralPath (Join-Path $TestAppRoot 'data\거래플랜.db'))) {
        throw "테스트 앱 데이터베이스가 없어 테스트 서버 시드를 만들 수 없습니다: $(Join-Path $TestAppRoot 'data\거래플랜.db')"
    }

    New-Item -ItemType Directory -Force -Path $SeedLogRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $ServerDataRoot | Out-Null

    $prepareResult = Invoke-WithProcessEnvironment -Variables @{
        GEORAEPLAN_APP_ROOT = $TestAppRoot
        GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
    } -Action {
        Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'prepare-test-seed')
    }

    Write-Utf8File -Path (Join-Path $SeedLogRoot 'prepare-test-seed.log') -Content $prepareResult.Text
    if ($prepareResult.ExitCode -ne 0) {
        throw "테스트 앱 데이터 시드 준비 실패`n$($prepareResult.Text)"
    }

    $seedPort = Get-FreeTcpPort -StartingPort 19080
    $serverState = Start-IsolatedServerProcess -DotnetExe $DotnetExe -ServerDll $ServerDll -ServerWorkingDirectory $ServerWorkingDirectory -Port $seedPort -FileStorageRoot (Join-Path $ServerDataRoot 'FileStore') -UpdatesRoot (Join-Path $ServerDataRoot 'updates')

    try {
        if (-not (Wait-HttpReady -Url ($serverState.ServerUrl + '/healthz') -TimeoutSeconds 50)) {
            throw "테스트 서버 기동 확인 실패. url=$($serverState.ServerUrl) dll=$ServerDll"
        }

        $preSyncResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_APP_ROOT = $TestAppRoot
            GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
            GEORAEPLAN_SYNC_USERNAME = 'admin'
            GEORAEPLAN_SYNC_PASSWORD = 'CHANGE_THIS_ADMIN_PASSWORD'
            GEORAEPLAN_SYNC_BASEURL = ($serverState.ServerUrl + '/')
        } -Action {
            Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'preseed-sync')
        }

        Write-Utf8File -Path (Join-Path $SeedLogRoot 'pre-seed-sync.log') -Content $preSyncResult.Text
        $preSeedSyncSucceeded = $preSyncResult.ExitCode -eq 0 -and $preSyncResult.Text -match 'sync_ok=(True|true)'
        if (-not $preSeedSyncSucceeded) {
            throw "테스트 서버 기본 데이터 동기화 준비 실패`n$($preSyncResult.Text)"
        }

        $markResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_APP_ROOT = $TestAppRoot
            GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
        } -Action {
            Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'mark-all-dirty')
        }

        Write-Utf8File -Path (Join-Path $SeedLogRoot 'mark-all-dirty.log') -Content $markResult.Text
        if ($markResult.ExitCode -ne 0) {
            throw "테스트 앱 데이터 dirty 표시 실패`n$($markResult.Text)"
        }

        $syncResult = Invoke-WithProcessEnvironment -Variables @{
            GEORAEPLAN_APP_ROOT = $TestAppRoot
            GEORAEPLAN_DISABLE_LEGACY_MERGE = '1'
            GEORAEPLAN_SYNC_USERNAME = 'admin'
            GEORAEPLAN_SYNC_PASSWORD = 'CHANGE_THIS_ADMIN_PASSWORD'
            GEORAEPLAN_SYNC_BASEURL = ($serverState.ServerUrl + '/')
        } -Action {
            Invoke-DotnetWithOutput -DotnetExe $DotnetExe -Arguments @('run', '--project', $SyncDiagProject, '--', 'sync')
        }

        Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-sync.log') -Content $syncResult.Text
        $seedSyncSucceeded = $syncResult.ExitCode -eq 0 -and $syncResult.Text -match 'sync_ok=(True|true)'
        if (-not $seedSyncSucceeded) {
            $seedSyncWarning = @(
                '테스트 서버 데이터 시드 동기화가 완료되지 않았습니다.',
                '로컬 AppData에 미동기화/충돌 데이터가 있을 때 발생할 수 있습니다.',
                '테스트 실행환경은 계속 만들고, 로그인/화면 미리보기용 사용자 시드는 계속 진행합니다.',
                '',
                $syncResult.Text
            ) -join [Environment]::NewLine
            Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-sync-warning.log') -Content $seedSyncWarning

            if (-not $AllowDirtySeedFailure) {
                Write-Warning $seedSyncWarning
            }
        }

        $storedCredentials = @(
            Get-StoredSyncCredentialsFromLocalState `
                -DotnetExe $DotnetExe `
                -SyncDiagProject $SyncDiagProject `
                -AppRoot $TestAppRoot `
                -LogPath (Join-Path $SeedLogRoot 'stored-sync-credentials.log')
        )

        $sourceUsersSnapshot = Get-SourceUsersFromApi `
            -BaseUrl $SourceApiBaseUrl `
            -StoredCredentials $storedCredentials `
            -LogPath (Join-Path $SeedLogRoot 'source-users.json')

        $sourceUsers = if ($null -ne $sourceUsersSnapshot -and @($sourceUsersSnapshot.Users).Count -gt 0) {
            @($sourceUsersSnapshot.Users)
        }
        else {
            Get-FallbackOperationalUsers
        }

        $resolvedUsers = Resolve-IsolatedUserDefinitions -SourceUsers $sourceUsers -StoredCredentials $storedCredentials
        $resolvedUsersSanitized = @(
            $resolvedUsers | ForEach-Object {
                [pscustomobject]@{
                    Username = [string]$_.Username
                    Role = [string]$_.Role
                    OfficeCode = [string]$_.OfficeCode
                    TenantCode = [string]$_.TenantCode
                    ScopeType = [string]$_.ScopeType
                    IsActive = [bool]$_.IsActive
                    Permissions = @($_.Permissions)
                    PasswordResolved = -not [string]::IsNullOrWhiteSpace([string]$_.Password)
                }
            }
        )
        Write-Utf8File -Path (Join-Path $SeedLogRoot 'resolved-users.json') -Content ($resolvedUsersSanitized | ConvertTo-Json -Depth 20)

        Sync-IsolatedServerUsers `
            -TargetBaseUrl $serverState.ServerUrl `
            -Users $resolvedUsers `
            -LogPath (Join-Path $SeedLogRoot 'user-bootstrap.json')

        $seedSummary = @(
            "seed_server_url=$($serverState.ServerUrl)",
            "seed_sync_log=$(Join-Path $SeedLogRoot 'seed-sync.log')",
            "seed_sync_succeeded=$seedSyncSucceeded",
            "source_api_base_url=$SourceApiBaseUrl",
            "user_bootstrap_log=$(Join-Path $SeedLogRoot 'user-bootstrap.json')"
        ) -join [Environment]::NewLine
        Write-Utf8File -Path (Join-Path $SeedLogRoot 'seed-summary.txt') -Content $seedSummary
    }
    finally {
        Stop-IsolatedServerProcess -State $serverState
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot '실행환경'
}
if ([string]::IsNullOrWhiteSpace($SourceAppRoot)) {
    $SourceAppRoot = Join-Path $env:LOCALAPPDATA '거래플랜'
}

$solutionPath = (Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1 -ExpandProperty FullName)
if ([string]::IsNullOrWhiteSpace($solutionPath)) {
    throw "솔루션 파일을 찾지 못했습니다: $ProjectRoot"
}

$desktopProject = Find-FirstFile -Root (Join-Path $ProjectRoot 'Desktop') -Filter '*.Desktop.App.csproj'
$serverProject = Find-FirstFile -Root (Join-Path $ProjectRoot 'Server') -Filter '*.Server.Api.csproj'
$syncDiagProject = Join-Path $ProjectRoot '.tmp\syncdiag\syncdiag.csproj'
$deploymentRoot = Find-DeploymentRoot -ProjectRoot $ProjectRoot
$templatePath = Join-Path $scriptRoot '검증 체크리스트 템플릿.md'
$recordsRoot = Join-Path $scriptRoot '기록'
$changedFilesPath = Join-Path $scriptRoot '최근 수정 파일.md'
$currentChecklistPath = Join-Path $scriptRoot '검증 체크리스트.md'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$sessionRoot = Join-Path $recordsRoot $timestamp
$appOutput = Join-Path $OutputRoot 'App'
$serverOutput = Join-Path $OutputRoot 'Server'
$isolatedAppRoot = Join-Path $OutputRoot 'AppData'
$serverDataRoot = Join-Path $OutputRoot 'ServerData'
$defaultBaseUrl = 'http://127.0.0.1:19080'
$setApiSource = Join-Path $deploymentRoot 'Set-ApiBaseUrl.ps1'
$serverDll = Join-Path $serverOutput '거래플랜.Server.Api.dll'
$desktopAppSettingsPath = Join-Path (Split-Path -Parent $desktopProject) 'appsettings.json'
$sourceApiBaseUrl = 'https://api.example.invalid'

foreach ($requiredPath in @($desktopProject, $serverProject, $syncDiagProject, $templatePath, $setApiSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "필수 경로를 찾지 못했습니다: $requiredPath"
    }
}

if (Test-Path -LiteralPath $desktopAppSettingsPath) {
    try {
        $desktopAppSettings = Get-Content -LiteralPath $desktopAppSettingsPath -Raw | ConvertFrom-Json
        $configuredBaseUrl = [string]$desktopAppSettings.Api.BaseUrl
        if (-not [string]::IsNullOrWhiteSpace($configuredBaseUrl)) {
            $sourceApiBaseUrl = $configuredBaseUrl
        }
    }
    catch {
    }
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

if (-not $SkipBuild) {
    Invoke-Dotnet -DotnetExe $dotnetExe -Arguments @('build', $solutionPath, '-c', $Configuration, '-nodeReuse:false', '/p:UseSharedCompilation=false')
}

Stop-IsolatedRuntimeProcesses -OutputRoot $OutputRoot
Remove-Item -LiteralPath $appOutput -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $serverOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null

Invoke-Dotnet -DotnetExe $dotnetExe -Arguments @('publish', $desktopProject, '-c', $Configuration, '-o', $appOutput)
Invoke-Dotnet -DotnetExe $dotnetExe -Arguments @('publish', $serverProject, '-c', $Configuration, '-o', $serverOutput)

if (-not (Test-Path -LiteralPath $serverDll)) {
    throw "테스트 서버 DLL을 찾지 못했습니다: $serverOutput"
}

Copy-Item -LiteralPath $setApiSource -Destination (Join-Path $OutputRoot 'Set-ApiBaseUrl.ps1') -Force
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $OutputRoot 'Set-ApiBaseUrl.ps1') -BaseUrl $defaultBaseUrl -AppSettingsPaths @((Join-Path $appOutput 'appsettings.json'))
if ($LASTEXITCODE -ne 0) {
    throw '로컬 테스트용 Api.BaseUrl 설정에 실패했습니다.'
}

Write-TestRunScripts -OutputRoot $OutputRoot -DefaultBaseUrl $defaultBaseUrl -DotnetExe $dotnetExe

$dataSnapshotResult = $null
if (-not $SkipDataCopy) {
    $dataSnapshotResult = Copy-CurrentAppSnapshot -SourceRoot $SourceAppRoot -TargetRoot $isolatedAppRoot
}
else {
    foreach ($child in @('data', 'attachments', 'backup', 'diagnostics', 'logs', 'temp')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $isolatedAppRoot $child) | Out-Null
    }

    $dataSnapshotResult = [pscustomobject]@{
        SourceExists = Test-Path -LiteralPath $SourceAppRoot
        DatabaseSource = ''
        UsedBackupFallback = $false
    }
}

Reset-IsolatedServerStorage -ServerOutput $serverOutput -ServerDataRoot $serverDataRoot

$seedSucceeded = $false
$seedSkippedReason = ''
if (-not $SkipServerSeed) {
    Initialize-IsolatedServerData -DotnetExe $dotnetExe -SyncDiagProject $syncDiagProject -TestAppRoot $isolatedAppRoot -ServerDll $serverDll -ServerWorkingDirectory $serverOutput -SeedLogRoot (Join-Path $sessionRoot 'server-seed') -ServerDataRoot $serverDataRoot -SourceApiBaseUrl $sourceApiBaseUrl -AllowDirtySeedFailure:$AllowDirtySeedFailure
    $seedSucceeded = $true
}
else {
    $seedSkippedReason = '사용자 옵션으로 서버 시드를 건너뜀'
}

$generatedAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
$branch = (Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') -AllowFailure).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { $branch = 'unknown' }
$commit = (Get-GitOutput -ProjectRoot $ProjectRoot -Arguments @('rev-parse', 'HEAD') -AllowFailure).Trim()
if ([string]::IsNullOrWhiteSpace($commit)) { $commit = 'unknown' }

$changedFilesContent = Build-ChangedFilesMarkdown -ProjectRoot $ProjectRoot -GeneratedAt $generatedAt -Branch $branch -Commit $commit
Write-Utf8File -Path $changedFilesPath -Content $changedFilesContent -WithBom
Write-Utf8File -Path (Join-Path $sessionRoot '최근 수정 파일.md') -Content $changedFilesContent -WithBom

$checklistTokens = @{
    GENERATED_AT = $generatedAt
    BRANCH = $branch
    COMMIT = $commit
    TEST_ROOT = $scriptRoot
    RUNTIME_ROOT = $OutputRoot
    APP_DATA_ROOT = $isolatedAppRoot
    SERVER_DB_PATH = (Join-Path $serverOutput '거래플랜-local.db')
}
$checklistContent = Build-ChecklistContent -TemplatePath $templatePath -Tokens $checklistTokens
Write-Utf8File -Path $currentChecklistPath -Content $checklistContent -WithBom
Write-Utf8File -Path (Join-Path $sessionRoot '검증 체크리스트.md') -Content $checklistContent -WithBom

$prepareLog = @(
    "generated_at=$generatedAt",
    "configuration=$Configuration",
    "project_root=$ProjectRoot",
    "runtime_root=$OutputRoot",
    "branch=$branch",
    "commit=$commit",
    "api_base_url=$defaultBaseUrl",
    "source_api_base_url=$sourceApiBaseUrl",
    "dotnet=$dotnetExe",
    "source_app_root=$SourceAppRoot",
    "isolated_app_root=$isolatedAppRoot",
    "isolated_server_db=$(Join-Path $serverOutput '거래플랜-local.db')",
    "isolated_server_data_root=$serverDataRoot",
    "data_snapshot_source_exists=$($dataSnapshotResult.SourceExists)",
    "data_snapshot_database_source=$($dataSnapshotResult.DatabaseSource)",
    "data_snapshot_used_backup_fallback=$($dataSnapshotResult.UsedBackupFallback)",
    "server_seed_enabled=$([bool](-not $SkipServerSeed))",
    "server_seed_succeeded=$seedSucceeded",
    "server_seed_skip_reason=$seedSkippedReason"
) -join [Environment]::NewLine
Write-Utf8File -Path (Join-Path $sessionRoot '준비 로그.txt') -Content $prepareLog -WithBom

Write-Host '테스트 환경 준비 완료'
Write-Host "- 현재 로컬 데이터 스냅샷: $SourceAppRoot"
Write-Host "- 테스트 앱 데이터 루트: $isolatedAppRoot"
Write-Host "- 테스트 서버 DB: $(Join-Path $serverOutput '거래플랜-local.db')"
Write-Host "- 최근 수정 파일: $changedFilesPath"
Write-Host "- 검증 체크리스트: $currentChecklistPath"
Write-Host "- 실행 환경: $OutputRoot"
Write-Host "- 실행 명령: $(Join-Path $OutputRoot 'Run-All.cmd')"

if ($Launch) {
    Start-Process -FilePath (Join-Path $OutputRoot 'Run-All.cmd') -WorkingDirectory $OutputRoot
    Write-Host '로컬 테스트 서버/앱 실행을 시작했습니다.'
}
