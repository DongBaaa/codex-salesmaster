[CmdletBinding()]
param(
    [string]$PostgreSqlBin = 'C:\Program Files\PostgreSQL\17\bin',
    [string]$ClusterBase = 'D:\DevCaches\georaeplan-postgres-tests',
    [string]$ResultsDirectory = '',
    [string]$TestFilter = 'FullyQualifiedName~PostgreSql',
    [string]$LogFileName = 'ephemeral-postgresql-tests.trx',
    [switch]$KeepCluster
)

$ErrorActionPreference = 'Stop'

function Assert-ManagedChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$ChildPath
    )

    $resolvedBase = [IO.Path]::GetFullPath($BasePath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $resolvedChild = [IO.Path]::GetFullPath($ChildPath)
    $baseRoot = [IO.Path]::GetPathRoot($resolvedBase)
    if ([string]::Equals(
            $resolvedBase,
            $baseRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The temporary PostgreSQL base cannot be a drive root.'
    }

    $expectedPrefix = $resolvedBase + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedChild.StartsWith(
            $expectedPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The temporary PostgreSQL cluster escaped its managed base.'
    }
}

$requiredExecutables = @('initdb.exe', 'pg_ctl.exe')
foreach ($name in $requiredExecutables) {
    $path = Join-Path $PostgreSqlBin $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "PostgreSQL executable was not found: $path"
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testProject = Join-Path $repositoryRoot (
    'Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj')
if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "Server test project was not found: $testProject"
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path (
        'D:\DevCaches\georaeplan-v1-test-runs') (
        'ephemeral-postgresql-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

$clusterBaseFullPath = [IO.Path]::GetFullPath($ClusterBase)
New-Item -ItemType Directory -Force -Path $clusterBaseFullPath | Out-Null
$clusterRoot = Join-Path $clusterBaseFullPath (
    'cluster-' + [Guid]::NewGuid().ToString('N'))
Assert-ManagedChildPath -BasePath $clusterBaseFullPath -ChildPath $clusterRoot
New-Item -ItemType Directory -Force -Path $clusterRoot | Out-Null
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

$dataDirectory = Join-Path $clusterRoot 'data'
$postgresLog = Join-Path $clusterRoot 'postgres.log'
$initDb = Join-Path $PostgreSqlBin 'initdb.exe'
$pgCtl = Join-Path $PostgreSqlBin 'pg_ctl.exe'
$started = $false
$stopped = $false
$testsPassed = $false

$listener = [Net.Sockets.TcpListener]::new(
    [Net.IPAddress]::Loopback,
    0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

try {
    & $initDb `
        -D $dataDirectory `
        -U postgres `
        '--auth-host=trust' `
        '--auth-local=trust' `
        '--encoding=UTF8' `
        '--no-locale'
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed with exit code $LASTEXITCODE."
    }

    & $pgCtl `
        -D $dataDirectory `
        -l $postgresLog `
        -o "-p $port -h 127.0.0.1" `
        -w `
        start
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl start failed with exit code $LASTEXITCODE."
    }
    $started = $true

    $env:GEORAEPLAN_POSTGRES_TEST_CONNECTION =
        "Host=127.0.0.1;Port=$port;Database=postgres;" +
        'Username=postgres;Pooling=false;Include Error Detail=false'

    Write-Host "ephemeral_postgresql=ready port=$port"
    dotnet test $testProject `
        --no-restore `
        --filter $TestFilter `
        --logger "trx;LogFileName=$LogFileName" `
        --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "PostgreSQL tests failed with exit code $LASTEXITCODE."
    }

    $testsPassed = $true
    Write-Host 'ephemeral_postgresql_tests=ok'
}
finally {
    Remove-Item Env:\GEORAEPLAN_POSTGRES_TEST_CONNECTION `
        -ErrorAction SilentlyContinue

    if ($started) {
        & $pgCtl -D $dataDirectory -m fast -w stop
        $stopped = $LASTEXITCODE -eq 0
        if (-not $stopped) {
            Write-Warning (
                "Temporary PostgreSQL did not stop cleanly. " +
                "The cluster is preserved at $clusterRoot")
        }
    }

    if ($testsPassed -and
        ($stopped -or -not $started) -and
        -not $KeepCluster) {
        Assert-ManagedChildPath `
            -BasePath $clusterBaseFullPath `
            -ChildPath $clusterRoot
        Remove-Item -LiteralPath $clusterRoot -Recurse -Force
        Write-Host 'ephemeral_postgresql_cleanup=ok'
    }
    else {
        Write-Host "ephemeral_postgresql_cluster_preserved=$clusterRoot"
    }
}
