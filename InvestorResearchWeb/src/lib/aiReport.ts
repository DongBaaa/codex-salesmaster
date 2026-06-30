import type { AiCitation, AiResearchReport } from "../types";

export function buildAiReportMarkdown(report: AiResearchReport, citations: AiCitation[] = []): string {
  const lines = [
    "# AI 실시간 투자 리서치 보고서",
    "",
    "## 요약",
    report.summary,
    "",
    "## 시장 맥락",
    report.marketContext,
    "",
    "## 추천 후보",
  ];

  for (const item of report.recommendations) {
    lines.push(
      "",
      `### ${item.rank}순위: ${item.name} (${item.ticker}) - ${item.coreInvestmentPoint}`,
      "",
      `배분비율 | 현재가 | 매수가 | 목표가 | 손절가 | 시총 | PER | PBR | PEG | ROE | 영업이익률 | 순이익률 | 매출CAGR[3년] | 이익CAGR[3년]`,
      `--- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | ---`,
      `${item.allocationPercent}% | ${item.currentPriceKrw} | ${item.buyPriceKrw} | ${item.targetPriceKrw} | ${item.stopLossPriceKrw} | ${item.marketCapKrw} | ${item.metrics.per} | ${item.metrics.pbr} | ${item.metrics.peg} | ${item.metrics.roe} | ${item.metrics.operatingMargin} | ${item.metrics.netMargin} | ${item.metrics.revenueCagr3y} | ${item.metrics.profitCagr3y}`,
      "",
      "투자 포인트:",
      ...item.investmentPoints.map((point, index) => `${index + 1}. ${point}`),
      "",
      "경제적 해자:",
      `- 브랜드: ${item.moat.brand}`,
      `- 전환비용: ${item.moat.switchingCost}`,
      `- 네트워크 효과: ${item.moat.networkEffect}`,
      `- 원가 우위: ${item.moat.costAdvantage}`,
      `- 무형자산: ${item.moat.intangibleAssets}`,
      `- 종합: ${item.moat.overall}`,
      "",
      "듀퐁 ROE:",
      `- 순이익률: ${item.dupont.netMargin}`,
      `- 자산회전율: ${item.dupont.assetTurnover}`,
      `- 레버리지: ${item.dupont.leverage}`,
      `- ROE: ${item.dupont.roe}`,
      `- 품질 판단: ${item.dupont.qualityComment}`,
      "",
      "밸류에이션:",
      `- PER 적정가: ${item.valuation.perFairValue}`,
      `- PBR 적정가: ${item.valuation.pbrFairValue}`,
      `- DCF 적정가: ${item.valuation.dcfFairValue}`,
      `- 가중평균 적정가: ${item.valuation.weightedFairValue}`,
      `- 안전마진 매수가: ${item.valuation.marginBuyPrice}`,
      `- 안전마진: ${item.valuation.marginOfSafety}`,
      `- 산출근거: ${item.valuation.methodNote}`,
      "",
      "리스크 요인:",
      ...item.riskFactors.map((risk, index) => `${index + 1}. ${risk}`),
      "",
      "매매 전략:",
      ...item.tradePlan.map((plan, index) => `${index + 1}. ${plan}`),
      "",
      "출처 URL:",
      ...item.sourceUrls.map((url) => `- ${url}`),
      "",
      `신뢰도 메모: ${item.confidenceNote}`,
      `데이터 품질: ${item.dataQuality}`,
    );
  }

  lines.push(
    "",
    "## 최종 추천 종목",
    `- 가장 확신 높은 종목: ${report.highestConviction}`,
    `- 리스크 대비 수익 최고: ${report.bestRiskReward}`,
    "",
    "## 데이터 한계",
    ...report.dataLimitations.map((item) => `- ${item}`),
    "",
    "## 고지",
    report.disclaimer,
  );

  if (citations.length > 0) {
    lines.push("", "## OpenAI 웹검색 인용 URL", ...citations.map((citation) => `- ${citation.title || "출처"}: ${citation.url}`));
  }

  return lines.join("\n");
}
