[CmdletBinding()]
param(
    [string]$PostgreSqlBin = 'C:\Program Files\PostgreSQL\17\bin',
    [string]$ClusterBase = 'D:\DevCaches\georaeplan-postgres-tests',
    [string]$ResultsDirectory = '',
    [string]$TestFilter = 'FullyQualifiedName~PostgreSql',
    [string]$LogFileName = 'ephemeral-postgresql-tests.trx',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$RunAllServerTests,
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

function Invoke-ManagedPgCtl {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][ValidateSet('start', 'stop')][string]$Stage
    )

    # Native invocation through a redirected PowerShell pipeline can wait for
    # PostgreSQL's long-lived descendants after pg_ctl itself has already exited.
    # Wait for this process only; keep its output out of the inherited pipeline.
    $stdoutPath = Join-Path $clusterRoot "pg_ctl-$Stage.stdout.log"
    $stderrPath = Join-Path $clusterRoot "pg_ctl-$Stage.stderr.log"
    $process = Start-Process -FilePath $pgCtl -ArgumentList $Arguments `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    try {
        $process.WaitForExit()
        $process.Refresh()
        $exitCode = $process.ExitCode
        foreach ($outputPath in @($stdoutPath, $stderrPath)) {
            if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
                Get-Content -LiteralPath $outputPath | ForEach-Object { Write-Host $_ }
            }
        }
        return $exitCode
    }
    finally {
        $process.Dispose()
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

if ($RunAllServerTests -and $PSBoundParameters.ContainsKey('TestFilter')) {
    throw 'RunAllServerTests and TestFilter cannot be used together.'
}
if (-not $RunAllServerTests -and [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'TestFilter cannot be empty unless RunAllServerTests is used.'
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

    $pgCtlExitCode = Invoke-ManagedPgCtl -Stage start -Arguments @(
        '-D', ('"{0}"' -f $dataDirectory),
        '-l', ('"{0}"' -f $postgresLog),
        '-o', ('"-p {0} -h 127.0.0.1"' -f $port), '-w', 'start'
    )
    if ($pgCtlExitCode -ne 0) {
        throw "pg_ctl start failed with exit code $pgCtlExitCode."
    }
    $started = $true

    $env:GEORAEPLAN_POSTGRES_TEST_CONNECTION =
        "Host=127.0.0.1;Port=$port;Database=postgres;" +
        'Username=postgres;Pooling=false;Include Error Detail=false'

    $testArguments = @(
        'test',
        $testProject,
        '--configuration',
        $Configuration,
        '--no-restore'
    )
    if (-not $RunAllServerTests) {
        $testArguments += @('--filter', $TestFilter)
    }
    $testArguments += @(
        '--logger',
        "trx;LogFileName=$LogFileName",
        '--results-directory',
        $ResultsDirectory
    )

    $testScope = if ($RunAllServerTests) { 'all-server-tests' } else { $TestFilter }
    Write-Host "ephemeral_postgresql=ready port=$port configuration=$Configuration scope=$testScope"
    & dotnet @testArguments
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
        $pgCtlExitCode = Invoke-ManagedPgCtl -Stage stop -Arguments @(
            '-D', ('"{0}"' -f $dataDirectory), '-m', 'fast', '-w', 'stop'
        )
        $stopped = $pgCtlExitCode -eq 0
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
