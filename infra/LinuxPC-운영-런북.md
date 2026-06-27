# 거래플랜 Linux PC 운영 런북

## 1. 현재 운영 기준

- 서버 위치: Linux PC `itw@192.168.0.199:2222`
- 운영 루트: `/srv/georaeplan`
- ops 경로: `/srv/georaeplan/ops`
- live 앱 경로: `/srv/georaeplan/app/live`
- release 보관 경로: `/srv/georaeplan/releases`
- 공개 주소: `https://trade.2884.kr`
- 필요한 reverse proxy/네트워크 중계도 Linux PC 운영 기준으로 확인합니다.

## 2. 배포 원칙

- 거래플랜, 워크플랜, itw 홈페이지 작업은 한 번에 하나의 서비스만 진행합니다.
- Linux PC 전체 Docker 정리/전체 재시작은 금지합니다.
- 허용 방향은 거래플랜 compose project 안에서 필요한 서비스만 명시적으로 반영하는 것입니다.
- live 반영 전후에는 아래 3개 공개 URL을 확인합니다.
  - `https://trade.2884.kr/healthz`
  - `https://work.2884.kr/healthz`
  - `https://itw.2884.kr/`

## 3. 권장 배포 명령

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\linux\Publish-GeoraeplanLinuxPcRelease.ps1" `
  -ProjectRoot "D:\거래플랜" `
  -MirrorToLive `
  -FailOnOperationalWarnings
```

전체 릴리스(PC 설치파일, Android APK, 업데이트 자산 생성 포함)는 아래 명령을 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\release\Publish-GeoraePlanFullRelease.ps1" `
  -ProjectRoot "D:\거래플랜" `
  -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" `
  -DeployToLinuxPc `
  -FailOnOperationalWarnings
```

> 유료 납품/엄격 release에서는 live 전/후 operational gate를 생략하지 않고 `-FailOnOperationalWarnings`로 warning도 차단합니다. gate 생략 옵션은 별도 엄격 gate가 이미 통과한 긴급 상황에서만 사용합니다.
> Android APK가 포함된 live 반영은 새 APK와 현재 live APK의 signing certificate SHA-256을 비교합니다. 불일치하면 기존 설치본 업데이트가 실패하므로 기본 차단하며, 재설치/전환 공지가 검증된 경우에만 `-AcceptAndroidSigningCertificateChange`를 사용합니다.

## 4. SSH 설정

- Windows 배포 PC SSH 키: `C:\Users\beene\.ssh\itwserver_codex_ed25519`
- Linux PC 등록 계정: `itw`
- SSH 포트: `2222`
- 운영 스크립트 경로: `/srv/georaeplan/ops`

확인 명령:

```powershell
ssh -i "$env:USERPROFILE\.ssh\itwserver_codex_ed25519" -p 2222 itw@192.168.0.199 "test -f /srv/georaeplan/ops/apply-release.sh && bash -n /srv/georaeplan/ops/apply-release.sh && docker ps --format '{{.Names}} {{.Status}}'"
```

## 5. Linux PC용 설정 템플릿

- 예시 env: `D:\거래플랜\infra\linux\.env.example`
- 예시 compose: `D:\거래플랜\infra\linux\docker-compose.yml`

현재 운영 중인 `/srv/georaeplan/ops/.env`와 compose 파일은 실제 운영값을 포함할 수 있으므로, 저장소의 예시 파일로 무조건 덮어쓰지 않습니다.

## 6. 구 운영 문서 처리

- 구 운영 문서
- `D:\거래플랜\infra\nas\*`
- 구 운영 스크립트

위 파일들은 legacy 또는 기존 호환용입니다. 새 작업에서는 Linux PC 런북과 `tools\linux` 스크립트를 우선 사용합니다.
