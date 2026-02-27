# Phase 2 TODO

이 파일은 Phase 1 완료 후 구현할 기능 목록입니다.

---

## 1. 인쇄 템플릿 에디터 (PrintTemplate)

- [ ] `PrintTemplate` + `PrintTemplateVersion` 엔티티 활용 (이미 DB 스키마 존재)
- [ ] 드래그 앤 드롭 레이아웃 에디터 (WPF Canvas)
- [ ] JSON 직렬화로 템플릿 저장/복원
- [ ] 외부 리포팅 도구 또는 RDLC 통합 검토

---

## 2. 안드로이드 앱

- [ ] MAUI 또는 Kotlin 클라이언트
- [ ] 동일한 `/sync/pull`, `/sync/push` API 재사용
- [ ] 오프라인 SQLite (Room 또는 MAUI SQLite)
- [ ] 터치 최적화 전표 입력 UI

---

## 3. 원격 접속 터널

- [ ] ngrok / Cloudflare Tunnel 통합
- [ ] 설정 화면에서 터널 URL 입력
- [ ] `appsettings.json`의 `Api.BaseUrl`을 터널 URL로 전환

---

## 4. 고급 리포트

- [ ] 월별 매출 차트 (WPF LiveCharts2 또는 OxyPlot)
- [ ] 거래처별 잔액 현황
- [ ] 기간별 수금율 대시보드

---

## 5. 다중 사용자 개선

- [ ] 사용자 관리 UI (현재 API만 존재)
- [ ] 권한별 UI 컴포넌트 비활성화
- [ ] 세션 만료 자동 재로그인

---

## 6. 재고 관리

- [ ] `LocalItem`의 IsRental 품목 재고 추적
- [ ] 임대 시작/종료 달력 뷰
- [ ] 재고 부족 알림

---

## 7. 전자세금계산서 연동

- [ ] 홈택스 API 연동 (전자세금계산서 발행)
- [ ] 세금계산서 번호 자동 채번

---

*Phase 1 완료 기준: Gate A/B/C 모두 통과 + 3화면 동작 + PDF 저장 성공*
