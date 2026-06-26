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
  - [ ] Android 실기기/에뮬레이터 기준 조회/저장/동기화/권한 거부 E2E
    - [x] 에뮬레이터 로컬 테스트 서버 조회/초안/동기화 smoke PASS(mobile-smoke-20260626-205314, mobile-smoke-20260626-205501)
    - [x] 에뮬레이터 로컬 테스트 서버 저장 성공/저장 거부/dirty push E2E PASS(mobile-write-e2e-20260626-210155, mobile-write-e2e-20260626-210415, mobile-write-e2e-20260626-210634, mobile-write-e2e-20260626-210854, mobile-payment-e2e-sales-20260626-211141, mobile-payment-e2e-purchase-20260626-211401, mobile-payment-e2e-sales-20260626-211621)
    - [x] 에뮬레이터 로컬 테스트 서버 권한 거부 403 E2E PASS(mobile-write-e2e-20260626-212730, mobile-payment-e2e-sales-20260626-212953)
    - [ ] 운영망/실기기 기준 권한 거부 E2E 증거 누적
  - [x] 전체 업무 화면별 수동 QA 체크리스트 정례화
  - [ ] 전체 업무 화면별 실제 수동 QA 증거 누적
- [x] 세션 만료 자동 재로그인/갱신 및 401 후 재시도
- [ ] 로컬 확장 마스터(담당지점/창고/선택값)와 재고이동의 서버 동기화 범위 확대
  - [x] 선택값 저장/삭제 동시성 및 무결성 회귀
  - [x] 창고별 재고 pull, 서버 누락 행 정리, dirty 보존 회귀
  - [x] 재고이동 생성/확정/삭제/복구/영구삭제 권한·scope 회귀
  - [x] 서버 재고이동 push scope 및 창고별 재고 반영 회귀
  - [ ] 실기기/운영 화면 기준 담당지점·창고 선택 제한 수동 QA 증거 정례화

---

## 6. 재고 관리

- [x] 내부 재고이동 기본 흐름
- [ ] `LocalItem`의 IsRental 품목 재고 추적 고도화
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
