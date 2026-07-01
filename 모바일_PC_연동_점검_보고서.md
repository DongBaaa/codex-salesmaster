# 모바일-PC 연동 점검 보고서

## 1. 검사 요약

- 검사일: 2026-07-01
- 기준 작업트리: `D:\새 폴더\georaeplan-main-fix`
- 목적: 모바일 버전이 PC 버전의 핵심 데이터(거래처, 품목, 전표, 수금, 재고, 렌탈 조회, 휴지통, 동기화, 권한 범위)와 같은 서버/동기화 계약으로 연결되는지 확인하고, 사용자가 체감하는 반복 로딩 병목을 줄이는 것.
- 운영 데이터 변경: 없음. 모든 확인은 코드 경로, 로컬 SQLite 테스트 DB, 빌드/테스트 기준으로 수행.
- 결론:
  - 모바일은 PC의 모든 기능을 100% 동일하게 제공하는 앱이 아니라, 현장 조회/간이 작성/동기화/오프라인 보완 중심 앱이다.
  - 모바일에서 제공 중인 기능은 PC와 같은 공용 계약(`Shared.Contracts`)과 API/동기화 payload를 사용하며, 테스트 기준으로 거래처/품목/전표/수금/렌탈 조회/휴지통/권한/오프라인 dirty push가 연동된다.
  - Android 실기기 또는 에뮬레이터가 연결되어 있지 않아 화면 터치 E2E는 이번 실행에서 제한되었지만, Android 환경 점검과 Debug 빌드는 통과했다.
  - PC 로딩 병목 중 메인 대시보드/즐겨찾기 로딩의 전표 전체 라인 재조회 문제를 수정했다.

## 2. PC-모바일 기능 연동 매트릭스

| 모듈 | PC 기준 기능 | 모바일 제공/연동 상태 | 확인 방법 | 판정 |
|---|---|---|---|---|
| 로그인/세션/권한 | 계정, 담당지점, 권한, 세션 만료 | 모바일 API 인증/401 세션 회복, 권한별 버튼/쓰기 제한 | `MobileReleaseConfigurationTests`, `SyncScopePendingMessageTests` | 통과 |
| 거래처 | 조회, 수정, 삭제, 담당지점 범위 | 거래처 조회/상세/수정, 캐시 fallback, 삭제 반영 | 모바일 거래처/수정/삭제 관련 테스트 | 통과 |
| 품목/재고 | 품목, 재고수량, 비재고 품목 구분 | 품목 조회/수정, 비재고 품목 재고값 노출 방지, 창고재고 캐시 | `MobileNonInventoryItems`, `AndroidItems`, `InvoiceItemSelection` | 통과 |
| 전표 | 판매/매입/수금/지급, 수정충돌, 첨부 | 모바일 전표 작성/수금 초안, stale revision 차단, 첨부 업로드 실패 보존 | `AndroidWriteE2E`, `AndroidInvoiceDraft`, `AndroidPaymentDraft` | 통과 |
| 수금/지급 | 전표 연결 수금, 미수 계산 | 모바일 수금 초안/저장, 미수금 계산, 실패 시 dirty 큐 유지 | `MobileImmediatePayment`, `AndroidPaymentE2E` | 통과 |
| 렌탈 자산/청구 | PC는 렌탈 자산 편집/청구서 생성/입금등록까지 제공 | 모바일은 고객 상세의 렌탈 프로필/자산/설치이력 조회와 읽기 전용 렌탈/재고 화면 중심 | `AndroidCustomerDetail`, `MobileRentalAssignments`, read-only guard | 부분 제공(의도된 PC 전용 업무 존재) |
| 재고 이동 | PC 재고 이동/입출고 | 모바일은 재고/렌탈 화면의 읽기 전용 보호 및 일부 동기화 데이터 사용 | `InventoryTransferScopeGuard`, `MobileInventoryTransferAndRentalScreens` | 통과 |
| 휴지통/운영점검 | 삭제 복구/정리, 무결성 점검 | 모바일 휴지통 필터/복원·purge 반영, 무결성 리포트 읽기 | `RecycleBinScopeAndSync`, `RecycleBinPurgeRecords`, `AndroidSettings` | 통과 |
| 동기화/오프라인 | dirty push/pull, 충돌, 권한 거절 | 모바일 pending push 재필터, 충돌 메시지, 서버 거절 시 dirty 보존 | `MobileSync*`, `AndroidSync*`, `SyncOutboxPendingStateTests` | 통과 |
| 업데이트 | PC/Android manifest | PC manifest 1.1.644, Android manifest 0.2.79 보존 | update manifest 생성 결과 | 통과 |

## 3. 이번에 발견·수정한 로딩 병목

### 병목 1: 메인 대시보드가 전표 목록 전체와 전표 라인까지 반복 로딩

- 증상: 메인 화면 로딩/새로고침 때 대시보드 카드 계산을 위해 전표 요약 전체를 다시 가져오고, 그 과정에서 전표 라인/수금 요약까지 함께 조회했다.
- 원인: `MainViewModel.RefreshDashboardMetricsAsync`가 필터 결과가 없을 때 `GetInvoiceListSummariesAsync(from:null...)`를 호출해 목록용 무거운 데이터를 대시보드 계산에도 재사용했다.
- 수정:
  - `LocalStateService.GetInvoiceDashboardMetricsAsync` 추가: 전표 ID/일자/유형/금액과 수금 합계만 사용해 대시보드 지표를 계산한다.
  - SQLite decimal 집계 제약을 피하기 위해 결제 행은 필요한 범위만 가져온 뒤 메모리에서 합산한다.
  - 안전재고 알림은 `GetItemsAsync` 전체 로딩 대신 `CountSafetyStockAlertsAsync`로 DB count만 수행한다.

### 병목 2: 즐겨찾기 전표가 없거나 일부만 있어도 전체 전표를 다시 조회

- 증상: 메인 목록 로딩 뒤 즐겨찾기 빠른 접근 목록을 만들 때 전체 전표 목록을 다시 가져오는 경로가 있었다.
- 원인: `LoadInvoiceFavoritesAsync`가 즐겨찾기 ID만 필요한 경우에도 전체 전표 요약 조회를 fallback으로 사용했다.
- 수정:
  - 즐겨찾기 ID가 없으면 즉시 반환한다.
  - 즐겨찾기 ID가 있을 때는 `GetInvoiceListSummariesByIdsAsync`로 필요한 전표만 조회한다.

## 4. 검증 결과

| 검증 항목 | 명령/증거 | 결과 |
|---|---|---|
| 신규 성능 회귀테스트 | `dotnet test ... --filter "FullyQualifiedName~MainLoadingPerformanceTests"` | 통과 4개 |
| 모바일/렌탈/권한/동기화 확대 테스트 | `.git` worktree 보정 후 `dotnet test ... --filter "MainLoadingPerformanceTests|RentalDashboardSummaryPerformanceTests|RentalBillingBatchLookupTests|RentalBillingSearchLimitTests|RentalAssetSearchLimitTests|RentalSearchIndexTests|Mobile|SyncScopePendingMessage|RecycleBinScopeAndSync|BusinessDatabaseScopeGuard|InventoryTransferScopeGuard"` | 통과 176개 |
| 전체 데스크톱 테스트 | `.git` worktree 보정 후 `dotnet test Tests\GeoraePlan.Desktop.App.Tests\GeoraePlan.Desktop.App.Tests.csproj` | 통과 825개 |
| 버전/릴리스 관련 테스트 | `ReleaseTempPathGuardTests|MobileReleaseConfigurationTests|MainLoadingPerformanceTests` | 통과 133개 |
| Android 환경 | `tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1` | `android_environment_ready=true` |
| Android 빌드 | bundled dotnet `build Mobile\GeoraePlan.Mobile.App -f net8.0-android -c Debug` | 통과, 경고 0개/오류 0개 |
| Android 기기 연결 | `adb devices` | 연결 기기 없음. 실기기 UI 조작 검증은 미수행 |
| PC 업데이트 manifest | `배포\업데이트\manifest\stable.json` | desktop 1.1.644, android 0.2.79 보존 |

## 5. 남은 위험 및 권장 작업

1. Android 실기기/에뮬레이터 UI E2E는 연결 기기가 없어 실행하지 못했다. 다음 배포 전 실제 단말에서 품목탭, 거래처탭→전표작성, 동기화 탭을 한 번 더 확인하는 것이 좋다.
2. 모바일은 PC의 렌탈 청구서 생성/입금등록/프로필 편집을 동일하게 제공하지 않는다. 사용자가 모바일에서도 렌탈 청구 업무를 해야 한다면 별도 구현 범위로 잡아야 한다.
3. 렌탈 청구관리 로딩은 이미 검색 제한/배치 로딩/비동기 로딩이 적용되어 있고 이번 패치는 메인 반복 로딩 병목을 우선 제거했다. 실제 운영 PC에서 여전히 느린 특정 검색어/거래처가 있으면 해당 조건의 OperationTiming 로그로 추가 최적화한다.
4. 운영 데이터 자체 정정은 이번 범위에서 제외했다.
