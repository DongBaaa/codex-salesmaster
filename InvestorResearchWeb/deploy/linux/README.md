# InvestorResearchWeb Linux PC 배포 런북

## 운영 구조

- 서비스명: `investor-research-web`
- 실제 배포 경로: `/home/itw/investor-research`
- 기본 LAN URL: `http://192.168.0.199:18088`
- 헬스체크: `http://192.168.0.199:18088/healthz`
- 공개 예정 도메인: `https://research.2884.kr`
- 기존 거래플랜/워크플랜/itw 서비스와 분리된 단독 Docker compose 서비스

## 파일 구성

| 파일 | 용도 |
|---|---|
| `Dockerfile` | Vite 빌드 후 Node API 서버로 정적 파일과 `/api/*` 제공 |
| `deploy/linux/docker-compose.yml` | Linux PC 전용 compose 정의 |
| `deploy/linux/.env.example` | 운영 `.env` 예시 |
| `deploy/linux/nginx-research.2884.kr.example.conf` | 공개 reverse proxy 예시 |
| `deploy/linux/dns-research.2884.kr.zone.example` | DNS zone 예시 |
| `D:\거래플랜\tools\linux\Publish-InvestorResearchLinuxPcRelease.ps1` | 패키징/배포 스크립트 |

## 로컬 패키지 검증

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\linux\Publish-InvestorResearchLinuxPcRelease.ps1"
```

실행 내용:

1. `npm ci`
2. `npm run build`
3. `release-temp\investor-research-<timestamp>` 아래 tar.gz 생성

## Linux PC 반영

현재 `/srv`는 비대화식 sudo 권한이 없어 `/home/itw/investor-research`를 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\linux\Publish-InvestorResearchLinuxPcRelease.ps1" `
  -Deploy `
  -RemoteRoot "/home/itw/investor-research" `
  -HostBind "192.168.0.199"
```

배포 스크립트는 아래 원칙을 지킵니다.

- Docker daemon 전체 재시작 금지
- `docker compose down` 금지
- `docker system prune` 금지
- 기존 거래플랜/워크플랜/itw 컨테이너 재시작 금지
- `investor-research-web` 서비스만 `up -d --no-deps`로 갱신

## OAuth/OpenAI 운영 설정

원격 `.env` 파일:

```bash
/home/itw/investor-research/.env
```

LAN 테스트용 예시:

```env
APP_BASE_URL=http://192.168.0.199:18088
COOKIE_SECURE=false
AUTH_REQUIRED=true
OAUTH_ENABLED=true
OAUTH_PROVIDER=google
OAUTH_CLIENT_ID=...
OAUTH_CLIENT_SECRET=...
OAUTH_REDIRECT_URI=http://192.168.0.199:18088/api/auth/callback
OPENAI_API_KEY=...
OPENAI_MODEL=gpt-5.4-mini
OPENAI_ENABLE_WEB_SEARCH=true
```

AI 제공자별 OAuth 등록 버튼 callback:

```text
https://research.2884.kr/api/ai-providers/gpt/oauth/callback
https://research.2884.kr/api/ai-providers/claude/oauth/callback
https://research.2884.kr/api/ai-providers/gemini/oauth/callback
https://research.2884.kr/api/ai-providers/perplexity/oauth/callback
```

환경변수 prefix:

```text
AI_GPT_*
AI_CLAUDE_*
AI_GEMINI_*
AI_PERPLEXITY_*
```

공개 도메인 전환 후 예시:

```env
APP_BASE_URL=https://research.2884.kr
COOKIE_SECURE=true
OAUTH_REDIRECT_URI=https://research.2884.kr/api/auth/callback
```

## 공개 도메인 연결

현재 확인된 차단 사항:

- `research.2884.kr` DNS 레코드 없음
- 현재 SSH 계정은 공용 reverse proxy 설정 권한 없음
- `itw.2884.kr:18088`, `work.2884.kr:18088`, `trade.2884.kr:18088` 직접 접근 불가

관리자 작업:

1. DNS에 `research.2884.kr` 레코드 생성
   - 예시: `deploy/linux/dns-research.2884.kr.zone.example`
   - 현재 `2884.kr` A 레코드는 `112.155.36.24`
   - 권장 public DNS: `research.2884.kr A 112.155.36.24`
2. 공용 reverse proxy에서 `research.2884.kr` → `http://192.168.0.199:18088`
3. SSL 인증서 발급/갱신 설정
4. OAuth 제공자 콘솔에 `https://research.2884.kr/api/auth/callback` 등록
5. AI 제공자별 OAuth callback 4개 등록
6. 적용 후 기존 서비스 헬스체크 확인
   - `https://trade.2884.kr/healthz`
   - `https://work.2884.kr/healthz`
   - `https://itw.2884.kr/`

## 운영 확인 명령

```bash
cd /home/itw/investor-research
docker compose --env-file .env -f docker-compose.yml ps
curl -fsS http://192.168.0.199:18088/healthz
curl -fsS http://192.168.0.199:18088/api/auth/status
```
