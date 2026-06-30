import type { RankedCandidate, ScreeningFilters, PortfolioSettings } from "../types";

export function buildOpenAiAnalysisPrompt(
  candidates: RankedCandidate[],
  filters: ScreeningFilters,
  settings: PortfolioSettings,
): string {
  return `당신은 20년 경력의 CFA 자격보유 시니어 애널리스트입니다.

목표:
- 아래 후보 종목 데이터만 사용해 6개월 관점의 성장주 리서치 리포트를 작성합니다.
- 투자 권유, 수익 보장, 확정적 매수 지시는 금지합니다.
- 출처 없는 수치는 "확인불가"로 표시합니다.
- 실시간 원천 데이터가 연결되지 않은 값은 "확인불가" 또는 "데모 데이터"로 표시합니다.

입력 조건:
- 투자금액: ${settings.investmentAmount}원
- 투자기간: ${settings.periodMonths}개월
- 목표수익률 입력값: ${settings.targetReturn}%
- PEG 기준: ${filters.maxPeg} 이하
- 매출 CAGR 기준: ${filters.minRevenueCagr}% 이상
- ROE 기준: ${filters.minRoe}% 이상
- 최근 3년 매출성장률 기준: 매년 ${filters.minYearlyRevenueGrowth}% 이상
- 시가총액 기준: ${filters.minMarketCap}원 이상

분석 요구사항:
1. 경제적 해자 강/중/약 평가: 브랜드 파워, 전환비용, 네트워크 효과, 원가 우위, 무형자산.
2. 듀퐁분석: ROE = 순이익률 x 자산회전율 x 재무레버리지.
3. 밸류에이션: PER 40% + PBR 30% + DCF 30% = 가중평균 적정가, 안전마진 20% 적용 매수가.
4. 리스크와 손절가를 먼저 명시.
5. 모든 핵심 수치 옆에 sourceUrl 또는 "확인불가"를 유지.

후보 데이터 JSON:
${JSON.stringify(candidates, null, 2)}

출력 JSON 필드:
- summary
- recommendations[]: rank, ticker, name, allocationPercent, currentPrice, buyPrice, targetPrice, stopLossPrice, metrics, dupont, moat, investmentPoints, riskFactors, tradePlan, sourceUrls, confidenceNote
- highestConviction
- bestRiskReward
- dataLimitations
`;
}

export const structuredOutputSchema = {
  type: "object",
  additionalProperties: false,
  required: ["summary", "recommendations", "highestConviction", "bestRiskReward", "dataLimitations"],
  properties: {
    summary: { type: "string" },
    recommendations: {
      type: "array",
      items: {
        type: "object",
        additionalProperties: false,
        required: [
          "rank",
          "ticker",
          "name",
          "allocationPercent",
          "currentPrice",
          "buyPrice",
          "targetPrice",
          "stopLossPrice",
          "metrics",
          "dupont",
          "moat",
          "investmentPoints",
          "riskFactors",
          "tradePlan",
          "sourceUrls",
          "confidenceNote",
        ],
        properties: {
          rank: { type: "number" },
          ticker: { type: "string" },
          name: { type: "string" },
          allocationPercent: { type: "number" },
          currentPrice: { type: "number" },
          buyPrice: { type: "number" },
          targetPrice: { type: "number" },
          stopLossPrice: { type: "number" },
          metrics: { type: "object", additionalProperties: { type: ["string", "number"] } },
          dupont: {
            type: "object",
            additionalProperties: false,
            required: ["netMargin", "assetTurnover", "leverage", "roe"],
            properties: {
              netMargin: { type: "number" },
              assetTurnover: { type: "number" },
              leverage: { type: "number" },
              roe: { type: "number" },
            },
          },
          moat: { type: "object", additionalProperties: { type: "string" } },
          investmentPoints: { type: "array", items: { type: "string" } },
          riskFactors: { type: "array", items: { type: "string" } },
          tradePlan: { type: "array", items: { type: "string" } },
          sourceUrls: { type: "array", items: { type: "string" } },
          confidenceNote: { type: "string" },
        },
      },
    },
    highestConviction: { type: "string" },
    bestRiskReward: { type: "string" },
    dataLimitations: { type: "array", items: { type: "string" } },
  },
} as const;
