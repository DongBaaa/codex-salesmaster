# Android 중단 없는 30분 사용 검증 — 2026-09-23

## 결과

격리 API와 실제 시험 APK 0.2.83/code194에서 31.5분 동안 같은 Android 앱 프로세스로 판매 저장 10회 및 홈 이동·복귀 10회를 완료했다. 매 회차의 실제 화면 실행, 독립 서버 DB 검사, 모바일 DB/WAL 대조가 통과했다. 제품 소스·버전·운영 배포는 변경하지 않았다.

APK SHA256: `6c0f363931ecc32e636c7ff57b84fd36dbdbca84554a4d832417a841d08478d1`.

## 검증 방법과 결과

- 첫 로그인과 이전 세션 정리는 측정 전에 완료했다. 이후 PID 3629, 프로세스 시작 tick `7070`, 부팅 ID가 모든 표본에서 같았다. 측정 중 강제 종료·재설치·데이터 초기화·강제 GC를 사용하지 않았다.
- 매 회차 품목 탭 → 거래처 검색 → 판매 작성 → 품목 선택 → 실제 저장 → 서버 확인 및 시험 자료 정리 → 실제 동기화 → Android 홈 15초 → 앱 복귀를 수행했다.
- 각 회차 전표·전표 품목이 정확히 1행이고 수량1·11,000원이었다. 서로 다른 전표 ID 10개를 확인했다. TenantCode USENET_GROUP 및 ResponsibleOfficeCode USENET을 검사했다.
- 해당 회차의 거래처·품목·전표·전표 품목은 소프트 삭제되고 시험 재고는0이었다. 각 동기화 뒤 실행 중 DB/WAL을 두 번 읽어 동일한 쌍을 확보했으며 integrity ok·저장된 모든 계정의 전송 대기0·현재 유즈넷 오류 없음이었다. DB 편집으로 대기를 없애지 않았다.
- Android lastanr와 crash 버퍼를 시작/종료 시 대조했다. 차이가 없었으며 최종 원본 파일은 증거 폴더에 보존했다.
- 기존 보정 E2E 감사 사본으로 정확한 거래처 결과 행의 판매작성 버튼만 선택했다. 빈 검색창과 이전 검색어가 남은 상태를 구분하고, 동기화 후 홈 탭으로 돌아가 다음 회차를 시작했다. 공식 E2E 원본은 바꾸지 않았다. 이번 실행 도중 도구 보정/재시작은 없었다. 이전 중단 기록은 ANDROID_CONTINUOUS_USE_VERIFICATION_20260923.md에 그대로 보존한다.

## 메모리와 시간 해석

20초 간격으로 총 94개 메모리 표본을 수집했다. PSS는 시작 259.9MiB, 종료 351.3MiB, 최소 259.9MiB, 최대 388.2MiB였다. 처음/마지막 5표본 중앙값은 321.2/350.4MiB다. 전체 한 회차는 184.7~188.3초였으며 UI 덤프·고정 대기·서버 정리·홈 대기가 포함된 시간이다. 이를 저장 버튼 자체의 응답 시간으로 해석하지 않는다.

PSS 변화에는 화면 로딩·캐시·시험 자료와 삭제 이력 증가가 포함된다. 이 결과만으로 메모리 누수가 없다고 판정하지 않는다. 실제 사용자 동작을 반복하는 지속 사용 시험이며 다중 사용자 최대 부하나 장기간 배터리/발열 시험은 아니다.

## 보존과 남은 항목

원본 PC DB·공식 시험 DB/API DLL·Git index·데스크톱/모바일 프로젝트 파일·원본 E2E·APK 해시를 전후 대조했다. 기존 업무 행 대조도 수행했다. 소유 시험 API와 AVD는 종료했다. 운영/Git/버전 변경 없음.

물리 Android 기기, 정식 Release 서명과 실제 제자리 업데이트, 사용자 수락, 기존 업무 자료 충돌, 독립 호스트 백업 복원, Windows 로컬 연결 reset 원인, 워크플랜 잔여 게이트는 별도 미완료다. 전체 Goal은 완료하지 않는다.

## 증거

`C:\Users\beene\Documents\Codex\tradeplan-android-uninterrupted-20260923`

- baseline.json / memory-samples.json / lifecycle.json / cycles.json / result.json
- cycle-*/ 및 cycle-*-sync/: 실제 UI 단계·저장·동기화
- cycle-*-state/ 및 end-state/: 실행 중 일관된 DB/WAL·계정별 전송 대기
- anr-before/after.txt, crash-before/after.log, end-screen.png
- input-manifest.json / source-preservation.json / mobile-final-summary.json
- business-comparison.private.json / ledger-semantic-preservation.json / verified-summary.json


## 로그 해석 보충

부팅 초기 UTC11:45:06.686에 Android `com.android.systemui`(PID717)의 `subscriptionId` 오류가 있었다. 측정 시작 전 기록이며, 측정 전후 crash 버퍼는 동일하다. 거래플랜 프로세스 충돌과 측정 중 새 충돌은 관찰되지 않았고 부팅 이후 ANR도 없다. 빈 crash 로그로 주장하지 않는다. `crash-classification.json`에 시작 시각과 구분 근거를 남겼다. 서버 로그의 준비 중 readiness503, 격리 환경 업데이트 manifest404, 초기 만료 세션401, 변경 대기 sync/wait499도 `api-log-summary.json`에 보존했다. 업무 요청5xx와 서버 fail 로그는0이다.
