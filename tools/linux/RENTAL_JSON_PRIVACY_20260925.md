# 렌탈 청구 JSON 비공개 금액과 이력 보존

## 구현 범위

렌탈 템플릿의 UnitPrice/Amount, 청구 실행 이력의 BilledAmount/SettledAmount를 nullable decimal로 바꿨다. 기본값 0은 기존 JSON과의 호환성을 유지하지만, 명시적인 null은 금액 비공개로 구분한다. JSON의 null 생략 옵션을 사용해도 이 네 필드는 반드시 기록한다.

`RentalBillingJsonPrivacy`는 청구 JSON 사본의 해당 금액만 null로 바꾼다. 속성 이름 대소문자를 처리하고 생략된 금액 속성도 명시적인 null로 추가한다. 품목·수량·비고·RunId·RunKey·자산 연결·tombstone·확장 속성은 보존한다. 파싱할 수 없거나 구조가 잘못된 JSON은 원문을 송신하거나 빈 이력으로 대체하지 않고 실패를 반환한다. 로컬 저장 원문은 수정하지 않는다.

`LocalMappings.ToDto`는 프로필이 금액 비공개이면 위 사본을 송신한다. 실패한 HTTP 전송 이후 숨김 Pull 및 재시도 시험에서 스칼라 금액뿐 아니라 실제 단가·청구액·입금액이 들어 있던 JSON도 null로 전송되며 원본은 유지됨을 검사한다.

## 조회와 재계산의 구분

- 공유 계약의 `ValidateForAmountPrivacyRead`는 비공개 청구액/입금액의 null을 조회할 때만 허용한다. 기존 Validate, 서버 mutation, 재계산 검증의 숫자 요구 조건은 그대로 유지한다. 이력 식별자 중복, 상태, 날짜, 음수 금액 검증도 완화하지 않는다.
- 템플릿/이력 파서는 비공개 값 때문에 이력을 빈 배열로 바꾸지 않는다. 비공개 프로필의 캐시 금액도 조회 사본에서는 숨긴다.
- 시작 시 연결 자산을 근거로 하는 금액 자동 보정은 프로필·자산·템플릿 중 금액 미확정 자료가 있으면 원문을 유지한다.
- 무결성 검사는 자산 연결 오류는 계속 보고하되, 비공개 금액을 0원이나 금액 불일치로 판정하지 않는다.
- 아직 필수 숫자를 받는 금융 계산·편집 소비자는 `DisclosedAmount.Require`로 비공개 값을 거부한다. 서버 계산 경로가 준비되기 전에 로컬 금액을 임의 산출하는 동작을 허용한 것이 아니다.

## 증거

`D:\DevCaches\tradeplan-rental-json-privacy-20260925`의 TRX, 빌드 로그, 공식 테스트 로그, `verification.json`을 참조한다.

- 새 JSON/읽기/검증/보정/무결성 시험 16개와 기존 렌탈 Pull 시험 5개 집중 검사.
- 관련 렌탈·outbox·데이터 무결성·정산 회귀 검사 및 서버 렌탈/전표 범위 검사.
- Android Debug APK 재빌드. 실기기/업그레이드 설치 검증을 대신하지 않는다.
- 초기 읽기 시험 실패 중 2건은 시험 데이터에서 tombstone 시간 null 필드를 생략한 오류였다. 정상 저장 형식으로 시험 데이터를 수정했다. 해당 실패를 운영 장애 재현으로 집계하지 않는다.

## 남은 통합 작업

이 단계는 JSON 전송·파서 기반의 보완이며 전체 렌탈 비공개 기능 완료가 아니다. 운영 응답의 렌탈 금액 마스킹은 계속 비활성이다.

1. RentalBillingViewModel/AutoSave의 숫자 편집기와 CreateBillingViewRow/BuildBillingHistoryRows/HistorySummary 등은 여전히 공개 금액을 요구한다. 비공개일 때도 메타데이터를 보여주고 품목·수량·비고를 저장하도록 nullable 표시·계산·저장 경로를 완성해야 한다.
2. LocalStateService의 ResolveBillingRunAmountAsync, 엄격한 TryDeserializeBillingRuns의 실패 후 대체 금액 사용 경로와 자산/프로필 로컬 숫자 필드의 모든 소비자를 점검해야 한다. 비공개 값을 실제 0원이나 이전 금액으로 대체하면 안 된다.
3. 서버는 권한별로 금액을 계산하고 비금액 변경을 보존해야 한다. 신규 금액 미확정 자료 정책, 응답 마스킹, 프로토콜 호환성, 권한별 실제 PC/Android 화면·출력 시험이 필요하다.
4. 기존 전체 Goal의 실데이터 확인·백업 복원·실기기·설치 패키지/서명·운영·Git 조건은 그대로 남아 있다. RT 외부 서비스와 운영 DB는 이번에 변경하지 않았다.
