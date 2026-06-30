param(
    [string]$ProjectRoot = "D:\거래플랜",
    [string]$AppRelativePath = "InvestorResearchWeb",
    [string]$LinuxHost = "192.168.0.199",
    [int]$LinuxPort = 2222,
    [string]$LinuxUser = "itw",
    [string]$SshKeyPath = "C:\Users\beene\.ssh\itwserver_codex_ed25519",
    [string]$RemoteRoot = "/home/itw/investor-research",
    [string]$HostBind = "192.168.0.199",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$scriptPath = $PSCommandPath

function Write-Step([string]$Message) {
    Write-Host "[investor-research] $Message" -ForegroundColor Cyan
}

$appRoot = Join-Path $ProjectRoot $AppRelativePath
if (-not (Test-Path $appRoot)) {
    throw "앱 경로를 찾을 수 없습니다: $appRoot"
}

$packageJson = Join-Path $appRoot "package.json"
if (-not (Test-Path $packageJson)) {
    throw "package.json을 찾을 수 없습니다: $packageJson"
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$tempRoot = Join-Path $ProjectRoot "release-temp\investor-research-$timestamp"
$stageRoot = Join-Path $tempRoot "source"
$archivePath = Join-Path $tempRoot "investor-research-$timestamp.tar.gz"

Write-Step "로컬 빌드 검증 시작"
Push-Location $appRoot
try {
    npm ci
    npm run build
}
finally {
    Pop-Location
}

Write-Step "릴리스 패키지 스테이징: $stageRoot"
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

$robocopyArgs = @(
    $appRoot,
    $stageRoot,
    "/E",
    "/XD", "node_modules", "dist", "artifacts", ".git",
    "/XF", ".env", "*.tsbuildinfo", "vite.config.js", "vite.config.d.ts",
    "/NFL", "/NDL", "/NJH", "/NJS", "/NP"
)
& robocopy @robocopyArgs | Out-Host
if ($LASTEXITCODE -gt 7) {
    throw "robocopy 실패: exit code $LASTEXITCODE"
}

Write-Step "스테이징 텍스트 파일 UTF-8 BOM 제거"
$bomTargets = Get-ChildItem -Path $stageRoot -Recurse -File | Where-Object {
    $_.Extension -in @(".conf", ".yml", ".yaml", ".json", ".md", ".ts", ".tsx", ".js", ".mjs", ".css", ".html", ".example", ".ignore", ".zone") -or
    $_.Name -in @("Dockerfile", ".dockerignore", ".env.example")
}
foreach ($file in $bomTargets) {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        [System.IO.File]::WriteAllBytes($file.FullName, $bytes[3..($bytes.Length - 1)])
    }
}

Write-Step "tar.gz 패키지 생성: $archivePath"
Push-Location $stageRoot
try {
    & tar -czf $archivePath .
    if ($LASTEXITCODE -ne 0) {
        throw "tar 패키지 생성 실패: exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Step "패키지 생성 완료: $archivePath"

if (-not $Deploy) {
    Write-Host ""
    Write-Host "배포 파일 생성까지만 완료했습니다. Linux PC 반영은 아래처럼 -Deploy를 붙여 실행하세요." -ForegroundColor Yellow
    Write-Host "powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Deploy"
    Write-Host ""
    Write-Host "주의: -Deploy는 $RemoteRoot 아래 investor-research 컨테이너만 갱신합니다. 기존 거래플랜/워크플랜 컨테이너는 재시작하지 않습니다."
    exit 0
}

if (-not (Test-Path $SshKeyPath)) {
    throw "SSH 키를 찾을 수 없습니다: $SshKeyPath"
}

$sshTarget = "$LinuxUser@$LinuxHost"
$remoteReleaseDir = "$RemoteRoot/releases/$timestamp"
$remoteArchivePath = "$remoteReleaseDir/investor-research-$timestamp.tar.gz"
$imageTag = "investor-research-web:$timestamp"

Write-Step "Linux PC 배포 디렉터리 생성: $remoteReleaseDir"
& ssh -i $SshKeyPath -p $LinuxPort $sshTarget "mkdir -p '$remoteReleaseDir' '$RemoteRoot/source'"
if ($LASTEXITCODE -ne 0) { throw "원격 디렉터리 생성 실패" }

Write-Step "패키지 업로드"
& scp -i $SshKeyPath -P $LinuxPort $archivePath "$sshTarget`:$remoteArchivePath"
if ($LASTEXITCODE -ne 0) { throw "패키지 업로드 실패" }

$remoteScriptTemplate = @'
set -euo pipefail
REMOTE_ROOT='__REMOTE_ROOT__'
RELEASE_DIR='__REMOTE_RELEASE_DIR__'
ARCHIVE='__REMOTE_ARCHIVE_PATH__'
IMAGE_TAG='__IMAGE_TAG__'

rm -rf "$REMOTE_ROOT/source.new"
mkdir -p "$REMOTE_ROOT/source.new"
tar -xzf "$ARCHIVE" -C "$REMOTE_ROOT/source.new"

if [ ! -f "$REMOTE_ROOT/source.new/Dockerfile" ]; then
  echo 'Dockerfile missing after extraction' >&2
  exit 1
fi

cp "$REMOTE_ROOT/source.new/deploy/linux/docker-compose.yml" "$REMOTE_ROOT/docker-compose.yml"
if [ ! -f "$REMOTE_ROOT/.env" ]; then
  cp "$REMOTE_ROOT/source.new/deploy/linux/.env.example" "$REMOTE_ROOT/.env"
fi

REMOTE_ROOT_FOR_PY="$REMOTE_ROOT" python3 - <<'PY'
from pathlib import Path
import os
root = Path(os.environ['REMOTE_ROOT_FOR_PY'])
env = root / '.env'
example = root / 'source.new' / 'deploy' / 'linux' / '.env.example'
if env.exists() and example.exists():
    current = env.read_text(encoding='utf-8-sig').splitlines()
    seen = {
        line.split('=', 1)[0].strip()
        for line in current
        if line.strip() and not line.lstrip().startswith('#') and '=' in line
    }
    additions = []
    for line in example.read_text(encoding='utf-8-sig').splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith('#') or '=' not in stripped:
            continue
        key = stripped.split('=', 1)[0].strip()
        if key not in seen:
            additions.append(line)
            seen.add(key)
    if additions:
        current.extend(['', '# Added by InvestorResearch deployment for new runtime settings'])
        current.extend(additions)
        env.write_text('\n'.join(current) + '\n', encoding='utf-8')
PY

REMOTE_ROOT_FOR_PY="$REMOTE_ROOT" python3 - <<'PY'
from pathlib import Path
import os
for path in [Path(os.environ['REMOTE_ROOT_FOR_PY']) / '.env']:
    if path.exists():
        data = path.read_bytes()
        if data.startswith(b'\xef\xbb\xbf'):
            path.write_bytes(data[3:])
PY

REMOTE_ROOT_FOR_PY="$REMOTE_ROOT" IMAGE_TAG_FOR_PY="$IMAGE_TAG" HOST_BIND_FOR_PY="__HOST_BIND__" python3 - <<'PY'
from pathlib import Path
import os
import secrets
root = Path(os.environ['REMOTE_ROOT_FOR_PY'])
image_tag = os.environ['IMAGE_TAG_FOR_PY']
host_bind = os.environ['HOST_BIND_FOR_PY']
env = root / '.env'
text = env.read_text() if env.exists() else ''
lines = text.splitlines()
values = {
    'INVESTOR_RESEARCH_IMAGE': image_tag,
    'INVESTOR_RESEARCH_HOST_BIND': host_bind,
}
has_secret = False
seen = set()
for i, line in enumerate(lines):
    if line.startswith('SESSION_SECRET='):
        has_secret = True
        if not line.split('=', 1)[1].strip() or 'replace-with' in line:
            lines[i] = f'SESSION_SECRET={secrets.token_urlsafe(48)}'
    for key, value in values.items():
        if line.startswith(f'{key}='):
            lines[i] = f'{key}={value}'
            seen.add(key)
if not has_secret:
    lines.append(f'SESSION_SECRET={secrets.token_urlsafe(48)}')
for key, value in values.items():
    if key not in seen:
        lines.append(f'{key}={value}')
env.write_text('\n'.join(lines) + '\n')
PY

rm -rf "$REMOTE_ROOT/source.prev"
if [ -d "$REMOTE_ROOT/source" ]; then mv "$REMOTE_ROOT/source" "$REMOTE_ROOT/source.prev"; fi
mv "$REMOTE_ROOT/source.new" "$REMOTE_ROOT/source"

docker build -t "$IMAGE_TAG" "$REMOTE_ROOT/source"
docker compose --env-file "$REMOTE_ROOT/.env" -f "$REMOTE_ROOT/docker-compose.yml" up -d --no-deps investor-research-web
sleep 3
HEALTH_HOST="__HOST_BIND__"
HEALTH_PORT="18088"
if grep -q '^INVESTOR_RESEARCH_HOST_PORT=' "$REMOTE_ROOT/.env"; then
  HEALTH_PORT="$(grep '^INVESTOR_RESEARCH_HOST_PORT=' "$REMOTE_ROOT/.env" | tail -n 1 | cut -d= -f2- | tr -cd '0-9')"
fi
HEALTH_PORT="${HEALTH_PORT:-18088}"
if [ "$HEALTH_HOST" = "0.0.0.0" ]; then HEALTH_HOST="127.0.0.1"; fi
curl -fsS "http://${HEALTH_HOST}:${HEALTH_PORT}/healthz"
echo
docker ps --filter name=investor-research-web --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
'@

$remoteScript = $remoteScriptTemplate.Replace("__REMOTE_ROOT__", $RemoteRoot).Replace("__REMOTE_RELEASE_DIR__", $remoteReleaseDir).Replace("__REMOTE_ARCHIVE_PATH__", $remoteArchivePath).Replace("__IMAGE_TAG__", $imageTag).Replace("__HOST_BIND__", $HostBind)

Write-Step "Linux PC에서 이미지 빌드 및 investor-research-web 컨테이너만 갱신"
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
& ssh -i $SshKeyPath -p $LinuxPort $sshTarget "echo $encoded | base64 -d | bash"
if ($LASTEXITCODE -ne 0) { throw "원격 배포 실패" }

Write-Step "배포 완료. 헬스체크: http://${HostBind}:18088/healthz"
Write-Host "공개 도메인 연결은 DNS/인증서/Nginx 프록시 확인 후 별도 적용하세요: research.2884.kr" -ForegroundColor Yellow
