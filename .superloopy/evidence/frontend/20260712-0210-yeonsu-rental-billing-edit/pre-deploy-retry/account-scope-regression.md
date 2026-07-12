# 계정별 권한/범위 회귀 점검 리포트

- 실행시각: 2026-07-12 20:55:37
- 결과: **PASS**
- BaseUrl: https://trade.2884.kr

| 계정 | 결과 | 테넌트 | 지점 | 범위 | 거래처 수 | 품목 수 | 비고 |
| --- | --- | --- | --- | --- | ---: | ---: | --- |
| ITWORLD | OK | ITWORLD | ITWORLD | TenantAll | 357 | 2186 | OK |
| USENET | OK | USENET_GROUP | USENET | TenantAll | 143 | 2527 | OK |
| YEONSU | OK | USENET_GROUP | YEONSU | OfficeOnly | 40 | 3 | OK |

## ITWORLD

- 사용자: itworld
- 테넌트/지점: ITWORLD / ITWORLD
- 범위유형: TenantAll
- 거래처 수: 357
- 품목 수: 2186

| 영역 | 조회 가능 지점 | 쓰기 가능 지점 | 비고 |
| --- | --- | --- | --- |
| 기본 범위 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 거래처 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 품목/재고 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 판매/구매 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 수금/지급 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 계약서 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 집계/리포트 | ITWORLD | ITWORLD | 업체 전체 범위입니다. |
| 렌탈 | ITWORLD, USENET, YEONSU | ITWORLD | 렌탈 관리자 범위입니다. |
| 납품/배송 | ITWORLD | ITWORLD | 납품 전체 조회 권한으로 상위 범위를 사용합니다. |

## USENET

- 사용자: usenet
- 테넌트/지점: USENET_GROUP / USENET
- 범위유형: TenantAll
- 거래처 수: 143
- 품목 수: 2527

| 영역 | 조회 가능 지점 | 쓰기 가능 지점 | 비고 |
| --- | --- | --- | --- |
| 기본 범위 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 거래처 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 품목/재고 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 판매/구매 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 수금/지급 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 계약서 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 집계/리포트 | USENET, YEONSU | USENET, YEONSU | 업체 전체 범위입니다. |
| 렌탈 | ITWORLD, USENET, YEONSU | USENET, YEONSU | 렌탈 관리자 범위입니다. |
| 납품/배송 | USENET, YEONSU | USENET, YEONSU | 납품 전체 조회 권한으로 상위 범위를 사용합니다. |

## YEONSU

- 사용자: yeonsu
- 테넌트/지점: USENET_GROUP / YEONSU
- 범위유형: OfficeOnly
- 거래처 수: 40
- 품목 수: 3

| 영역 | 조회 가능 지점 | 쓰기 가능 지점 | 비고 |
| --- | --- | --- | --- |
| 기본 범위 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 거래처 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 품목/재고 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 판매/구매 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 수금/지급 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 계약서 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 집계/리포트 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 렌탈 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
| 납품/배송 | YEONSU | YEONSU | 현재 지점 기준 범위입니다. |
