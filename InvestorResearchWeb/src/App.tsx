import { useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  BarChart3,
  CheckCircle2,
  ClipboardList,
  Download,
  ExternalLink,
  FileText,
  Filter,
  Info,
  KeyRound,
  LineChart,
  Loader2,
  LogIn,
  LogOut,
  PlugZap,
  Search,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Unplug,
} from "lucide-react";
import { sampleCandidates } from "./data/sampleCandidates";
import type { AiAnalyzeResponse, AiCitation, AiProviderRegistration, AiResearchReport, AuthStatus, PortfolioSettings, RankedCandidate, ScreeningFilters } from "./types";
import { buildOpenAiAnalysisPrompt } from "./lib/reportPrompt";
import { buildAiReportMarkdown } from "./lib/aiReport";
import { buildReportMarkdown, formatMarketCap, formatPercent, formatWon, getScreeningResult, rankCandidates } from "./lib/metrics";

const defaultFilters: ScreeningFilters = {
  maxPeg: 1.5,
  minRevenueCagr: 15,
  minRoe: 15,
  minYearlyRevenueGrowth: 20,
  minMarketCap: 100_000_000_000,
};

const defaultSettings: PortfolioSettings = {
  investmentAmount: 10_000_000,
  periodMonths: 6,
  targetReturn: 20,
  riskPolicy: "고위험 제외",
};

const workflowSteps = ["조건 입력", "정량 필터", "AI 분석", "리스크 검증", "리포트 출력"];

const offlineAuthStatus: AuthStatus = {
  authRequired: true,
  oauthConfigured: false,
  oauthProvider: "OAuth",
  authenticated: false,
  user: null,
  aiConfigured: false,
  openaiModel: "미설정",
  aiCredentialMode: "missing",
  webSearchEnabled: false,
};

type AnalysisState = "idle" | "loading" | "success" | "error";

function NumberInput({
  label,
  value,
  suffix,
  onChange,
}: {
  label: string;
  value: number;
  suffix?: string;
  onChange: (value: number) => void;
}) {
  return (
    <label className="field">
      <span>{label}</span>
      <div className="fieldInput">
        <input value={value} onChange={(event) => onChange(Number(event.target.value.replace(/[^0-9.]/g, "")) || 0)} />
        {suffix ? <em>{suffix}</em> : null}
      </div>
    </label>
  );
}

function StatusPill({ passed, children }: { passed: boolean; children: string }) {
  return <span className={passed ? "pill pass" : "pill fail"}>{children}</span>;
}

function SourceBadge({ status }: { status: string }) {
  if (status === "verified") return <span className="sourceBadge verified">검증</span>;
  if (status === "unavailable") return <span className="sourceBadge unavailable">확인불가</span>;
  return <span className="sourceBadge demo">데모</span>;
}

function AuthControl({ status, onRefresh }: { status: AuthStatus | null; onRefresh: () => void }) {
  const logout = async () => {
    await fetch("/api/auth/logout", { method: "POST" }).catch(() => undefined);
    onRefresh();
  };

  if (!status) {
    return <span className="authBadge muted"><Loader2 size={14} className="spin" /> 인증 확인</span>;
  }

  if (!status.oauthConfigured) {
    return (
      <span className="authBadge warning" title="OAUTH_CLIENT_ID, OAUTH_TOKEN_URL 등 서버 환경변수 설정 필요">
        <ShieldCheck size={14} /> OAuth 미설정
      </span>
    );
  }

  if (!status.authenticated) {
    return (
      <a className="secondaryButton authLink" href="/api/auth/login">
        <LogIn size={16} /> {status.oauthProvider} 로그인
      </a>
    );
  }

  return (
    <div className="authUser">
      <span title={status.user?.email}>
        <ShieldCheck size={14} /> {status.user?.name || status.user?.email}
      </span>
      <button className="iconButton" type="button" onClick={logout} aria-label="로그아웃">
        <LogOut size={15} />
      </button>
    </div>
  );
}

function AiProviderPanel({
  providers,
  loading,
  error,
  onRefresh,
}: {
  providers: AiProviderRegistration[];
  loading: boolean;
  error: string | null;
  onRefresh: () => void;
}) {
  const disconnect = async (providerId: string) => {
    await fetch(`/api/ai-providers/${providerId}/disconnect`, { method: "POST" }).catch(() => undefined);
    onRefresh();
  };

  return (
    <section className="providerPanel" aria-label="AI OAuth 등록">
      <div className="panelHeader compact">
        <div>
          <p className="sectionKicker">AI OAuth 등록</p>
          <h2>Claude · GPT · Gemini · Perplexity 연결</h2>
        </div>
        <span className="providerHint">
          <KeyRound size={14} /> OAuth endpoint 설정 시 활성화
        </span>
      </div>
      {error ? <div className="analysisStatus error"><AlertTriangle size={15} /> {error}</div> : null}
      <div className="providerGrid">
        {providers.map((provider) => (
          <article className={provider.registered ? "providerCard registered" : "providerCard"} key={provider.id}>
            <div className="providerTop">
              <div className={`providerLogo ${provider.id}`}>{provider.shortLabel.slice(0, 2).toUpperCase()}</div>
              <div>
                <strong>{provider.shortLabel}</strong>
                <span>{provider.modelFamily}</span>
              </div>
              <span className={provider.registered ? "providerState ok" : provider.configured ? "providerState ready" : "providerState off"}>
                {provider.registered ? "등록 완료" : provider.configured ? "등록 가능" : "설정 필요"}
              </span>
            </div>
            <p>{provider.authNote}</p>
            <div className="providerMeta">
              <span>Callback</span>
              <code>{provider.callbackUrl}</code>
            </div>
            {provider.registered ? (
              <div className="providerActions">
                <span className="registeredAccount"><ShieldCheck size={13} /> {provider.account}</span>
                <button className="secondaryButton compactButton" type="button" onClick={() => disconnect(provider.id)}>
                  <Unplug size={15} /> 해제
                </button>
              </div>
            ) : (
              <div className="providerActions">
                <a className={provider.configured ? "primaryButton compactButton" : "secondaryButton compactButton disabledLink"} href={provider.configured ? `/api/ai-providers/${provider.id}/oauth/start` : provider.docsUrl}>
                  <PlugZap size={15} /> {provider.shortLabel} OAuth 등록
                </a>
                {!provider.configured ? <span className="requiredEnv">{provider.requiredEnv.slice(0, 3).join(" · ")}</span> : null}
              </div>
            )}
          </article>
        ))}
        {loading && providers.length === 0 ? (
          <article className="providerCard">
            <div className="analysisStatus loading"><Loader2 size={15} className="spin" /> AI 제공자 상태 확인 중</div>
          </article>
        ) : null}
      </div>
    </section>
  );
}

function SummaryCards({ ranked, total, excluded }: { ranked: RankedCandidate[]; total: number; excluded: number }) {
  const avgRoe = ranked.length ? ranked.reduce((sum, item) => sum + item.roe, 0) / ranked.length : 0;
  const avgUpside = ranked.length ? ranked.reduce((sum, item) => sum + (item.weightedFairValue / item.currentPrice - 1) * 100, 0) / ranked.length : 0;
  return (
    <section className="summaryGrid" aria-label="요약 지표">
      <article className="summaryCard">
        <span className="summaryLabel">필터 통과</span>
        <strong>{ranked.length}개</strong>
        <p>분석 대상 {total}개 중 제외 {excluded}개</p>
      </article>
      <article className="summaryCard">
        <span className="summaryLabel">평균 ROE</span>
        <strong>{formatPercent(avgRoe)}</strong>
        <p>듀퐁 분석으로 품질 재확인</p>
      </article>
      <article className="summaryCard">
        <span className="summaryLabel">평균 적정가 괴리</span>
        <strong>{formatPercent(avgUpside)}</strong>
        <p>PER 40% + PBR 30% + DCF 30%</p>
      </article>
      <article className="summaryCard caution">
        <span className="summaryLabel">출처 상태</span>
        <strong>데모</strong>
        <p>실거래 전 OpenDART/KRX 연결 필요</p>
      </article>
    </section>
  );
}

function FilterPanel({
  filters,
  settings,
  onFiltersChange,
  onSettingsChange,
  onReset,
}: {
  filters: ScreeningFilters;
  settings: PortfolioSettings;
  onFiltersChange: (filters: ScreeningFilters) => void;
  onSettingsChange: (settings: PortfolioSettings) => void;
  onReset: () => void;
}) {
  return (
    <section className="filterPanel" aria-label="스크리닝 조건">
      <div className="panelHeader compact">
        <div>
          <p className="sectionKicker">스크리닝 조건</p>
          <h2>피터린치 PEG와 성장성 필터</h2>
        </div>
        <button className="secondaryButton" type="button" onClick={onReset}>
          <SlidersHorizontal size={16} /> 기본값
        </button>
      </div>
      <div className="filterGrid">
        <NumberInput label="투자금액" value={settings.investmentAmount} suffix="원" onChange={(investmentAmount) => onSettingsChange({ ...settings, investmentAmount })} />
        <NumberInput label="기간" value={settings.periodMonths} suffix="개월" onChange={(periodMonths) => onSettingsChange({ ...settings, periodMonths })} />
        <NumberInput label="목표수익률" value={settings.targetReturn} suffix="%" onChange={(targetReturn) => onSettingsChange({ ...settings, targetReturn })} />
        <NumberInput label="PEG 상한" value={filters.maxPeg} onChange={(maxPeg) => onFiltersChange({ ...filters, maxPeg })} />
        <NumberInput label="매출 CAGR" value={filters.minRevenueCagr} suffix="%+" onChange={(minRevenueCagr) => onFiltersChange({ ...filters, minRevenueCagr })} />
        <NumberInput label="ROE" value={filters.minRoe} suffix="%+" onChange={(minRoe) => onFiltersChange({ ...filters, minRoe })} />
        <NumberInput label="3년 매출성장" value={filters.minYearlyRevenueGrowth} suffix="%+" onChange={(minYearlyRevenueGrowth) => onFiltersChange({ ...filters, minYearlyRevenueGrowth })} />
        <NumberInput label="시총 하한" value={Math.round(filters.minMarketCap / 100_000_000)} suffix="억+" onChange={(value) => onFiltersChange({ ...filters, minMarketCap: value * 100_000_000 })} />
      </div>
    </section>
  );
}

function CandidateTable({
  ranked,
  selectedTicker,
  onSelect,
  searchTerm,
  onSearchTermChange,
}: {
  ranked: RankedCandidate[];
  selectedTicker: string;
  onSelect: (candidate: RankedCandidate) => void;
  searchTerm: string;
  onSearchTermChange: (value: string) => void;
}) {
  return (
    <section className="tablePanel" aria-label="후보 종목 테이블">
      <div className="panelHeader tableHeader">
        <div>
          <p className="sectionKicker">후보 테이블</p>
          <h2>정량 필터 통과 종목</h2>
        </div>
        <div className="tableTools">
          <div className="searchBox">
            <Search size={15} />
            <input
              aria-label="종목명 또는 섹터 검색"
              value={searchTerm}
              placeholder="종목명/섹터 검색"
              onChange={(event) => onSearchTermChange(event.target.value)}
            />
          </div>
          <button className="secondaryButton" type="button">
            <Filter size={16} /> 필터
          </button>
        </div>
      </div>
      <div className="tableWrap">
        <table>
          <thead>
            <tr>
              <th>순위</th>
              <th>종목</th>
              <th>배분</th>
              <th>현재가</th>
              <th>매수가</th>
              <th>목표가</th>
              <th>시총</th>
              <th>PER</th>
              <th>PBR</th>
              <th>PEG</th>
              <th>ROE</th>
              <th>영업이익률</th>
              <th>출처</th>
            </tr>
          </thead>
          <tbody>
            {ranked.map((candidate) => (
              <tr
                key={candidate.ticker}
                className={selectedTicker === candidate.ticker ? "selected" : ""}
                onClick={() => onSelect(candidate)}
              >
                <td><strong>{candidate.rank}</strong></td>
                <td>
                  <div className="stockCell">
                    <strong>{candidate.name}</strong>
                    <span>{candidate.ticker} · {candidate.sector}</span>
                  </div>
                </td>
                <td><span className="allocation">{candidate.allocationPercent}%</span></td>
                <td>{formatWon(candidate.currentPrice)}</td>
                <td>{formatWon(candidate.marginBuyPrice)}</td>
                <td>{formatWon(candidate.targetPrice)}</td>
                <td>{formatMarketCap(candidate.marketCap)}</td>
                <td>{candidate.per.toFixed(1)}</td>
                <td>{candidate.pbr.toFixed(1)}</td>
                <td>{candidate.peg.toFixed(2)}</td>
                <td>{formatPercent(candidate.roe)}</td>
                <td>{formatPercent(candidate.operatingMargin)}</td>
                <td><SourceBadge status={candidate.sources[0]?.status ?? "unavailable"} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ExclusionList({ filters }: { filters: ScreeningFilters }) {
  const rows = sampleCandidates.map((candidate) => ({ candidate, result: getScreeningResult(candidate, filters) }));
  return (
    <section className="exclusionPanel">
      <div className="panelHeader compact">
        <div>
          <p className="sectionKicker">검증 로그</p>
          <h2>제외 조건 추적</h2>
        </div>
      </div>
      <div className="exclusionList">
        {rows.map(({ candidate, result }) => (
          <div className="exclusionRow" key={candidate.ticker}>
            <div>
              <strong>{candidate.name}</strong>
              <span>{result.passed ? "모든 정량 조건 통과" : result.reasons.join(" · ")}</span>
            </div>
            <StatusPill passed={result.passed}>{result.passed ? "통과" : "제외"}</StatusPill>
          </div>
        ))}
      </div>
    </section>
  );
}

function DetailDrawer({ selected }: { selected: RankedCandidate }) {
  const dupontRoe = selected.netMargin * selected.assetTurnover * selected.leverage;
  return (
    <aside className="detailDrawer" aria-label="선택 종목 상세">
      <div className="drawerTitle">
        <div>
          <p className="sectionKicker">선택 종목</p>
          <h2>{selected.name}</h2>
          <span>{selected.ticker} · {selected.market} · {selected.sector}</span>
        </div>
        <span className="rankBadge">{selected.rank}순위</span>
      </div>

      <div className="noticeStrip small"><Info size={15} /> 실데이터 연결 전 데모 산출값입니다.</div>

      <div className="drawerMetricGrid">
        <div><span>배분금액</span><strong>{formatWon(selected.allocationAmount)}</strong></div>
        <div><span>적정가</span><strong>{formatWon(selected.weightedFairValue)}</strong></div>
        <div><span>손절가</span><strong>{formatWon(selected.stopLossPrice)}</strong></div>
        <div><span>점수</span><strong>{selected.score.toFixed(1)}</strong></div>
      </div>

      <section className="drawerSection">
        <h3><LineChart size={16} /> 핵심 투자 포인트</h3>
        <ol>{selected.growthDrivers.map((item) => <li key={item}>{item}</li>)}</ol>
      </section>

      <section className="drawerSection">
        <h3><ShieldCheck size={16} /> 경제적 해자</h3>
        <div className="moatGrid">
          <span>브랜드 <b>{selected.moat.brand}</b></span>
          <span>전환비용 <b>{selected.moat.switchingCost}</b></span>
          <span>네트워크 <b>{selected.moat.networkEffect}</b></span>
          <span>원가우위 <b>{selected.moat.costAdvantage}</b></span>
          <span>무형자산 <b>{selected.moat.intangibleAssets}</b></span>
        </div>
      </section>

      <section className="drawerSection">
        <h3><BarChart3 size={16} /> 듀퐁 ROE</h3>
        <div className="dupontLine">
          <strong>{formatPercent(selected.netMargin)}</strong>
          <span>×</span>
          <strong>{selected.assetTurnover.toFixed(2)}회</strong>
          <span>×</span>
          <strong>{selected.leverage.toFixed(2)}배</strong>
          <span>=</span>
          <strong>{formatPercent(dupontRoe)}</strong>
        </div>
        <p>표시 ROE {formatPercent(selected.roe)}와 듀퐁 산출값의 차이는 반올림 및 회계 기준 차이 점검 대상입니다.</p>
      </section>

      <section className="drawerSection">
        <h3><ClipboardList size={16} /> 매매 전략</h3>
        <ul className="strategyList">
          <li>적정가: {formatWon(selected.weightedFairValue)} = PER 40% + PBR 30% + DCF 30%</li>
          <li>안전마진 매수가: {formatWon(selected.marginBuyPrice)}</li>
          <li>1차 매수: 배분금액의 60%</li>
          <li>2차 매수: {formatWon(Math.round(selected.marginBuyPrice * 0.96))} / 배분금액의 40%</li>
          <li>손절가: {formatWon(selected.stopLossPrice)}</li>
        </ul>
      </section>

      <section className="drawerSection risk">
        <h3><AlertTriangle size={16} /> 리스크 요인</h3>
        <ol>{selected.riskFactors.map((item) => <li key={item}>{item}</li>)}</ol>
      </section>

      <section className="drawerSection sources">
        <h3><FileText size={16} /> 출처 URL</h3>
        {selected.sources.map((source) => (
          <a key={source.label} href={source.url} target="_blank" rel="noreferrer">
            {source.label} <SourceBadge status={source.status} />
          </a>
        ))}
      </section>
    </aside>
  );
}

function ReportActions({
  ranked,
  filters,
  settings,
  aiReport,
  aiCitations,
  analysisState,
  analysisError,
}: {
  ranked: RankedCandidate[];
  filters: ScreeningFilters;
  settings: PortfolioSettings;
  aiReport: AiResearchReport | null;
  aiCitations: AiCitation[];
  analysisState: AnalysisState;
  analysisError: string | null;
}) {
  const report = useMemo(() => buildReportMarkdown(ranked, settings), [ranked, settings]);
  const prompt = useMemo(() => buildOpenAiAnalysisPrompt(ranked, filters, settings), [ranked, filters, settings]);
  const aiMarkdown = useMemo(() => (aiReport ? buildAiReportMarkdown(aiReport, aiCitations) : ""), [aiReport, aiCitations]);

  const download = (filename: string, body: string) => {
    const blob = new Blob([body], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = filename;
    anchor.click();
    URL.revokeObjectURL(url);
  };

  return (
    <section className="reportPanel">
      <div className="panelHeader compact">
        <div>
          <p className="sectionKicker">리포트 출력</p>
          <h2>{aiReport ? "AI 실시간 분석 결과" : "AI 호출 준비 자료"}</h2>
        </div>
      </div>
      {analysisState === "loading" ? (
        <div className="analysisStatus loading"><Loader2 size={15} className="spin" /> OpenAI 웹검색 기반 분석 중입니다.</div>
      ) : null}
      {analysisState === "error" && analysisError ? (
        <div className="analysisStatus error"><AlertTriangle size={15} /> {analysisError}</div>
      ) : null}
      {analysisState === "success" && aiReport ? (
        <div className="analysisStatus success"><Sparkles size={15} /> 실시간 AI 분석 결과가 생성되었습니다.</div>
      ) : null}
      <div className="reportActions">
        <button type="button" className="primaryButton" onClick={() => download("investment-research-report.md", report)}>
          <Download size={16} /> Markdown 저장
        </button>
        {aiMarkdown ? (
          <button type="button" className="secondaryButton" onClick={() => download("ai-investment-research-report.md", aiMarkdown)}>
            <Download size={16} /> AI 결과 저장
          </button>
        ) : null}
        <button type="button" className="secondaryButton" onClick={() => navigator.clipboard.writeText(prompt)}>
          <ClipboardList size={16} /> AI 프롬프트 복사
        </button>
      </div>
      {aiReport ? (
        <div className="aiReportPreview">
          <p>{aiReport.summary}</p>
          <div className="aiRecommendationList">
            {aiReport.recommendations.slice(0, 5).map((item) => (
              <article key={`${item.rank}-${item.ticker}`}>
                <strong>{item.rank}순위 · {item.name}</strong>
                <span>{item.ticker} · {item.coreInvestmentPoint}</span>
                <em>매수가 {item.buyPriceKrw} / 목표가 {item.targetPriceKrw} / 손절가 {item.stopLossPriceKrw}</em>
              </article>
            ))}
          </div>
          {aiCitations.length > 0 ? (
            <div className="citationList">
              {aiCitations.slice(0, 4).map((citation) => (
                <a key={citation.url} href={citation.url} target="_blank" rel="noreferrer">
                  <ExternalLink size={13} /> {citation.title || citation.url}
                </a>
              ))}
            </div>
          ) : null}
        </div>
      ) : (
        <pre>{report.slice(0, 1200)}{report.length > 1200 ? "\n..." : ""}</pre>
      )}
    </section>
  );
}

export default function App() {
  const [filters, setFilters] = useState<ScreeningFilters>(defaultFilters);
  const [settings, setSettings] = useState<PortfolioSettings>(defaultSettings);
  const [searchTerm, setSearchTerm] = useState("");
  const [analysisRunAt, setAnalysisRunAt] = useState<string | null>(null);
  const [analysisState, setAnalysisState] = useState<AnalysisState>("idle");
  const [analysisError, setAnalysisError] = useState<string | null>(null);
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null);
  const [aiReport, setAiReport] = useState<AiResearchReport | null>(null);
  const [aiCitations, setAiCitations] = useState<AiCitation[]>([]);
  const [aiProviders, setAiProviders] = useState<AiProviderRegistration[]>([]);
  const [aiProvidersLoading, setAiProvidersLoading] = useState(false);
  const [aiProviderError, setAiProviderError] = useState<string | null>(null);

  const ranked = useMemo(() => rankCandidates(sampleCandidates, filters, settings), [filters, settings]);
  const visibleRanked = useMemo(() => {
    const term = searchTerm.trim().toLowerCase();
    if (!term) return ranked;
    return ranked.filter((item) => {
      const haystack = `${item.name} ${item.ticker} ${item.sector}`.toLowerCase();
      return haystack.includes(term);
    });
  }, [ranked, searchTerm]);
  const [selectedTicker, setSelectedTicker] = useState<string>("DEMO-002");
  const selected = visibleRanked.find((item) => item.ticker === selectedTicker) ?? visibleRanked[0] ?? ranked[0];
  const excludedCount = sampleCandidates.length - ranked.length;
  const effectiveAuthStatus = authStatus ?? offlineAuthStatus;

  const refreshAuthStatus = async () => {
    try {
      const response = await fetch("/api/auth/status", { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`status ${response.status}`);
      const status = await response.json() as AuthStatus;
      setAuthStatus(status);
    } catch {
      setAuthStatus(offlineAuthStatus);
    }
  };

  const refreshAiProviders = async () => {
    setAiProvidersLoading(true);
    setAiProviderError(null);
    try {
      const response = await fetch("/api/ai-providers", { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`status ${response.status}`);
      const payload = await response.json() as { providers: AiProviderRegistration[] };
      setAiProviders(payload.providers || []);
    } catch {
      setAiProviderError("AI 제공자 등록 상태를 확인하지 못했습니다. Node API 서버 상태를 확인하세요.");
    } finally {
      setAiProvidersLoading(false);
    }
  };

  useEffect(() => {
    void refreshAuthStatus();
    void refreshAiProviders();
  }, []);

  const runAnalysis = async () => {
    const now = new Date().toLocaleTimeString("ko-KR", { hour: "2-digit", minute: "2-digit" });
    setAnalysisRunAt(now);
    setAnalysisError(null);

    if (effectiveAuthStatus.authRequired && effectiveAuthStatus.oauthConfigured && !effectiveAuthStatus.authenticated) {
      window.location.href = "/api/auth/login";
      return;
    }

    if (!effectiveAuthStatus.aiConfigured) {
      setAnalysisState("error");
      setAnalysisError("AI 서버 환경변수 OPENAI_API_KEY 또는 OPENAI_BEARER_TOKEN이 아직 설정되지 않았습니다.");
      return;
    }

    setAnalysisState("loading");
    try {
      const response = await fetch("/api/research/analyze", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json",
        },
        body: JSON.stringify({ filters, settings, candidates: ranked }),
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        throw new Error(payload.message || payload.error || `AI 분석 요청 실패: ${response.status}`);
      }
      const data = payload as AiAnalyzeResponse;
      if (!data.report) {
        throw new Error("AI 응답을 구조화 JSON으로 해석하지 못했습니다.");
      }
      setAiReport(data.report);
      setAiCitations(data.citations || []);
      setAnalysisState("success");
      setAnalysisRunAt(new Date(data.createdAt).toLocaleTimeString("ko-KR", { hour: "2-digit", minute: "2-digit" }));
    } catch (error) {
      setAnalysisState("error");
      setAnalysisError(error instanceof Error ? error.message : "AI 분석 중 오류가 발생했습니다.");
    }
  };

  const runStateLabel = analysisState === "loading"
    ? "AI 분석 중"
    : analysisState === "success"
      ? `AI 분석 완료 · ${analysisRunAt}`
      : analysisState === "error"
        ? `분석 점검 필요 · ${analysisRunAt}`
        : analysisRunAt
          ? `분석 완료 · ${analysisRunAt}`
          : "진행 가능 · 데모 모드";

  return (
    <main className="appShell">
      <nav className="sideRail" aria-label="워크플로우">
        <div className="brandMark">IR</div>
        <div className="railItems">
          {workflowSteps.map((step, index) => (
            <button key={step} className={index === 1 ? "active" : ""} type="button">
              <span>{index + 1}</span>
              {step}
            </button>
          ))}
        </div>
      </nav>

      <div className="contentShell">
        <header className="topBar">
          <div>
            <h1>투자 리서치 플랜</h1>
            <p>정량 스크리닝, 듀퐁 ROE, 밸류에이션, 출처 체크를 한 화면에서 검토합니다.</p>
          </div>
          <div className="topActions">
            <span className="runState">
              {analysisState === "loading" ? <Loader2 size={16} className="spin" /> : <CheckCircle2 size={16} />}
              {runStateLabel}
            </span>
            <AuthControl status={authStatus} onRefresh={refreshAuthStatus} />
            <button className="primaryButton" type="button" disabled={analysisState === "loading"} onClick={runAnalysis}>
              {analysisState === "loading" ? "실행 중" : effectiveAuthStatus.aiConfigured ? "AI 분석 실행" : "분석 실행"}
            </button>
          </div>
        </header>

        <div className="noticeStrip">
          <Info size={16} /> 투자 권유가 아닌 리서치 보조 자료입니다. OAuth는 사용자 접근 제어에, OpenAI 자격 증명은 서버 환경변수에만 사용됩니다.
        </div>

        <SummaryCards ranked={ranked} total={sampleCandidates.length} excluded={excludedCount} />
        <FilterPanel
          filters={filters}
          settings={settings}
          onFiltersChange={setFilters}
          onSettingsChange={setSettings}
          onReset={() => {
            setFilters(defaultFilters);
            setSettings(defaultSettings);
            setSearchTerm("");
          }}
        />

        <AiProviderPanel
          providers={aiProviders}
          loading={aiProvidersLoading}
          error={aiProviderError}
          onRefresh={refreshAiProviders}
        />

        <div className="workspaceGrid">
          <div className="mainColumn">
            <CandidateTable
              ranked={visibleRanked}
              selectedTicker={selected?.ticker ?? ""}
              onSelect={(candidate) => setSelectedTicker(candidate.ticker)}
              searchTerm={searchTerm}
              onSearchTermChange={setSearchTerm}
            />
            <div className="lowerGrid">
              <ExclusionList filters={filters} />
              <ReportActions
                ranked={ranked}
                filters={filters}
                settings={settings}
                aiReport={aiReport}
                aiCitations={aiCitations}
                analysisState={analysisState}
                analysisError={analysisError}
              />
            </div>
          </div>
          {selected ? <DetailDrawer selected={selected} /> : null}
        </div>
      </div>
    </main>
  );
}
