# 거래플랜 NAS 운영 런북

- 기준일: 2026-03-19
- 대상 NAS: 워크플랜이 현재 사용하는 Synology NAS
- 목표: NAS에 거래플랜 API + PostgreSQL을 두고, Windows PC와 안드로이드 앱이 같은 서버를 사용하도록 준비

## 1. 현재 코드 기준 결론

현재 거래플랜은 중앙 서버 방식으로 운영할 수 있다.

- 데스크톱 앱은 `Api.BaseUrl`만 바꾸면 NAS API를 바라본다.
- 로그인/동기화는 REST API 기반이다.
- 서버는 PostgreSQL을 기본으로 사용할 수 있고, NAS 운영에서는 SQLite fallback을 꺼야 한다.

관련 소스:

- `D:\거래플랜\Desktop\거래플랜.Desktop.App\App.xaml.cs`
- `D:\거래플랜\Desktop\거래플랜.Desktop.App\Services\ErpApiClient.cs`
- `D:\거래플랜\Desktop\거래플랜.Desktop.App\Services\SyncService.cs`
- `D:\거래플랜\Server\거래플랜.Server.Api\Program.cs`
- `D:\거래플랜\Server\거래플랜.Server.Api\Controllers\SyncController.cs`

## 2. 권장 운영 방식

### 권장 1단계: Tailscale 사설망 운영

가장 안전하고 빠른 초기 방식이다.

- NAS, 외부 PC, 안드로이드 폰에 Tailscale 설치
- 거래플랜 API는 NAS에서 `127.0.0.1:18082`로만 열고
- Tailscale 또는 NAS 리버스 프록시를 통해 HTTPS 주소를 제공

장점:

- 공인 인터넷에 DB/API 포트를 직접 노출하지 않아도 됨
- 안드로이드에서도 Tailscale 앱으로 접속 가능
- 초기 보안 부담이 낮음

### 권장 2단계: NAS 리버스 프록시 + HTTPS

운영이 안정되면 공개 도메인을 붙인다.

- 예시 주소: `https://api.example.invalid`
- NAS Reverse Proxy 또는 별도 프록시가 `127.0.0.1:18082`로 전달
- PostgreSQL은 계속 `127.0.0.1` 바인딩 유지

## 3. 워크플랜과 분리된 NAS 구조

워크플랜과 충돌하지 않게 아래처럼 별도 루트를 사용한다.

```text
/volume1/docker/georaeplan/
  app/
    live/
  data/
    postgres/
  releases/
  backups/
```

Windows SMB 예시:

```text
\\192.0.2.10\docker\georaeplan
```

## 4. 포트 권장값

- 거래플랜 API: `127.0.0.1:18082 -> container 8080`
- 거래플랜 PostgreSQL: `127.0.0.1:15434 -> container 5432`

워크플랜이 이미 쓰는 포트와 분리한다.

## 5. 배포 파일

아래 파일을 추가해 두었다.

- `D:\거래플랜\infra\nas\docker-compose.yml`
- `D:\거래플랜\infra\nas\.env.example`
- `D:\거래플랜\infra\nas\.env.api.example.invalid.example`
- `D:\거래플랜\tools\nas\Sync-GeoraeplanNasConfig.ps1`
- `D:\거래플랜\tools\nas\Publish-GeoraeplanNasRelease.ps1`

운영 전에는 `.env.example`을 복사해 `.env`를 만들고 최소 다음 값을 바꾼다.

- `POSTGRES_PASSWORD`
- `JWT_SIGNING_KEY`
- `PUBLIC_BASE_URL`

`api.example.invalid` 기준 예시값이 필요하면 아래 파일을 바로 시작점으로 쓰면 된다.

- `D:\거래플랜\infra\nas\.env.api.example.invalid.example`

## 6. NAS ops 동기화 절차

로컬 PC에서:

```powershell
cd "D:\거래플랜"
powershell -ExecutionPolicy Bypass -File .\tools\nas\Sync-GeoraeplanNasConfig.ps1
```

실행 결과:

- `\\192.0.2.10\docker\georaeplan\ops\docker-compose.yml`
- `\\192.0.2.10\docker\georaeplan\ops\.env.example`

가 갱신된다.

그 뒤 NAS에서 아래 위치에 `.env`를 만든다.

```text
\\192.0.2.10\docker\georaeplan\ops\.env
```

## 7. 서버 publish 절차

로컬 PC에서:

```powershell
cd "D:\거래플랜"
powershell -ExecutionPolicy Bypass -File .\tools\nas\Publish-GeoraeplanNasRelease.ps1 -MirrorToLive
```

이 스크립트는:

- `dotnet build`
- 서버 `dotnet publish`
- NAS `ops` 구성 동기화
- `releases\<release-id>` 복사
- `-MirrorToLive` 사용 시 `app\live` 동기화

까지 한 번에 처리한다.

직접 확인해야 할 NAS 경로:

```text
\\192.0.2.10\docker\georaeplan\app\live
```

## 8. NAS에서 컨테이너 실행

NAS에서 `ops` 경로 기준으로 실행:

```bash
cd /volume1/docker/georaeplan/ops
docker compose --env-file .env up -d
```

확인:

```bash
docker compose ps
docker compose logs -f api
```

## 9. 상태 점검

2026-03-19에 서버 쪽에 아래 운영용 보강을 넣었다.

- `/healthz` 엔드포인트 추가
- reverse proxy/NAS 환경용 `ForwardedHeaders` 처리 추가
- JWT audience 기본값을 `georaeplan-client`로 일반화

체크 주소:

```text
http://127.0.0.1:18082/healthz
```

프록시를 붙였다면:

```text
https://api.example.invalid/healthz
```

## 10. PC 클라이언트 연결

각 PC에서 배포본 `appsettings.json`의 `Api.BaseUrl`을 NAS 주소로 바꾼다.

이미 있는 도구:

- `D:\거래플랜\배포\Set-ApiBaseUrl.ps1`

예:

```powershell
cd "D:\거래플랜\배포"
powershell -ExecutionPolicy Bypass -File .\Set-ApiBaseUrl.ps1 -BaseUrl https://api.example.invalid
```

이 스크립트는 기본적으로 아래 2개를 한 번에 바꾼다.

- `D:\거래플랜\배포\App\appsettings.json`
- `D:\거래플랜\배포\거래플랜\appsettings.json`

Tailscale만 쓸 때 예:

```powershell
powershell -ExecutionPolicy Bypass -File .\Set-ApiBaseUrl.ps1 -BaseUrl https://georaeplan.tailnet-name.ts.net
```

## 11. 안드로이드 앱 연결 전제

안드로이드 앱은 현재 아직 구현되지 않았지만, 서버 API는 재사용 가능하다.

우선 재사용 가능한 영역:

- `auth/login`
- `sync/pull`
- `sync/push`
- `customers`
- `items`
- `invoices`
- `payments`

즉, 서버는 NAS 1대에 두고 PC와 안드로이드 앱이 같은 API를 쓰는 구조가 가능하다.

## 12. 운영 체크리스트

- PostgreSQL 포트는 외부 전체 공개 금지
- API는 가능하면 Tailscale 또는 NAS HTTPS 프록시 뒤에만 노출
- 관리자 비밀번호와 JWT 키를 강한 값으로 변경
- `Database__EnableSqliteFallback=false` 유지
- NAS 백업 대상에 `data/postgres` 포함
- PC 배포본이 모두 같은 `Api.BaseUrl`을 사용하도록 통일

## 13. 추천 실행 순서

1. NAS에 `georaeplan` 운영 루트 생성
2. `infra/nas/.env` 작성
3. 서버 publish 후 `app/live` 복사
4. NAS에서 compose 실행
5. `healthz` 확인
6. 테스트용 PC 1대에서 `Api.BaseUrl` 변경 후 로그인/동기화 검증
7. 그 다음 외부 PC 확장
8. 마지막으로 안드로이드 MVP 연결
