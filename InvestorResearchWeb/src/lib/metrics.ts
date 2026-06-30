import type { PortfolioSettings, RankedCandidate, ScreeningFilters, StockCandidate } from "../types";

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

export function formatWon(value: number): string {
  return new Intl.NumberFormat("ko-KR", { style: "currency", currency: "KRW", maximumFractionDigits: 0 }).format(value);
}

export function formatMarketCap(value: number): string {
  if (value >= 1_0000_0000_0000) {
    return `${(value / 1_0000_0000_0000).toFixed(2)}조`;
  }
  return `${Math.round(value / 100_000_000).toLocaleString("ko-KR")}억`;
}

export function formatPercent(value: number): string {
  return `${value.toFixed(1)}%`;
}

export function calculateWeightedFairValue(stock: StockCandidate): number {
  return Math.round(stock.fairValueByPer * 0.4 + stock.fairValueByPbr * 0.3 + stock.fairValueByDcf * 0.3);
}

export function getScreeningResult(stock: StockCandidate, filters: ScreeningFilters): { passed: boolean; reasons: string[] } {
  const reasons: string[] = [];
  if (stock.peg > filters.maxPeg) reasons.push(`PEG ${stock.peg.toFixed(2)} > ${filters.maxPeg}`);
  if (stock.revenueCagr3y < filters.minRevenueCagr) reasons.push(`매출 CAGR ${stock.revenueCagr3y.toFixed(1)}% < ${filters.minRevenueCagr}%`);
  if (stock.roe < filters.minRoe) reasons.push(`ROE ${stock.roe.toFixed(1)}% < ${filters.minRoe}%`);
  if (stock.yearlyRevenueGrowth.some((growth) => growth < filters.minYearlyRevenueGrowth)) {
    reasons.push(`최근 3년 중 매출성장 ${filters.minYearlyRevenueGrowth}% 미만 연도 존재`);
  }
  if (stock.operatingMarginTrend[2] <= stock.operatingMarginTrend[0]) reasons.push("영업이익률 개선 미충족");
  if (stock.marketCap < filters.minMarketCap) reasons.push("시가총액 1,000억 미만");
  if (stock.twoYearLoss) reasons.push("최근 2년 연속 적자");
  if (stock.auditOpinion !== "적정") reasons.push(`감사의견 ${stock.auditOpinion}`);
  return { passed: reasons.length === 0, reasons };
}

function scoreStock(stock: StockCandidate): number {
  const growthScore = clamp((stock.revenueCagr3y - 15) * 2.2, 0, 35);
  const qualityScore = clamp((stock.roe - 15) * 2.4 + (stock.operatingMargin - 10) * 1.1, 0, 35);
  const valuationScore = clamp((1.5 - stock.peg) * 20 + (calculateWeightedFairValue(stock) / stock.currentPrice - 1) * 25, 0, 25);
  const momentumScore = stock.operatingMarginTrend[2] > stock.operatingMarginTrend[1] ? 5 : 0;
  return Math.round((growthScore + qualityScore + valuationScore + momentumScore) * 10) / 10;
}

export function rankCandidates(
  stocks: StockCandidate[],
  filters: ScreeningFilters,
  settings: PortfolioSettings,
): RankedCandidate[] {
  const analyzed = stocks.map((stock) => {
    const screening = getScreeningResult(stock, filters);
    const weightedFairValue = calculateWeightedFairValue(stock);
    const marginBuyPrice = Math.round(weightedFairValue * 0.8);
    const targetPrice = Math.round(marginBuyPrice * (1 + settings.targetReturn / 100 + 0.1));
    const stopLossPrice = Math.round(marginBuyPrice * 0.9);
    return {
      ...stock,
      rank: 0,
      score: screening.passed ? scoreStock(stock) : scoreStock(stock) * 0.35,
      allocationPercent: 0,
      allocationAmount: 0,
      weightedFairValue,
      marginBuyPrice,
      targetPrice,
      stopLossPrice,
      screeningPassed: screening.passed,
      failReasons: screening.reasons,
    } satisfies RankedCandidate;
  });

  const passed = analyzed
    .filter((stock) => stock.screeningPassed)
    .sort((a, b) => b.score - a.score)
    .slice(0, 5);

  const scoreTotal = passed.reduce((sum, stock) => sum + stock.score, 0);
  let allocated = 0;
  return passed.map((stock, index) => {
    const rawPercent = scoreTotal === 0 ? 100 / passed.length : (stock.score / scoreTotal) * 100;
    const allocationPercent = index === passed.length - 1 ? Math.round((100 - allocated) * 10) / 10 : Math.round(rawPercent * 10) / 10;
    allocated += allocationPercent;
    return {
      ...stock,
      rank: index + 1,
      allocationPercent,
      allocationAmount: Math.round(settings.investmentAmount * (allocationPercent / 100)),
    };
  });
}

export function buildReportMarkdown(stocks: RankedCandidate[], settings: PortfolioSettings): string {
  const header = [
    "# 투자 리서치 플랜 MVP 리포트",
    "",
    `- 투자금액: ${formatWon(settings.investmentAmount)}`,
    `- 투자기간: ${settings.periodMonths}개월`,
    `- 목표수익률 입력값: ${settings.targetReturn}%`,
    "- 상태: 데모 데이터 기반 UI 검증용 산출물",
    "- 고지: 투자 권유가 아닌 리서치 보조 자료이며, 실거래 전 원천 데이터 검증이 필요합니다.",
    "",
  ];

  const body = stocks.flatMap((stock) => [
    `## ${stock.rank}순위: ${stock.name} (${stock.ticker})`,
    "",
    `배분비율 | 현재가 | 매수가 | 목표가 | 손절가 | 시총 | PER | PBR | PEG | ROE | 영업이익률 | 순이익률 | 매출CAGR[3년] | 이익CAGR[3년]`,
    `--- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | ---`,
    `${stock.allocationPercent}% | ${formatWon(stock.currentPrice)} | ${formatWon(stock.marginBuyPrice)} | ${formatWon(stock.targetPrice)} | ${formatWon(stock.stopLossPrice)} | ${formatMarketCap(stock.marketCap)} | ${stock.per.toFixed(1)} | ${stock.pbr.toFixed(1)} | ${stock.peg.toFixed(2)} | ${formatPercent(stock.roe)} | ${formatPercent(stock.operatingMargin)} | ${formatPercent(stock.netMargin)} | ${formatPercent(stock.revenueCagr3y)} | ${formatPercent(stock.profitCagr3y)}`,
    "",
    "투자 포인트:",
    ...stock.growthDrivers.map((item, i) => `${i + 1}. ${item}`),
    "",
    "리스크 요인:",
    ...stock.riskFactors.map((item, i) => `${i + 1}. ${item}`),
    "",
    "매매 전략:",
    `1. 적정가: ${formatWon(stock.weightedFairValue)} = PER 40% + PBR 30% + DCF 30%`,
    `2. 목표가: ${formatWon(stock.targetPrice)}`,
    `3. 1차 매수: ${formatWon(stock.marginBuyPrice)} / 배분금액의 60%`,
    `4. 2차 매수: ${formatWon(Math.round(stock.marginBuyPrice * 0.96))} / 배분금액의 40%`,
    `5. 손절가: ${formatWon(stock.stopLossPrice)} / 손실 제한 기준`,
    "",
    `출처: ${stock.sources.map((source) => source.url).join(", ")}`,
    "",
  ]);

  return [...header, ...body].join("\n");
}
