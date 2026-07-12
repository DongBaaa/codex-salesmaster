# 거래플랜 운영 검증 게이트 리포트

- 실행시각: 2026-07-12 23:21:40 +09:00
- 결과: **PASS**
- ProjectRoot: `D:\거래플랜`
- BaseUrl: `https://trade.2884.kr`
- Channel: `stable`
- OutputDirectory: `D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy`
- 무결성 Warning 실패 처리: `True`
- 운영 Warning 실패 처리: `True`
- Android legacy debug signing 경고 수용: `True`
- 로컬 캐시 필수 점검: `False`
- 로컬 캐시 Warning 실패 처리: `False`
- 쓰기 안전성 점검 생략: `True`

## 1. 체크 결과

| 결과 | 항목 | 상세 |
| --- | --- | --- |
| PASS | live healthz | 200 OK, 115ms |
| PASS | live readyz | 200 OK, 36ms, status=ready, databaseInitialization.started=true/completed=true/failed=false; attempts=1 |
| PASS | stable manifest | desktop=1.1.676, android=0.2.81 |
| PASS | update package downloads | desktop=HEAD 200/GET 200/size 232214537, android=HEAD 200/GET 200/size 41424314; D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\update-downloads.md |
| PASS | live observation | D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\live-observation.md |
| PASS | platform status files | SKIP: Linux PC platform state root is not configured; live health/manifest checks are used instead |
| PASS | integrity report | Error=0, Warning=0; Info=2; D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\integrity-report-summary.md |
| PASS | integrity report by account | accessible=3; no errors/warnings; D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\integrity-scope-summary.md |
| PASS | rental monthly amount consistency | candidate_count=0; report=D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\rental-monthly-repair\rental-monthly-repair-20260712-232119.md |
| PASS | account scope regression | D:\거래플랜\.superloopy\evidence\frontend\20260712-2310-rental-billing-cycle-ux\pre-deploy\account-scope-regression.md |
| PASS | approved target file | SKIP: read-only gate mode |
| PASS | write safety metadata | SKIP: read-only gate mode |
| PASS | operational writes | SKIP: read-only gate mode |

## 2. 계정 입력 상태

- SecretPath 존재: `True`

| 계정 | 사용자명 | 비밀번호 |
| --- | --- | --- |
| ADMIN | a***n | present |
| ITWORLD | i***d | present |
| USENET | u***t | present |
| YEONSU | y***u | present |

## 3. 승인 대상 상태

- ApprovedTargetsPath 존재: `False`
- 읽기 전용 게이트 모드로 승인 대상 JSON/운영 쓰기 검증을 생략함

## 4. 운영 데이터 변경 여부

- 이 게이트는 읽기 전용 모드로 실행되었으며 운영 데이터를 변경하지 않았다.
- 읽기 전용 게이트 모드로 운영 데이터 쓰기/원복 검증을 생략했다.
- 비밀번호 원문은 보고서와 로그에 기록하지 않는다.
