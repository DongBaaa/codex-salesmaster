param(
    [string]$ProjectRoot = ".."
)

$ErrorActionPreference = "Stop"
$resolvedRoot = Resolve-Path $ProjectRoot
Set-Location $resolvedRoot

dotnet tool restore
dotnet tool run dotnet-ef database update `
    --project .\Server\SalesMaster.Server.Api\SalesMaster.Server.Api.csproj `
    --startup-project .\Server\SalesMaster.Server.Api\SalesMaster.Server.Api.csproj

Write-Host "데이터베이스 마이그레이션 완료." -ForegroundColor Green
