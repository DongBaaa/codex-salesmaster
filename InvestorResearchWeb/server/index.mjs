import crypto from "node:crypto";
import { existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import express from "express";
import OpenAI from "openai";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, "..");
const distRoot = path.join(projectRoot, "dist");

const env = process.env;
const port = Number(env.PORT || 8080);
const appBaseUrl = stripTrailingSlash(env.APP_BASE_URL || `http://127.0.0.1:${port}`);
const sessionSecret = env.SESSION_SECRET || crypto.randomBytes(32).toString("base64url");
const secureCookies = boolEnv("COOKIE_SECURE", appBaseUrl.startsWith("https://"));
const authRequired = boolEnv("AUTH_REQUIRED", true);
const maxBodyBytes = env.API_BODY_LIMIT || "2mb";
const oauthConfig = buildOAuthConfig();
const openaiCredential = firstNonEmpty(env.OPENAI_API_KEY, env.OPENAI_BEARER_TOKEN, env.OPENAI_ACCESS_TOKEN);
const openaiModel = firstNonEmpty(env.OPENAI_MODEL, "gpt-5.4-mini");
const enableWebSearch = boolEnv("OPENAI_ENABLE_WEB_SEARCH", true);
const maxOutputTokens = Number(env.OPENAI_MAX_OUTPUT_TOKENS || 6500);
const allowedEmails = parseCsv(env.AUTH_ALLOWED_EMAILS);

if (!env.SESSION_SECRET) {
  console.warn("[investor-research] SESSION_SECRET is not set. Sessions will be invalidated on restart.");
}

const app = express();
app.disable("x-powered-by");
app.set("trust proxy", true);
app.use(express.json({ limit: maxBodyBytes }));

app.use((req, res, next) => {
  res.setHeader("X-Content-Type-Options", "nosniff");
  res.setHeader("Referrer-Policy", "strict-origin-when-cross-origin");
  res.setHeader("X-Frame-Options", "SAMEORIGIN");
  next();
});

app.get("/healthz", (_req, res) => {
  res.type("text/plain").send("ok");
});

app.get("/api/auth/status", (req, res) => {
  const user = getSessionUser(req);
  res.json({
    authRequired,
    oauthConfigured: oauthConfig.configured,
    oauthProvider: oauthConfig.providerName,
    authenticated: Boolean(user),
    user,
    aiConfigured: Boolean(openaiCredential),
    openaiModel,
    aiCredentialMode: openaiCredential ? "server-side-bearer" : "missing",
    webSearchEnabled: enableWebSearch,
    docs: {
      openaiAuth: "https://developers.openai.com/api/reference/overview#authentication",
      responses: "https://developers.openai.com/api/reference/responses/create",
      appsOauth: "https://developers.openai.com/apps-sdk/build/auth",
    },
  });
});

app.get("/api/auth/login", (req, res) => {
  if (!oauthConfig.configured) {
    return res.status(503).json({
      error: "oauth_not_configured",
      message: "OAuth/OIDC 환경변수가 설정되지 않았습니다.",
    });
  }

  const returnTo = sanitizeReturnTo(req.query.returnTo);
  const state = randomToken();
  const nonce = randomToken();
  const codeVerifier = randomToken(64);
  const codeChallenge = base64url(crypto.createHash("sha256").update(codeVerifier).digest());
  const statePayload = {
    state,
    nonce,
    codeVerifier,
    returnTo,
    exp: Date.now() + 10 * 60 * 1000,
  };

  setSignedCookie(res, "ir_oauth_state", statePayload, {
    httpOnly: true,
    secure: secureCookies,
    sameSite: "Lax",
    maxAge: 10 * 60,
    path: "/api/auth",
  });

  const params = new URLSearchParams({
    response_type: "code",
    client_id: oauthConfig.clientId,
    redirect_uri: oauthConfig.redirectUri,
    scope: oauthConfig.scopes,
    state,
    code_challenge: codeChallenge,
    code_challenge_method: "S256",
  });
  if (oauthConfig.scopes.includes("openid")) {
    params.set("nonce", nonce);
  }
  if (oauthConfig.extraAuthorizationParams) {
    for (const [key, value] of Object.entries(oauthConfig.extraAuthorizationParams)) {
      params.set(key, value);
    }
  }

  res.redirect(`${oauthConfig.authorizationUrl}?${params.toString()}`);
});

app.get("/api/auth/callback", async (req, res) => {
  if (!oauthConfig.configured) {
    return res.status(503).send("OAuth/OIDC 환경변수가 설정되지 않았습니다.");
  }

  const stateCookie = getSignedCookie(req, "ir_oauth_state");
  clearCookie(res, "ir_oauth_state", { path: "/api/auth" });

  if (!stateCookie || stateCookie.exp < Date.now()) {
    return res.status(400).send("OAuth state가 만료되었거나 유효하지 않습니다.");
  }
  if (!req.query.state || req.query.state !== stateCookie.state) {
    return res.status(400).send("OAuth state 검증에 실패했습니다.");
  }
  if (!req.query.code || typeof req.query.code !== "string") {
    return res.status(400).send("OAuth authorization code가 없습니다.");
  }

  try {
    const tokenSet = await exchangeCodeForToken(req.query.code, stateCookie.codeVerifier);
    const profile = await loadOAuthProfile(tokenSet);
    if (!profile.email) {
      return res.status(403).send("OAuth 프로필에서 이메일을 확인할 수 없습니다.");
    }
    if (allowedEmails.length > 0 && !allowedEmails.includes(profile.email.toLowerCase())) {
      return res.status(403).send("허용된 이메일 계정이 아닙니다.");
    }

    setSignedCookie(res, "ir_session", {
      provider: oauthConfig.providerName,
      email: profile.email,
      name: profile.name || profile.email,
      picture: profile.picture || "",
      exp: Date.now() + 12 * 60 * 60 * 1000,
    }, {
      httpOnly: true,
      secure: secureCookies,
      sameSite: "Lax",
      maxAge: 12 * 60 * 60,
      path: "/",
    });

    res.redirect(stateCookie.returnTo || "/");
  } catch (error) {
    console.error("[investor-research] OAuth callback failed", error);
    res.status(502).send("OAuth 인증 처리 중 오류가 발생했습니다.");
  }
});

app.post("/api/auth/logout", (_req, res) => {
  clearCookie(res, "ir_session", { path: "/" });
  res.json({ ok: true });
});

const rateLimitMemory = new Map();

app.post("/api/research/analyze", async (req, res) => {
  const user = getSessionUser(req);
  if (authRequired && !user) {
    return res.status(401).json({
      error: "authentication_required",
      message: oauthConfig.configured
        ? "AI 분석을 실행하려면 OAuth 로그인이 필요합니다."
        : "AI 분석은 OAuth 설정 후 사용할 수 있습니다.",
    });
  }
  if (!openaiCredential) {
    return res.status(503).json({
      error: "openai_not_configured",
      message: "OPENAI_API_KEY 또는 OPENAI_BEARER_TOKEN이 서버 환경변수에 설정되지 않았습니다.",
    });
  }

  const limiterKey = user?.email || req.ip || "anonymous";
  if (!checkRateLimit(limiterKey, 8, 10 * 60 * 1000)) {
    return res.status(429).json({
      error: "rate_limited",
      message: "AI 분석 요청이 너무 많습니다. 잠시 후 다시 시도하세요.",
    });
  }

  const settings = req.body?.settings || {};
  const filters = req.body?.filters || {};
  const candidates = Array.isArray(req.body?.candidates) ? req.body.candidates.slice(0, 10) : [];
  const prompt = buildInvestorResearchPrompt({ settings, filters, candidates });

  try {
    const client = new OpenAI({
      apiKey: openaiCredential,
      baseURL: env.OPENAI_BASE_URL || undefined,
    });

    const response = await client.responses.create({
      model: openaiModel,
      store: false,
      input: [
        {
          role: "user",
          content: [
            {
              type: "input_text",
              text: prompt,
            },
          ],
        },
      ],
      instructions: [
        "당신은 투자 권유가 아닌 리서치 보조 보고서를 작성하는 한국어 CFA 애널리스트입니다.",
        "확인 가능한 출처 URL이 없는 핵심 수치는 반드시 '확인불가'로 표시합니다.",
        "수익 보장, 확정적 매수 지시, 과장 표현을 금지합니다.",
      ].join("\n"),
      tools: enableWebSearch
        ? [
            {
              type: "web_search_preview",
              search_context_size: "medium",
              user_location: {
                type: "approximate",
                country: "KR",
                timezone: "Asia/Seoul",
              },
            },
          ]
        : [],
      text: {
        format: {
          type: "json_schema",
          name: "investor_research_report",
          strict: true,
          schema: investorResearchSchema,
        },
      },
      max_output_tokens: maxOutputTokens,
    });

    const rawText = extractOutputText(response);
    const citations = extractUrlCitations(response);
    const parsedReport = safeJsonParse(rawText);

    res.json({
      ok: true,
      model: response.model || openaiModel,
      responseId: response.id,
      createdAt: new Date().toISOString(),
      report: parsedReport,
      rawText,
      citations,
      usage: response.usage || null,
      sourceMode: enableWebSearch ? "openai-web-search" : "model-only",
    });
  } catch (error) {
    console.error("[investor-research] OpenAI analysis failed", error);
    res.status(502).json({
      error: "openai_request_failed",
      message: normalizeErrorMessage(error),
    });
  }
});

if (existsSync(distRoot)) {
  app.use(express.static(distRoot, {
    index: false,
    setHeaders(res, filePath) {
      if (/\.(?:js|css|png|jpg|jpeg|gif|svg|ico|webp|woff2?)$/i.test(filePath)) {
        res.setHeader("Cache-Control", "public, max-age=604800, immutable");
      } else {
        res.setHeader("Cache-Control", "no-store");
      }
    },
  }));
  app.get(/.*/, (_req, res) => {
    res.setHeader("Cache-Control", "no-store");
    res.sendFile(path.join(distRoot, "index.html"));
  });
} else {
  app.get(/.*/, (_req, res) => {
    res.status(503).type("text/plain").send("dist folder is missing. Run npm run build first.");
  });
}

app.listen(port, "0.0.0.0", () => {
  console.log(`[investor-research] listening on 0.0.0.0:${port}`);
  console.log(`[investor-research] OAuth configured: ${oauthConfig.configured}`);
  console.log(`[investor-research] OpenAI configured: ${Boolean(openaiCredential)} (${openaiModel})`);
});

function buildOAuthConfig() {
  const provider = (env.OAUTH_PROVIDER || "custom").toLowerCase();
  const defaults = provider === "google"
    ? {
        providerName: "Google",
        authorizationUrl: "https://accounts.google.com/o/oauth2/v2/auth",
        tokenUrl: "https://oauth2.googleapis.com/token",
        userinfoUrl: "https://openidconnect.googleapis.com/v1/userinfo",
        scopes: "openid email profile",
        extraAuthorizationParams: { access_type: "offline", prompt: "select_account" },
      }
    : {
        providerName: env.OAUTH_PROVIDER_NAME || "OAuth",
        authorizationUrl: "",
        tokenUrl: "",
        userinfoUrl: "",
        scopes: "openid email profile",
        extraAuthorizationParams: null,
      };

  const config = {
    providerName: env.OAUTH_PROVIDER_NAME || defaults.providerName,
    clientId: env.OAUTH_CLIENT_ID || "",
    clientSecret: env.OAUTH_CLIENT_SECRET || "",
    authorizationUrl: env.OAUTH_AUTHORIZATION_URL || defaults.authorizationUrl,
    tokenUrl: env.OAUTH_TOKEN_URL || defaults.tokenUrl,
    userinfoUrl: env.OAUTH_USERINFO_URL || defaults.userinfoUrl,
    scopes: env.OAUTH_SCOPES || defaults.scopes,
    redirectUri: env.OAUTH_REDIRECT_URI || `${appBaseUrl}/api/auth/callback`,
    extraAuthorizationParams: defaults.extraAuthorizationParams,
  };
  const explicitEnabled = env.OAUTH_ENABLED;
  const hasMinimum = Boolean(config.clientId && config.authorizationUrl && config.tokenUrl);
  return {
    ...config,
    configured: boolEnv("OAUTH_ENABLED", hasMinimum) && hasMinimum,
  };
}

async function exchangeCodeForToken(code, codeVerifier) {
  const params = new URLSearchParams({
    grant_type: "authorization_code",
    code,
    redirect_uri: oauthConfig.redirectUri,
    client_id: oauthConfig.clientId,
    code_verifier: codeVerifier,
  });
  if (oauthConfig.clientSecret) {
    params.set("client_secret", oauthConfig.clientSecret);
  }

  const response = await fetch(oauthConfig.tokenUrl, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: params,
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(`Token exchange failed: ${response.status} ${JSON.stringify(payload)}`);
  }
  return payload;
}

async function loadOAuthProfile(tokenSet) {
  if (oauthConfig.userinfoUrl && tokenSet.access_token) {
    const response = await fetch(oauthConfig.userinfoUrl, {
      headers: { Authorization: `Bearer ${tokenSet.access_token}` },
    });
    if (response.ok) {
      const profile = await response.json();
      return normalizeProfile(profile);
    }
  }
  if (tokenSet.id_token) {
    const payload = decodeJwtPayload(tokenSet.id_token);
    return normalizeProfile(payload);
  }
  return {};
}

function normalizeProfile(profile) {
  const email = String(profile.email || profile.preferred_username || profile.upn || "").toLowerCase();
  return {
    email,
    name: String(profile.name || profile.given_name || email || ""),
    picture: String(profile.picture || profile.avatar_url || ""),
  };
}

function decodeJwtPayload(jwt) {
  const [, payload] = String(jwt).split(".");
  if (!payload) return {};
  try {
    return JSON.parse(Buffer.from(payload.replace(/-/g, "+").replace(/_/g, "/"), "base64").toString("utf8"));
  } catch {
    return {};
  }
}

function buildInvestorResearchPrompt({ settings, filters, candidates }) {
  return `아래 조건에 맞는 한국 주식 5개 종목을 발굴해 리서치 보고서를 JSON으로 작성하세요.

역할:
- 20년 경력의 CFA 자격보유 시니어 애널리스트
- 모닝스타, 메리츠, NH 출신 수준의 글로벌시장 분석 경험

투자 조건:
- 투자금액: ${settings.investmentAmount ?? 10000000}원
- 투자기간: ${settings.periodMonths ?? 6}개월
- 목표 수익률 입력값: ${settings.targetReturn ?? 20}% 이상
- 고위험 제외한 성장주
- 중점분석: 시장점유율 확대, 신사업 진출
- 한국 시장 업황과 글로벌 경쟁사 대비 국내 기업 경쟁력을 반영
- 단기 모멘텀보다 6개월 관점의 구조적 성장 중시

정량 필터:
- PEG ${filters.maxPeg ?? 1.5} 이하
- 매출 CAGR ${filters.minRevenueCagr ?? 15}% 이상
- ROE ${filters.minRoe ?? 15}% 이상
- 최근 3년 연속 매출성장률 매년 ${filters.minYearlyRevenueGrowth ?? 20}% 이상
- 영업이익률 개선
- 시가총액 ${filters.minMarketCap ?? 100000000000}원 이상

제외 조건:
- 최근 2년 연속 적자 기업 제외
- 감사의견 비적정 기업 제외
- 시가총액 1,000억 원 미만 기업 제외

분석 요구:
1. 듀퐁분석: ROE = 순이익률 × 자산회전율 × 재무레버리지. 빚으로 만든 ROE인지, 마진/효율 기반 ROE인지 구분하세요.
2. 경제적 해자 강/중/약 평가: 브랜드 파워, 전환비용, 네트워크 효과, 원가 우위, 무형자산.
3. 밸류에이션: PER 40% + PBR 30% + DCF 30% = 가중평균 적정가, 안전마진 20% 적용 매수가.
4. 목표가는 매수가 대비 +30% 기준으로 제시하되, 원천 데이터가 없으면 "확인불가"로 표시하세요.
5. 손절가와 손절 이유를 매수 전 기준으로 먼저 정리하세요.
6. 모든 핵심 수치에는 source URL을 붙이고, 출처 없는 수치는 "확인불가"로 표시하세요.
7. "약", "대략", "추정" 같은 표현을 사용하지 마세요.
8. 투자 권유, 수익 보장, 확정적 매수 지시는 금지하고 리서치 시작점으로만 작성하세요.

참고용 현재 UI 후보 데이터:
${JSON.stringify(candidates, null, 2)}

응답은 지정된 JSON Schema를 반드시 따르세요.`;
}

const stringArraySchema = {
  type: "array",
  items: { type: "string" },
};

const investorResearchSchema = {
  type: "object",
  additionalProperties: false,
  required: [
    "summary",
    "marketContext",
    "recommendations",
    "highestConviction",
    "bestRiskReward",
    "dataLimitations",
    "disclaimer",
  ],
  properties: {
    summary: { type: "string" },
    marketContext: { type: "string" },
    recommendations: {
      type: "array",
      items: {
        type: "object",
        additionalProperties: false,
        required: [
          "rank",
          "ticker",
          "name",
          "coreInvestmentPoint",
          "allocationPercent",
          "currentPriceKrw",
          "buyPriceKrw",
          "targetPriceKrw",
          "stopLossPriceKrw",
          "marketCapKrw",
          "metrics",
          "dupont",
          "valuation",
          "moat",
          "investmentPoints",
          "riskFactors",
          "tradePlan",
          "sourceUrls",
          "confidenceNote",
          "dataQuality",
        ],
        properties: {
          rank: { type: "number" },
          ticker: { type: "string" },
          name: { type: "string" },
          coreInvestmentPoint: { type: "string" },
          allocationPercent: { type: "number" },
          currentPriceKrw: { type: "string" },
          buyPriceKrw: { type: "string" },
          targetPriceKrw: { type: "string" },
          stopLossPriceKrw: { type: "string" },
          marketCapKrw: { type: "string" },
          metrics: {
            type: "object",
            additionalProperties: false,
            required: ["per", "pbr", "peg", "roe", "operatingMargin", "netMargin", "revenueCagr3y", "profitCagr3y"],
            properties: {
              per: { type: "string" },
              pbr: { type: "string" },
              peg: { type: "string" },
              roe: { type: "string" },
              operatingMargin: { type: "string" },
              netMargin: { type: "string" },
              revenueCagr3y: { type: "string" },
              profitCagr3y: { type: "string" },
            },
          },
          dupont: {
            type: "object",
            additionalProperties: false,
            required: ["netMargin", "assetTurnover", "leverage", "roe", "qualityComment"],
            properties: {
              netMargin: { type: "string" },
              assetTurnover: { type: "string" },
              leverage: { type: "string" },
              roe: { type: "string" },
              qualityComment: { type: "string" },
            },
          },
          valuation: {
            type: "object",
            additionalProperties: false,
            required: ["perFairValue", "pbrFairValue", "dcfFairValue", "weightedFairValue", "marginBuyPrice", "marginOfSafety", "methodNote"],
            properties: {
              perFairValue: { type: "string" },
              pbrFairValue: { type: "string" },
              dcfFairValue: { type: "string" },
              weightedFairValue: { type: "string" },
              marginBuyPrice: { type: "string" },
              marginOfSafety: { type: "string" },
              methodNote: { type: "string" },
            },
          },
          moat: {
            type: "object",
            additionalProperties: false,
            required: ["brand", "switchingCost", "networkEffect", "costAdvantage", "intangibleAssets", "overall"],
            properties: {
              brand: { type: "string" },
              switchingCost: { type: "string" },
              networkEffect: { type: "string" },
              costAdvantage: { type: "string" },
              intangibleAssets: { type: "string" },
              overall: { type: "string" },
            },
          },
          investmentPoints: stringArraySchema,
          riskFactors: stringArraySchema,
          tradePlan: stringArraySchema,
          sourceUrls: stringArraySchema,
          confidenceNote: { type: "string" },
          dataQuality: { type: "string" },
        },
      },
    },
    highestConviction: { type: "string" },
    bestRiskReward: { type: "string" },
    dataLimitations: stringArraySchema,
    disclaimer: { type: "string" },
  },
};

function extractOutputText(response) {
  if (typeof response.output_text === "string" && response.output_text.trim()) {
    return response.output_text;
  }
  const chunks = [];
  for (const item of response.output || []) {
    if (item.type !== "message") continue;
    for (const part of item.content || []) {
      if (part.type === "output_text" && typeof part.text === "string") {
        chunks.push(part.text);
      }
    }
  }
  return chunks.join("\n").trim();
}

function extractUrlCitations(response) {
  const citations = [];
  for (const item of response.output || []) {
    if (item.type !== "message") continue;
    for (const part of item.content || []) {
      if (part.type !== "output_text") continue;
      for (const annotation of part.annotations || []) {
        if (annotation.type === "url_citation") {
          citations.push({
            title: annotation.title || "",
            url: annotation.url || "",
          });
        }
      }
    }
  }
  return citations.filter((item, index, arr) => item.url && arr.findIndex((x) => x.url === item.url) === index);
}

function safeJsonParse(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function normalizeErrorMessage(error) {
  if (error?.response?.data) return JSON.stringify(error.response.data);
  if (error?.message) return error.message;
  return "알 수 없는 오류";
}

function getSessionUser(req) {
  const session = getSignedCookie(req, "ir_session");
  if (!session || session.exp < Date.now()) return null;
  return {
    email: session.email,
    name: session.name,
    picture: session.picture,
    provider: session.provider,
  };
}

function setSignedCookie(res, name, payload, options) {
  const value = signPayload(payload);
  res.append("Set-Cookie", serializeCookie(name, value, options));
}

function getSignedCookie(req, name) {
  const raw = parseCookies(req.headers.cookie || "")[name];
  if (!raw) return null;
  return verifyPayload(raw);
}

function signPayload(payload) {
  const encoded = base64url(Buffer.from(JSON.stringify(payload), "utf8"));
  const signature = hmac(encoded);
  return `${encoded}.${signature}`;
}

function verifyPayload(value) {
  const [encoded, signature] = String(value).split(".");
  if (!encoded || !signature) return null;
  const expected = hmac(encoded);
  if (!safeEqual(signature, expected)) return null;
  try {
    return JSON.parse(Buffer.from(encoded.replace(/-/g, "+").replace(/_/g, "/"), "base64").toString("utf8"));
  } catch {
    return null;
  }
}

function hmac(value) {
  return base64url(crypto.createHmac("sha256", sessionSecret).update(value).digest());
}

function safeEqual(a, b) {
  const left = Buffer.from(String(a));
  const right = Buffer.from(String(b));
  return left.length === right.length && crypto.timingSafeEqual(left, right);
}

function parseCookies(header) {
  return Object.fromEntries(
    header
      .split(";")
      .map((part) => part.trim())
      .filter(Boolean)
      .map((part) => {
        const index = part.indexOf("=");
        if (index === -1) return [part, ""];
        return [part.slice(0, index), decodeURIComponent(part.slice(index + 1))];
      }),
  );
}

function serializeCookie(name, value, options = {}) {
  const parts = [`${name}=${encodeURIComponent(value)}`];
  if (options.maxAge !== undefined) parts.push(`Max-Age=${Math.floor(options.maxAge)}`);
  if (options.path) parts.push(`Path=${options.path}`);
  if (options.httpOnly) parts.push("HttpOnly");
  if (options.secure) parts.push("Secure");
  if (options.sameSite) parts.push(`SameSite=${options.sameSite}`);
  return parts.join("; ");
}

function clearCookie(res, name, options = {}) {
  res.append("Set-Cookie", serializeCookie(name, "", {
    path: options.path || "/",
    maxAge: 0,
    httpOnly: true,
    secure: secureCookies,
    sameSite: "Lax",
  }));
}

function checkRateLimit(key, maxCount, windowMs) {
  const now = Date.now();
  const bucket = rateLimitMemory.get(key) || [];
  const recent = bucket.filter((timestamp) => now - timestamp < windowMs);
  if (recent.length >= maxCount) {
    rateLimitMemory.set(key, recent);
    return false;
  }
  recent.push(now);
  rateLimitMemory.set(key, recent);
  return true;
}

function randomToken(size = 32) {
  return crypto.randomBytes(size).toString("base64url");
}

function base64url(buffer) {
  return Buffer.from(buffer).toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function boolEnv(name, defaultValue) {
  const value = env[name];
  if (value === undefined || value === "") return defaultValue;
  return ["1", "true", "yes", "on"].includes(value.toLowerCase());
}

function firstNonEmpty(...values) {
  return values.find((value) => typeof value === "string" && value.trim())?.trim() || "";
}

function parseCsv(value) {
  return String(value || "")
    .split(",")
    .map((item) => item.trim().toLowerCase())
    .filter(Boolean);
}

function stripTrailingSlash(value) {
  return String(value).replace(/\/+$/g, "");
}

function sanitizeReturnTo(value) {
  const candidate = Array.isArray(value) ? value[0] : value;
  if (typeof candidate !== "string" || !candidate.startsWith("/") || candidate.startsWith("//")) {
    return "/";
  }
  return candidate;
}
