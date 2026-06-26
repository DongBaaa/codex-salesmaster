# Phase 2 TODO

- 문서 기준시점: 2026-06-03
- 이 파일은 현재 코드 기준으로 남은 Phase 2 과제를 관리합니다.
- `[x]` 는 반영 완료, `[ ]` 는 미완료/후속 작업입니다.

---

## 1. 인쇄 템플릿 에디터 (PrintTemplate)

- [ ] `PrintTemplate` + `PrintTemplateVersion` 엔티티 활용 (이미 DB 스키마 존재)
- [ ] 드래그 앤 드롭 레이아웃 에디터 (WPF Canvas)
- [ ] JSON 직렬화로 템플릿 저장/복원
- [ ] WPF/QuestPDF 기반 인쇄 편집 고도화 검토

---

## 2. 안드로이드 앱

- [x] .NET MAUI Android 클라이언트 기본 앱 구성
- [x] 동일한 `/sync/pull`, `/sync/push` API 재사용
- [x] JSON 기반 모바일 동기화 상태 / 대기 업로드 캐시
- [x] 오프라인 SQLite 고도화 (`JsonSyncStateStore` 명칭 유지, SQLite 주 저장소 + scoped JSON 백업/마이그레이션)
- [ ] 터치 최적화 전표 입력 UI 추가 고도화
- [ ] 실기기 운영 E2E 회귀 테스트 시나리오 정례화
  - [x] Android 에뮬레이터 로컬 테스트 서버 기본/확장 smoke PASS(로그인, 홈, 렌탈 조회, 판매/구매/수금·지급 초안, 거래처, 품목, 전표, 동기화, 권장 동기화 실행)
  - [x] Android 에뮬레이터 로컬 테스트 서버 저장 E2E PASS(판매 저장, 매입 저장, 판매 저장 거부 422, 판매 dirty 동기화, 수금 저장, 지급 저장, 수금 저장 거부 422)
  - [x] Android 에뮬레이터 로컬 테스트 서버 권한 거부 E2E PASS(판매전표 403, 수금 403, 서버 미생성/dirty 0건)
  - [ ] 실기기/운영망 기준 장시간 실행·재로그인·권한 거부 E2E 증거 누적

---

## 3. 원격 접속 터널

- [ ] ngrok / Cloudflare Tunnel 통합
- [x] 모바일 설정 화면에서 터널 URL 입력/저장/운영 서버 초기화
- [x] Android 런타임 `Api.BaseUrl`을 저장된 터널 URL 우선으로 전환

---

## 4. 고급 리포트

- [x] 월별 매출 차트(메인 대시보드 최근 6개월 매출 막대 차트 표시)
- [x] 거래처별 잔액 현황(기간별 집계 화면 미수잔액 요약 표시)
- [x] 기간별 수금율 대시보드(기간별 집계 화면 수금율 요약 표시)

---

## 5. 다중 사용자 / 권한 / 동기화

- [x] 환경설정의 사용자 관리 UI 반영
- [x] 관리자 전용 UI 기본 비활성화/숨김 반영 (사용자 관리, 재고이동, 지점 제한 등)
- [ ] 지점/권한 정책을 전표 저장/조회/수정 전 구간으로 확장
  - [x] PC/서버 핵심 범위 회귀: 전표 저장 scope, 로컬 운영 scope, 공유 dirty 권한, 마스터 CRUD scope, 휴지통 scope, office scope/paging, sync push coverage
  - [x] Android 에뮬레이터 기준 조회/저장/동기화/권한 거부 E2E
    - [x] 에뮬레이터 로컬 테스트 서버 조회/초안/동기화 smoke PASS(mobile-smoke-20260626-205314, mobile-smoke-20260626-205501)
    - [x] 에뮬레이터 로컬 테스트 서버 저장 성공/저장 거부/dirty push E2E PASS(mobile-write-e2e-20260626-210155, mobile-write-e2e-20260626-210415, mobile-write-e2e-20260626-210634, mobile-write-e2e-20260626-210854, mobile-payment-e2e-sales-20260626-211141, mobile-payment-e2e-purchase-20260626-211401, mobile-payment-e2e-sales-20260626-211621)
    - [x] 에뮬레이터 로컬 테스트 서버 권한 거부 403 E2E PASS(mobile-write-e2e-20260626-212730, mobile-payment-e2e-sales-20260626-212953)
    - [x] 2026-06-27 에뮬레이터 로컬 테스트 서버 재검증 PASS: smoke+권장 동기화(`mobile-smoke-20260627-020200`), 판매/구매 전표 저장(`mobile-write-e2e-20260627-020402`, `mobile-write-e2e-20260627-020608`), 판매/구매 수금·지급(`mobile-payment-e2e-sales-20260627-020813`, `mobile-payment-e2e-purchase-20260627-021011`), 판매 dirty push(`mobile-write-e2e-20260627-021209`), 403 권한 거부/dirty 0건(`mobile-write-e2e-20260627-021427`), 수금 stale revision 충돌(`mobile-payment-e2e-sales-20260627-021638`).
    - [ ] 운영망/실기기 기준 장시간 실행·재로그인·권한 거부 E2E 증거 누적
  - [x] 전체 업무 화면별 수동 QA 체크리스트 정례화
  - [ ] 전체 업무 화면별 실제 수동 QA 증거 누적
- [x] 세션 만료 자동 재로그인/갱신 및 401 후 재시도
- [x] 로컬 확장 마스터(담당지점/창고/선택값)와 재고이동의 서버 동기화 범위 확대
  - [x] 선택값 저장/삭제 동시성 및 무결성 회귀
  - [x] 창고별 재고 pull, 서버 누락 행 정리, dirty 보존 회귀
  - [x] 재고이동 생성/확정/삭제/복구/영구삭제 권한·scope 회귀
  - [x] 서버 재고이동 push scope 및 창고별 재고 반영 회귀
  - [ ] 실기기/운영 화면 기준 담당지점·창고 선택 제한 수동 QA 증거 정례화

---

## 6. 재고 관리

- [x] 내부 재고이동 기본 흐름
- [ ] `LocalItem`의 IsRental 품목 재고 추적 고도화
  - [x] 서버 직접 저장/sync push/시작 보정에서 렌탈·장비 품목의 현재고·창고재고 잔여값 차단
  - [x] Android 품목/전표 화면과 pending 저장 경로에서 비재고·자산 품목 재고값 표시/저장 차단
  - [x] PC 동기화 pull/push 및 수동 재고 조정 경로에서 비재고·자산 품목의 창고재고 row/현재고/안전재고 잔여값 차단
  - [ ] PC/Android 운영 로컬 캐시 잔여 row 실데이터 정리 증거 누적
- [ ] 임대 시작/종료 달력 뷰
- [x] 재고 부족 알림(메인 대시보드 안전재고 카드 표시)
- [ ] FIFO/원가배분/시리얼 운영 UI 고도화

---

## 7. 연수구 / 지점 운영

- [x] 연수구 납품내역 화면
- [x] 집계 화면의 `연수구 납품내역` 유형 추가
- [ ] 연수구 실데이터 기준 정합성 검증
- [ ] 지점별 운영 예외(담당 거래처/출고창고 선택 제한) 추가 보정
  - [x] PC/서버 담당 거래처 scope, 고객 마스터 scope, 전표 저장 scope 회귀
  - [x] PC/서버 재고이동 출고/입고 창고 scope 및 target/source office 권한 회귀
  - [x] 운영 화면 수동 QA 체크리스트: USENET/YEONSU/ITWORLD 계정별 거래처 선택, 전표 출고창고, 재고이동 창고 선택 제한 항목 정례화
  - [ ] 운영 화면 실제 수동 QA 증거: USENET/YEONSU/ITWORLD 계정별 거래처 선택, 전표 출고창고, 재고이동 창고 선택 제한 결과

---

## 8. 전자세금계산서 연동

- [ ] 홈택스 API 연동 (전자세금계산서 발행)
- [ ] 세금계산서 번호 자동 채번

---

*참고: 기존 문서의 `사용자 관리 UI (현재 API만 존재)` 항목은 현재 코드 상태와 맞지 않아 완료 항목으로 정정함.*

### 2026-06-26 Android 오프라인 SQLite 고도화 완료 증거
- [x] 모바일 동기화 상태 저장소를 `files/sync-states/mobile-sync-state.db` SQLite DB로 전환했습니다.
- [x] 기존 scoped/legacy JSON 상태는 현재 계정 소유자 정보가 맞을 때 SQLite로 자동 이전하고, 계정 없는 미전송 초안은 기존 정책대로 격리합니다.
- [x] SQLite 저장 성공 후에도 동일 scoped JSON 백업을 남겨 DB 파일 손상/오픈 실패 시 JSON fallback이 가능하도록 보강했습니다.
- [x] Android 에뮬레이터 로컬 테스트 서버 기준 smoke PASS: `D:\거래플랜\테스트 시행\기록\mobile-smoke-20260626-220042.md`.
- [x] Android 저장/dirty E2E PASS: `D:\거래플랜\테스트 시행\기록\mobile-write-e2e-20260626-215827.md`.
- [x] 앱 내부 저장소 증거: `D:\거래플랜\테스트 시행\기록\mobile-sqlite-json-backup-evidence-20260626-220259.md`.
- [x] Android 0.2.69 signed APK 및 stable 업데이트 manifest 생성/배포 완료.

### 2026-06-26 Android BaseUrl 설정 저장/적용 보강 완료 증거
- [x] Android `SettingsService.SaveBaseUrlAsync`가 더 이상 no-op이 아니며, 기본 운영 서버와 다른 URL은 `Preferences`에 저장하고 기본값과 같으면 저장값을 제거합니다.
- [x] `SettingsService.GetBaseUrl`은 저장된 URL을 우선 사용하고 값이 없으면 `ApiOptions.DefaultBaseUrl`로 돌아갑니다.
- [x] `ApiClient`는 기존처럼 `SettingsService.GetBaseUrl()`을 통해 실제 요청 base URL을 결정하므로 저장값 반영 경로가 끊기지 않습니다.
- [x] 0.2.70에서는 Settings 화면의 BaseUrl 입력 UI를 숨긴 상태로 저장 로직만 보강했고, 0.2.71에서 별도 고급 연결 설정 UI를 추가했습니다.
- [x] 검증: Android 설정 저장 source guard 87/87 PASS, Android Debug build PASS, Android 에뮬레이터 smoke PASS(`mobile-smoke-20260626-222120.md`), 전체 데스크톱 테스트 747/747 PASS.
- [x] Android 0.2.70 / version code 181 signed APK, stable 업데이트 manifest, live 배포 완료(`20260626-android-baseurl-settings-v070`).
- [x] 모바일 고급 연결 설정 UI 노출은 0.2.71에서 완료했습니다. ngrok/Cloudflare Tunnel 자체 발급/연결 자동화는 별도 인프라 작업으로 유지합니다.

### 2026-06-26 Android 고급 연결 설정 UI 완료 증거
- [x] Settings 화면에 “고급 연결 설정” 버튼을 추가하고, 토글 후에만 BaseUrl 입력/저장/운영 서버 초기화 UI가 표시되도록 했습니다.
- [x] URL 저장 전 `http://`/`https://` 절대 URL과 host를 검증하고, 잘못된 저장값은 제거 후 기본 운영 서버로 fallback합니다.
- [x] Android 에뮬레이터 UI-tree 기준으로 설정 화면 진입, 고급 연결 설정 토글, URL 입력칸, 연결 URL 저장, 운영 서버 초기화 버튼과 초기화 완료 메시지를 확인했습니다.
- [x] 검증: Android 설정 source guard 87/87 PASS, Android Debug build PASS, Android smoke PASS(`mobile-smoke-20260626-224340.md`), 전체 데스크톱 테스트 747/747 PASS.
- [x] Android 0.2.71 / version code 182 signed APK, stable 업데이트 manifest, live 배포 완료(`20260626-android-advanced-baseurl-v071`).
- [x] Android 0.2.72에서 고급 연결 URL 저장 전 `/healthz` 연결 테스트 버튼과 성공/실패 메시지를 추가했습니다.
- [ ] ngrok/Cloudflare Tunnel 자체 발급/연결 자동화는 별도 인프라 작업으로 유지합니다.


### 2026-06-26 Android 고급 연결 테스트 완료 증거
- [x] Settings 고급 연결 설정에 “연결 테스트” 버튼을 추가했습니다.
- [x] 입력한 BaseUrl을 정규화한 뒤 `/healthz`를 8초 timeout으로 확인하고, 성공/실패/404/timeout 메시지를 사용자에게 표시합니다.
- [x] Android 에뮬레이터 UI-tree 기준으로 고급 연결 설정 토글, 연결 테스트 버튼, `http://10.0.2.2:19080/healthz` 성공 메시지를 확인했습니다.
- [x] 검증: Android 설정 source guard 87/87 PASS, Android Debug build PASS, Android smoke PASS(`mobile-smoke-20260626-230301.md`), 전체 데스크톱 테스트 747/747 PASS.
- [x] Android 0.2.72 / version code 183 signed APK, stable 업데이트 manifest, live 배포 완료(`20260626-android-connection-test-v072`).
- [x] Android 0.2.73에서 고급 연결 URL 저장 전에 `/healthz` 연결 테스트 성공을 필수로 적용했습니다.


### 2026-06-26 Android 고급 연결 저장 전 검증 완료 증거
- [x] “연결 URL 저장” 버튼이 먼저 `/healthz` 연결 테스트를 수행하고, 실패 시 저장하지 않도록 변경했습니다.
- [x] 저장 전 검증 중/실패/성공 후 저장 메시지를 분리했습니다.
- [x] Android 에뮬레이터 UI-tree 기준으로 0.2.73 설정 화면, 고급 연결 설정, 저장 버튼, “연결 테스트 성공 후 운영 서버 기본 연결로 저장했습니다.” 메시지를 확인했습니다.
- [x] 검증: Android 설정 source guard 87/87 PASS, Android Debug build PASS, Android smoke PASS(`mobile-smoke-20260626-231832.md`), 전체 데스크톱 테스트 747/747 PASS.
- [x] Android 0.2.73 / version code 184 signed APK, stable 업데이트 manifest, live 배포 완료(`20260626-android-save-preflight-v073`).

### 2026-06-27 Android 에뮬레이터 핵심 E2E 재검증 증거
- [x] Android 환경 점검 PASS: dotnet, `maui-android`, JDK/keytool, Android SDK/adb, Android Studio 확인.
- [x] 에뮬레이터 `Medium_Phone_API_36.1` 부팅 및 adb 연결 확인 후 검증했습니다.
- [x] 로컬 격리 테스트 API `http://127.0.0.1:19080` healthz/readyz 정상 상태에서 모바일 앱의 `http://10.0.2.2:19080` 연결로 로그인·조회·동기화 smoke PASS: `D:\거래플랜\테스트 시행\evidence\android-smoke-20260627-020159\mobile-smoke-20260627-020200.md`.
- [x] 판매/구매 전표 작성 후 서버 저장·재조회 PASS: `D:\거래플랜\테스트 시행\evidence\android-write-sales-20260627-020402\mobile-write-e2e-20260627-020402.md`, `D:\거래플랜\테스트 시행\evidence\android-write-purchase-20260627-020608\mobile-write-e2e-20260627-020608.md`.
- [x] 판매 수금/구매 지급 저장 후 서버 재조회 PASS: `D:\거래플랜\테스트 시행\evidence\android-payment-sales-20260627-020813\mobile-payment-e2e-sales-20260627-020813.md`, `D:\거래플랜\테스트 시행\evidence\android-payment-purchase-20260627-021010\mobile-payment-e2e-purchase-20260627-021011.md`.
- [x] 모바일 판매전표 오프라인 dirty 저장 후 수동 동기화 push PASS: `D:\거래플랜\테스트 시행\evidence\android-write-sales-offline-20260627-021209\mobile-write-e2e-20260627-021209.md`.
- [x] 403 권한/범위 거부 시 서버 미생성 및 dirty 0건 PASS: `D:\거래플랜\테스트 시행\evidence\android-write-sales-403-20260627-021427\mobile-write-e2e-20260627-021427.md`.
- [x] 수금 stale invoice revision 충돌 시 저장 거부/서버 미반영 PASS: `D:\거래플랜\테스트 시행\evidence\android-payment-stale-sales-20260627-021637\mobile-payment-e2e-sales-20260627-021638.md`.
- [ ] 실기기·운영망·장시간 실행·재로그인 증거는 운영망/기기 조건이 필요하므로 후속 미완료로 유지합니다.

### 2026-06-27 확장 마스터/재고이동 동기화 범위 재검증 증거
- [x] 선택값, 창고별 재고, 재고이동 scope/권한/동기화 항목의 기존 하위 TODO가 모두 완료 상태임을 확인했습니다.
- [x] Desktop 회귀테스트 PASS: `SelectionOption|ItemCategoryOption|ItemWarehouseStock|InventoryTransferScope|InventoryStock|DataIntegrityDuplicateMergeTests.MergeDuplicateIssueAsync_ItemMergeMovesReferencesAndAggregatesWarehouseStock` 필터 28/28 통과.
- [x] Server 회귀테스트 PASS: `ItemCategoryOption|ItemWarehouseStock|InventoryTransfer|WarehouseStock|OfficeOnlyUser_InventoryTransferScope` 필터 56/56 통과.
- [ ] 운영 화면 실제 수동 QA 증거(USENET/YEONSU/ITWORLD 계정별 거래처 선택, 전표 출고창고, 재고이동 창고 선택 제한)는 별도 항목으로 유지합니다.


### 2026-06-27 인쇄 편집/저장 경로 회귀 가드 증거
- [x] 현재 소스에는 TODO에 적힌 `PrintTemplate`/`PrintTemplateVersion` 공통 엔티티·서버 API·마이그레이션이 실제로 존재하지 않음을 확인했습니다.
- [x] 현재 납품 가능한 출력물 편집 경로는 전표별 `InvoicePrintModel` JSON 저장 방식입니다. 저장 위치는 PC 로컬 설정 키 `InvoicePrint:{invoiceId:N}`이며, 판매 화면과 메인 미리보기 모두 저장 JSON을 다시 읽고 현재 거래처/회사 정보 및 전표 라인 순서를 재동기화합니다.
- [x] `PrintEditWindow`/`PrintEditViewModel` 기준으로 거래명세서·견적서·대금청구서 편집, 품목 라인 편집, 미리보기, 저장 명령, JSON 직렬화/역직렬화, 현재 정보/라인 순서 동기화 경로를 회귀테스트로 고정했습니다.
- [x] 인쇄 관련 targeted 테스트 PASS: `PrintEditPersistenceGuardTests|TradePrint|PrintDocumentRenderingSmoke|InvoicePrintLineOrder|InvoicePrintModelCurrentInfoSynchronizer` 필터 26/26 통과.
- [x] Desktop 전체 테스트 PASS: 758/758 통과.
- [ ] 공통 템플릿 엔티티, 템플릿 버전 관리, 드래그 앤 드롭 WPF Canvas 편집기는 기존 전표별 JSON 편집과 다른 스키마/동기화/권한 설계가 필요하므로 별도 설계 후 진행합니다.

### 2026-06-27 렌탈/장비 품목 재고 추적 분리 보강 증거
- [x] 서버 품목 저장 경로에서 `IsRental=true` 또는 장비 품목이 들어오면 `TrackingType=자산`, `ItemKind=장비`, 현재고/안전재고 0, 판매품목 해제로 정규화하도록 보강했습니다.
- [x] 서버 품목 수정 경로에서 일반 재고 품목을 렌탈/장비 품목으로 바꾸면 기존 `ItemWarehouseStocks` 잔여 row를 제거하도록 보강했습니다.
- [x] sync push에서 구형/오염 클라이언트가 자산 품목 창고재고를 보내도 서버가 저장하지 않고 기존 잔여 row를 제거하도록 보강했습니다.
- [x] 서버 시작 정합성 보정에서 비재고/자산 품목의 창고재고 row를 제거하고 현재고 잔여값을 0으로 정리하도록 보강했습니다.
- [x] 회귀테스트 PASS: `RentalItemInventoryGuardTests` 4/4 통과, 관련 서버 테스트 235/235 통과, 서버 전체 테스트 560/560 통과.

### 2026-06-27 Android 렌탈/장비 품목 로컬 재고 표시/dirty 차단 증거
- [x] `ItemEditPage`에서 비재고/자산 품목은 현재고·안전재고 입력을 비활성화하고 화면값을 0으로 고정했습니다.
- [x] 모바일 품목 목록 상세와 전표 품목 선택 상세에서 비재고/자산 품목은 “재고 추적 대상 아님”으로 표시하고 창고재고 fallback row를 만들지 않도록 했습니다.
- [x] 모바일 품목 저장 DTO와 네트워크 장애 pending 저장 경로 모두 `ItemOperationalPolicy`로 정규화해 비재고/자산 품목 현재고·안전재고가 0으로만 저장되도록 했습니다.
- [x] 검증: `MobileNonInventoryItems_DoNotExposeOrQueueStockValues|MobileReleaseConfigurationTests` 85/85 PASS, Android Debug build PASS, Android 에뮬레이터 smoke/권장 동기화 PASS(`D:\거래플랜\테스트 시행\evidence\mobile-noninventory-20260627\mobile-smoke-20260627-030235.md`).
- [x] Android 0.2.76 / version code 187 signed APK 생성 및 stable 업데이트 manifest live 반영 완료. live manifest와 다운로드 APK SHA256 검증 PASS.
- [x] Android sync pull 캐시와 pending push에서 비재고/자산 품목 창고재고 row를 정리하고, scope filter가 해당 row를 서버로 올리지 않도록 보강했습니다.
- [x] Android 0.2.77 / version code 188 signed APK 생성, 에뮬레이터 release smoke PASS, stable 업데이트 manifest live 반영 및 다운로드 SHA256 검증 PASS.
- [ ] Android 실기기·운영망 장시간 실행 증거는 별도 기기 조건에서 추가 누적합니다.

### 2026-06-27 PC 비재고/자산 품목 로컬 재고 캐시 및 push/pull 차단 증거
- [x] PC sync push 직전에 비재고·자산 품목 창고재고 row를 제거하고 서버 전송 대상에서 제외합니다.
- [x] PC sync pull에 비재고·자산 품목 창고재고 row가 섞여도 로컬 저장하지 않고 현재고·안전재고를 0으로 유지합니다.
- [x] PC 수동 재고 조정 서비스가 재고 추적 대상이 아닌 품목의 0이 아닌 재고 입력을 거부하고 기존 잔여 row를 정리합니다.
- [x] 회귀 테스트 PASS: `SyncItemWarehouseStockPullTests|LocalIntegrityInventoryResidueTests` 8/8, 동기화/재고 관련 확장 필터 24/24.
- [ ] 사용자 PC별 오래된 로컬 DB와 운영망 현장 실행 증거는 별도 조건에서 추가 누적합니다.
- [x] PC 1.1.615 설치/업데이트 패키지 생성 및 Linux PC live manifest 반영 PASS(SHA256 `F7161495B4E07F1ECF97F3F95D418F2A6785C855CEFE1069AD0BE28F213376FB`).

### 2026-06-27 PC 창고재고 sync push scope 필터 증거
- [x] PC sync push 창고재고 수집이 item write scope와 warehouse write scope를 모두 만족하는 row만 전송하도록 보강했습니다.
- [x] USENET office-only 사용자가 YEONSU 품목/창고 row를 push 후보에 싣지 않는 회귀테스트를 추가했습니다.
- [x] 회귀 테스트 PASS: 창고재고 push scope targeted 8/8, 동기화/권한 확장 회귀 17/17, Desktop 전체 764/764.
