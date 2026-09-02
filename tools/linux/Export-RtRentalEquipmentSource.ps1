[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$SshHost = "100.64.215.109",
    [int]$SshPort = 22,
    [string]$SshUser = "rt_codex_ro",
    [string]$IdentityFile = "$env:USERPROFILE\.ssh\itwserver_codex_ed25519"
)

$ErrorActionPreference = "Stop"

$resolvedIdentity = [System.IO.Path]::GetFullPath($IdentityFile)
if (-not [System.IO.File]::Exists($resolvedIdentity)) {
    throw "RT 읽기 전용 SSH 개인키를 찾을 수 없습니다: $resolvedIdentity"
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw "출력 경로의 상위 폴더를 확인할 수 없습니다."
}
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$sql = @'
SELECT
  COALESCE(current_location, '') AS "Status",
  COALESCE(management_number, '') AS "ManagementNumber",
  COALESCE(category, '') AS "ItemCategoryName",
  COALESCE(model_name, '') AS "ItemName",
  COALESCE(manufacturer, '') AS "Manufacturer",
  COALESCE(serial_number, '') AS "MachineNumber",
  COALESCE(customer_name, '') AS "CustomerName",
  COALESCE(install_location, '') AS "InstallLocation",
  COALESCE(management_company, '') AS "ManagementCompany",
  COALESCE(rental_fee_text, '') AS "MonthlyFeeText",
  COALESCE(contract_months_text, '') AS "ContractMonthsText",
  COALESCE(contract_start::text, '') AS "ContractStartDate",
  COALESCE(rental_expiration::text, '') AS "RentalEndDate",
  COALESCE(disposal_date::text, '') AS "DisposalDate",
  COALESCE(k_limit, '') AS "BlackIncludedText",
  COALESCE(c_limit, '') AS "ColorIncludedText",
  COALESCE(k_extra, '') AS "BlackOverageText",
  COALESCE(c_extra, '') AS "ColorOverageText"
FROM public.equipment
WHERE deleted_at IS NULL
ORDER BY management_number, management_id;
'@

$sshArguments = @(
    "-T",
    "-p", $SshPort.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    "-o", "BatchMode=yes",
    "-o", "IdentitiesOnly=yes",
    "-o", "ExitOnForwardFailure=yes",
    "-o", "ConnectTimeout=10",
    "-i", $resolvedIdentity,
    "$SshUser@$SshHost",
    "query"
)

$csvLines = $sql | & ssh.exe @sshArguments 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "RT 읽기 전용 조회에 실패했습니다: $($csvLines -join [Environment]::NewLine)"
}

$csvText = ($csvLines -join [Environment]::NewLine) + [Environment]::NewLine
$utf8WithBom = [System.Text.UTF8Encoding]::new($true)
[System.IO.File]::WriteAllText($resolvedOutput, $csvText, $utf8WithBom)

$dataRowCount = [Math]::Max(0, $csvLines.Count - 1)
$sha256 = (Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash
Write-Output "rt_rental_source_path=$resolvedOutput"
Write-Output "rt_rental_source_rows=$dataRowCount"
Write-Output "rt_rental_source_sha256=$sha256"
