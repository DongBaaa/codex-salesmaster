export type MoatLevel = "강" | "중" | "약";
export type SourceStatus = "demo" | "verified" | "unavailable";

export interface StockSource {
  label: string;
  url: string;
  status: SourceStatus;
}

export interface StockCandidate {
  ticker: string;
  name: string;
  market: "KOSPI" | "KOSDAQ";
  sector: string;
  currentPrice: number;
  marketCap: number;
  per: number;
  pbr: number;
  peg: number;
  roe: number;
  operatingMargin: number;
  netMargin: number;
  assetTurnover: number;
  leverage: number;
  revenueCagr3y: number;
  profitCagr3y: number;
  yearlyRevenueGrowth: [number, number, number];
  operatingMarginTrend: [number, number, number];
  twoYearLoss: boolean;
  auditOpinion: "적정" | "비적정" | "확인불가";
  fairValueByPer: number;
  fairValueByPbr: number;
  fairValueByDcf: number;
  growthDrivers: string[];
  competitiveAdvantages: string[];
  riskFactors: string[];
  monitoringPoints: string[];
  moat: {
    brand: MoatLevel;
    switchingCost: MoatLevel;
    networkEffect: MoatLevel;
    costAdvantage: MoatLevel;
    intangibleAssets: MoatLevel;
  };
  sources: StockSource[];
}

export interface ScreeningFilters {
  maxPeg: number;
  minRevenueCagr: number;
  minRoe: number;
  minYearlyRevenueGrowth: number;
  minMarketCap: number;
}

export interface PortfolioSettings {
  investmentAmount: number;
  periodMonths: number;
  targetReturn: number;
  riskPolicy: "고위험 제외" | "중립";
}

export interface RankedCandidate extends StockCandidate {
  rank: number;
  score: number;
  allocationPercent: number;
  allocationAmount: number;
  weightedFairValue: number;
  marginBuyPrice: number;
  targetPrice: number;
  stopLossPrice: number;
  screeningPassed: boolean;
  failReasons: string[];
}

export interface AuthStatus {
  authRequired: boolean;
  oauthConfigured: boolean;
  oauthProvider: string;
  authenticated: boolean;
  user: {
    email: string;
    name: string;
    picture?: string;
    provider: string;
  } | null;
  aiConfigured: boolean;
  openaiModel: string;
  aiCredentialMode: "server-side-bearer" | "missing" | string;
  webSearchEnabled: boolean;
  docs?: Record<string, string>;
}

export interface AiProviderRegistration {
  id: "gpt" | "claude" | "gemini" | "perplexity" | string;
  label: string;
  shortLabel: string;
  modelFamily: string;
  configured: boolean;
  registered: boolean;
  account: string | null;
  registeredAt: string | null;
  callbackUrl: string;
  docsUrl: string;
  authNote: string;
  requiredEnv: string[];
}

export interface AiResearchRecommendation {
  rank: number;
  ticker: string;
  name: string;
  coreInvestmentPoint: string;
  allocationPercent: number;
  currentPriceKrw: string;
  buyPriceKrw: string;
  targetPriceKrw: string;
  stopLossPriceKrw: string;
  marketCapKrw: string;
  metrics: {
    per: string;
    pbr: string;
    peg: string;
    roe: string;
    operatingMargin: string;
    netMargin: string;
    revenueCagr3y: string;
    profitCagr3y: string;
  };
  dupont: {
    netMargin: string;
    assetTurnover: string;
    leverage: string;
    roe: string;
    qualityComment: string;
  };
  valuation: {
    perFairValue: string;
    pbrFairValue: string;
    dcfFairValue: string;
    weightedFairValue: string;
    marginBuyPrice: string;
    marginOfSafety: string;
    methodNote: string;
  };
  moat: {
    brand: string;
    switchingCost: string;
    networkEffect: string;
    costAdvantage: string;
    intangibleAssets: string;
    overall: string;
  };
  investmentPoints: string[];
  riskFactors: string[];
  tradePlan: string[];
  sourceUrls: string[];
  confidenceNote: string;
  dataQuality: string;
}

export interface AiResearchReport {
  summary: string;
  marketContext: string;
  recommendations: AiResearchRecommendation[];
  highestConviction: string;
  bestRiskReward: string;
  dataLimitations: string[];
  disclaimer: string;
}

export interface AiCitation {
  title: string;
  url: string;
}

export interface AiAnalyzeResponse {
  ok: boolean;
  model: string;
  responseId: string;
  createdAt: string;
  report: AiResearchReport | null;
  rawText: string;
  citations: AiCitation[];
  usage: unknown;
  sourceMode: "openai-web-search" | "model-only" | string;
}
