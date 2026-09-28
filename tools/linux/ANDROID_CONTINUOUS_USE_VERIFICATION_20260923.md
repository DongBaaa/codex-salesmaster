# Android 반복 사용·프로세스 연속성 검증 — 2026-09-23

## 결과와 범위

실제 시험 APK 0.2.83/code194를 재시작하지 않고 반복 판매·동기화·Android 홈 이동/복귀를 검사했다. 동일 PID/시작 tick/부팅 ID가 유지됐으며 두 측정 구간 합계 32.71분, 그 사이 도구 진단 시간 3.18분, 전체 경과 35.89분이다. 두 구간에서 판매 10회가 완료됐다. **자동화가 중단 없이 30분 성공한 결과는 아니다.** 시험 도구 오류 두 번과 보정 시간을 아래에 별도로 기록한다.

최종 보정 도구 구간은 15.92분, 5회 전체 통과다. 제품/공식 E2E/버전/APK/운영 설정은 변경하지 않았다. APK SHA256은 `6c0f363931ecc32e636c7ff57b84fd36dbdbca84554a4d832417a841d08478d1`이다.

## 실제 검증

- 품목 탭 → 거래처 검색 → 해당 거래처 판매 작성 → 품목 선택 → 판매 저장 → 서버 확인/시험 자료 정리 → 실제 동기화 → Android 홈15초 → 앱 복귀를 반복했다.
- 각 성공 전표/품목은 정확히1행, 수량1·금액11,000원이었다. TenantCode USENET_GROUP과 ResponsibleOfficeCode USENET을 각각 확인했다. 서로 다른 성공 전표 ID이며 중복 저장은 없었다.
- 각 회차 후 실행 중 DB/WAL을 두 번 읽어 동일한 쌍을 확보했다. integrity ok, 저장된 모든 계정 전송 대기0, 현재 유즈넷 오류 없음이다. DB를 편집하거나 강제 종료해 성공 상태를 만들지 않았다.
- 최종 전체 13개 fixture의 거래처/품목·생성된 전표/품목 소프트 삭제와 재고0을 독립 조회했다. 최초 준비 구간까지 포함하면 실제 성공 저장은 11회다. 원래 업무 행은 별도 비교했고 변경되지 않았다.
- PID 3530 / 시작 tick `12832` / 동일 부팅 ID를 확인했다. 측정 중 앱 강제 종료·재설치·초기화·강제 GC는 없었다. 첫 측정 시작과 최종 종료의 ANR/crash 기록도 같았다.

## 시험 도구 오류와 정정

1. 첫 실행은 1회 성공 후 다음 회차에서 이전 검색어가 유지되어 빈 검색창 안내 문구 검사가 실패했다. 실제 초기화 버튼을 누르는 감사 사본으로 보정했다. 원본 실패와 정리 증거는 보존했다.
2. 다음 구간은 5회 성공 후, 입력칸의 fixture 이름만으로 결과가 준비됐다고 판단해 첫 번째 다른 거래처의 판매작성 버튼을 눌렀다. 전표 작성 화면의 선택 거래처 검사에서 중단되어 **다른 거래처로 저장되지는 않았다**. 검색 결과의 정확한 TextView 이름을 기다린 뒤 그 결과 행 안의 판매작성 버튼만 선택하도록 감사 사본을 보정했다. 실제 실패 XML과 실제 결과 행 XML로 입력칸만 일치하는 경우를 거절하는지 확인했다.

제품 문제를 가정해 검색 기능이나 업무 데이터를 수정하지 않았다. 보정 후에도 선택 거래처·서버 ID·금액·중복·정리 검사를 유지했다. 중간 실패를 PASS로 변경하지 않는다. 준비 과정의 Python 인코딩 오류와 과거 덤프를 양성 예제로 잘못 가정한 검증 시도도 기록으로 남긴다.

## 메모리·서버·한계

20초 간격 표본 98개의 PSS는 시작 306.3MiB, 종료 338.8MiB, 최소 306.3MiB, 최대 377.6MiB다. 화면/캐시·삭제 이력 증가가 포함되므로 누수 없음으로 판정하지 않는다. 두 측정 구간 사이의 자동 표본 공백은 위에 명시했다.

서버 로그의 비성공 응답은 `api-log-final-summary.json`에 경로별로 기록했다. 준비 중 readiness503, 격리 업데이트 루트의 manifest404, 초기 보존 세션의401, sync/wait499를 성공 전표 저장과 분리했다. 업무 요청의5xx는 없었다. 단일 사용자의 실제 입력 시험이며 다중 사용자 최대 부하·실기기 배터리/발열·정식 Release 업데이트 검증은 아니다.

원본 PC DB·공식 시험 DB/API DLL·Git index·프로젝트 파일·원본 E2E·APK 해시는 같고 소유 시험 API/AVD는 종료됐다. 운영·버전·Git 변경 없음. 중단 없는30분 자동화, 물리 기기, 정식 서명/실제 업데이트, 사용자 수락, 업무 자료 충돌, 독립 호스트 복원, Windows 로컬 reset 원인, 워크플랜 잔여 항목은 미완료로 둔다. 전체 Goal은 완료하지 않는다.

## 증거

`C:\Users\beene\Documents\Codex\tradeplan-android-continuity-20260923`

- result.json: 최초 FAIL, retry/result.json: 16분대 구간 FAIL, continuation/result.json: 최종 보정 구간 PASS
- 각 구간 baseline.json / memory-samples.json / cycles.json / lifecycle.json / cycle-*/
- second-harness-correction.json / prior-row-observations.json / row-selector-observed-fixtures.json
- all-attempts-cleanup.json / verified-summary.json / api-log-final-summary.json
- continuation/mobile-final-summary.json / continuation/end-screen.png / source-preservation.json
- continuation/business-comparison.private.json / continuation/ledger-semantic-preservation.json
