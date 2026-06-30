# 투자 리서치 플랜 웹앱

한국 주식 성장주 후보를 정량 스크리닝하고, OAuth 로그인으로 보호된 서버 API를 통해 OpenAI Responses API 기반 실시간 리서치 초안을 생성하는 웹앱입니다.

> 이 앱은 투자 권유가 아니라 리서치 보조 도구입니다. 모든 투자 판단과 주문은 사용자가 별도 검증 후 결정해야 합니다.

## 현재 기능

- 데모 후보 데이터 기반 정량 스크리닝
- PEG, 매출 CAGR, ROE, 최근 3년 매출성장, 영업이익률 개선, 시총, 적자, 감사의견 필터
- 듀퐁 ROE 분석 표시
- PER 40% + PBR 30% + DCF 30% 가중 적정가, 안전마진 매수가, 목표가, 손절가 계산
- 경제적 해자 강/중/약 평가 UI
- Markdown 리포트 저장
- OAuth/OIDC 사용자 로그인 구조
- Claude, GPT, Gemini, Perplexity AI 제공자 OAuth 등록 버튼
- 서버 측 OpenAI Responses API 호출
- OpenAI `web_search_preview` + Structured Outputs 기반 AI 리서치 결과 표시

## 중요한 인증 구조

OpenAI 일반 API 호출은 브라우저 OAuth 토큰으로 직접 호출하지 않습니다.

- 사용자 접근 제어: OAuth/OIDC 로그인
- OpenAI API 자격 증명: 서버 환경변수 `OPENAI_API_KEY` 또는 `OPENAI_BEARER_TOKEN`
- 브라우저에는 OpenAI 키를 절대 노출하지 않습니다.

공식 참고:

- OpenAI API Authentication: <https://developers.openai.com/api/reference/overview#authentication>
- Responses API: <https://developers.openai.com/api/reference/responses/create>
- Apps SDK OAuth 참고: <https://developers.openai.com/apps-sdk/build/auth>

## 로컬 실행

프론트엔드만 확인:

```powershell
cd "D:\거래플랜\InvestorResearchWeb"
npm install
npm run dev
```

브라우저에서 `http://127.0.0.1:5177` 접속.

Node API 서버까지 확인:

```powershell
cd "D:\거래플랜\InvestorResearchWeb"
npm install
npm run build
npm start
```

기본 접속 주소:

```text
http://127.0.0.1:8080
http://127.0.0.1:8080/healthz
```

## 환경변수

`.env.example`을 참고하세요.

필수 운영값:

```env
APP_BASE_URL=http://192.168.0.199:18088
SESSION_SECRET=replace-with-32-byte-random-secret
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

AI 제공자별 OAuth 등록 버튼을 활성화하려면 아래 값도 설정합니다.

```env
AI_GPT_OAUTH_ENABLED=true
AI_GPT_CLIENT_ID=...
AI_GPT_CLIENT_SECRET=...
AI_GPT_AUTHORIZATION_URL=...
AI_GPT_TOKEN_URL=...
AI_GPT_REDIRECT_URI=https://research.2884.kr/api/ai-providers/gpt/oauth/callback

AI_CLAUDE_OAUTH_ENABLED=true
AI_CLAUDE_CLIENT_ID=...
AI_CLAUDE_CLIENT_SECRET=...
AI_CLAUDE_AUTHORIZATION_URL=...
AI_CLAUDE_TOKEN_URL=...
AI_CLAUDE_REDIRECT_URI=https://research.2884.kr/api/ai-providers/claude/oauth/callback

AI_GEMINI_OAUTH_ENABLED=true
AI_GEMINI_CLIENT_ID=...
AI_GEMINI_CLIENT_SECRET=...
AI_GEMINI_AUTHORIZATION_URL=https://accounts.google.com/o/oauth2/v2/auth
AI_GEMINI_TOKEN_URL=https://oauth2.googleapis.com/token
AI_GEMINI_USERINFO_URL=https://openidconnect.googleapis.com/v1/userinfo
AI_GEMINI_REDIRECT_URI=https://research.2884.kr/api/ai-providers/gemini/oauth/callback

AI_PERPLEXITY_OAUTH_ENABLED=true
AI_PERPLEXITY_CLIENT_ID=...
AI_PERPLEXITY_CLIENT_SECRET=...
AI_PERPLEXITY_AUTHORIZATION_URL=...
AI_PERPLEXITY_TOKEN_URL=...
AI_PERPLEXITY_REDIRECT_URI=https://research.2884.kr/api/ai-providers/perplexity/oauth/callback
```

참고: Claude, OpenAI Platform, Perplexity의 일반 모델 API는 API key 기반 구성이 일반적입니다. 이 앱의 OAuth 등록 버튼은 조직별 OAuth/OIDC gateway 또는 제공자 OAuth endpoint가 환경변수로 설정된 경우 활성화됩니다.

공개 도메인 전환 후에는 아래처럼 변경합니다.

```env
APP_BASE_URL=https://research.2884.kr
COOKIE_SECURE=true
OAUTH_REDIRECT_URI=https://research.2884.kr/api/auth/callback
```

## API

### `GET /healthz`

컨테이너 헬스체크입니다.

### `GET /api/auth/status`

OAuth 설정, 로그인 여부, OpenAI 설정 여부를 반환합니다.

### `GET /api/auth/login`

OAuth 로그인으로 리다이렉트합니다.

### `GET /api/auth/callback`

OAuth authorization code를 처리하고 HttpOnly 세션 쿠키를 발급합니다.

### `POST /api/auth/logout`

세션 쿠키를 삭제합니다.

### `GET /api/ai-providers`

Claude, GPT, Gemini, Perplexity 등록 버튼 상태를 반환합니다.

### `GET /api/ai-providers/:provider/oauth/start`

제공자별 OAuth 등록을 시작합니다.

### `GET /api/ai-providers/:provider/oauth/callback`

제공자별 OAuth 등록 callback을 처리합니다.

### `POST /api/ai-providers/:provider/disconnect`

제공자별 등록 상태 쿠키를 삭제합니다.

### `POST /api/research/analyze`

현재 필터/후보 데이터를 OpenAI Responses API에 전달해 AI 리서치 결과를 생성합니다.

요청:

```json
{
  "settings": {
    "investmentAmount": 10000000,
    "periodMonths": 6,
    "targetReturn": 20,
    "riskPolicy": "고위험 제외"
  },
  "filters": {
    "maxPeg": 1.5,
    "minRevenueCagr": 15,
    "minRoe": 15,
    "minYearlyRevenueGrowth": 20,
    "minMarketCap": 100000000000
  },
  "candidates": []
}
```

응답:

```json
{
  "ok": true,
  "model": "gpt-5.4-mini",
  "report": {},
  "citations": [],
  "sourceMode": "openai-web-search"
}
```

## 검증 명령

```powershell
cd "D:\거래플랜\InvestorResearchWeb"
npm run build
npm run smoke:interaction
npm run smoke:server
```

## 기존 거래플랜 ERP 영향

- 기존 `Server`, `Desktop`, `Mobile`, `Shared` 코드는 수정하지 않습니다.
- 거래플랜 운영 DB, 동기화, 전표, 품목, 자산, 거래처 데이터와 연결하지 않습니다.
- Linux PC에서는 전용 컨테이너 `investor-research-web`만 갱신합니다.
