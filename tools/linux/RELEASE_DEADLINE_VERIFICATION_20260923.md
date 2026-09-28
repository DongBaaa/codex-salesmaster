# 거래플랜 배포 준비 검사 시간 제한

2026-09-23. 로컬 배포 도구 수정과 격리 검증 진행 가능. 운영 스크립트/앱/DB/서비스 변경 없이 수행했다. 직전 Goal 단계는 한 파일 설치 도구20개 검증 완료이며 이번 단계는 실제 시간 제한과 모든 발견된 호출자 호환성 보완이다.

## 문제와 변경
- 기존 Publisher는 HEALTH_CHECK_RETRIES=900을 전달했다. 각 반복의 HTTP 요청2개와 sleep을 합하면 최악의 준비 검사 한 구간이 약5시간15분까지 늘어날 수 있었고, 원복에서도 같은 반복횟수를 썼다.
- 이제 apply-release.sh는 각 준비 검사 구간의 경과시간을 제한한다. healthz와 readyz가 같은 기한을 공유하고 curl 최대 요청시간/연결시간은 남은 예산을 넘지 않는다. 기한 뒤 HTTP200은 성공으로 인정하지 않는다.
- 새 환경변수는 HEALTH_CHECK_TIMEOUT_SECONDS, ROLLBACK_HEALTH_TIMEOUT_SECONDS. 공식 Publisher/Deploy-After-Test/한글 wrapper의 기본값은 각각900초(15분)이며 두 값을 독립 지정할 수 있다. 허용1~3600초, 0/음수/초과/선행0 등 잘못된 Linux 값은 앱 변경 전 중단한다. 직접 셸 실행 기본120초, 원복 미지정 시 배포 값 상속.
- 옛 HEALTH_CHECK_RETRIES 변수가 존재하면 사전 거절한다. 새 Publisher는 --capabilities의 정확한 응답을 업로드 전과 apply 직전에 검사한다. SkipPlatformHealthChecks로 우회되지 않는다.
- 추가로 발견된 Invoke-GeoraeplanUsenetDatabaseSplitCutover.ps1 호출도 새900초 값2개를 전달한다. apply 모드에서 DB 조회/중지/생성/설정 변경 전에 capability를 확인한다. 운영 DB 분리 작업은 실행하지 않았다.
- 준비 검사만의 제한이며 파일 복사, Docker 명령, 백업/업로드 시간을 포함한 전체 작업 제한이 아니다. Bash 정수 초 계산 및 OS 스케줄링 오차가 있어 실시간 시스템의 엄밀한 hard deadline을 보장하지 않는다.

## 검증
- Linux 원복/시간 제한 **27/27 통과**, Bash 구문 및 capability 응답 정상. 실제 cp/rsync와 임시 폴더를 사용하고 Docker 명령은 대역으로 제한했다.
- 실제 curl + 임시127.0.0.1 서버: 새 앱 상태에서 응답을3초 지연, 준비 검사1초 제한으로 실패한 뒤 기존 파일 원복 및 old healthz/readyz200 확인. 전체 fixture 약1.45초, exit30. 운영 HTTP 주소에는 시험 요청을 보내지 않았다.
- 지연 health, 공유 예산 ready, 늦은200, 원복 시간 초과를 실제 경과시간으로 확인했다. 원복 실패는 exit31로 구분한다.
- 유즈넷 분리 도구의 실제 내장 셸을 임시 경로로 치환한 **2개 호출자 검사 통과**: 미지원은 exit35/외부 명령0/파일 무변경, 지원은 첫 Docker inspect 대역까지만 도달 후 exit77/변경0. DB 생성·서비스 정지 명령은 실행되지 않았다.
- 한 파일 교체 도구 **20/20 재통과**, 새 후보의 운영 읽기 전용 설치 점검도 통과(changed=false).
- PowerShell7·5.1 각각 **30/30 통과**: 실제 파라미터 바인딩, 독립 예산, 범위 거절, capability 거절/연결 실패, 필수 사전 게이트 위치, 실제 apply 명령과 Deploy 인수 구성, 실제 한글 wrapper를 무해한 child로 실행한 전달 확인.
- 실제 Publisher apply/postflight AST의 보존/실패 경계는 PowerShell7·5.1 각각 **6/6 통과**. 미지원 capability는 disk/apply/gate/prune에 진입하기 전 중단한다. 원격 실행/삭제0.
- 최초 PowerShell5 검증은 새 검사 파일의 UTF-8 BOM 누락으로 한글 경로가 깨져 실패했다. 검사 파일 인코딩 수정 후30개 통과. 실패 기록을 별도 보존했다.
- 운영 apply-release.sh 해시, API/DB ID·시작 시각·재시작 횟수 전후 동일, readyz200. 소유 원격 임시 디렉터리 부재 확인.

## 최신 후보와 파일
- 새 apply-release.sh SHA-256: `fbf0caf15a2efa3a9756642c4b5d602acd5019fcdaaeef867adc9f987f5a52b2`.
- 운영 기존 해시: `75ad0948b701ac32cb1f1396d1e2cb811058e50f9540313f34d5705b9e1390a4`. **새 후보는 아직 설치하지 않았다.**
- 수정: tools/linux/assets/georaeplan-release/apply-release.sh 및 README.md, tools/linux/Publish-GeoraeplanLinuxPcRelease.ps1, tools/linux/Invoke-GeoraeplanUsenetDatabaseSplitCutover.ps1, 테스트 시행/Deploy-After-Test.ps1, 테스트 시행/검증완료-반영.ps1, tools/verification/Test-GeoraePlanReleaseRollback.py, Test-GeoraePlanReleaseRetention.ps1, 신규 Test-GeoraePlanReleaseDeadlines.ps1, 검증 보고서와 변경기록.
- 앱/서버 제품 소스와 기존1.1.743 패키지는 변경하지 않았다. 검토 worktree의 선택 목록에 운영 도구/검사/보고서만 명시적으로 반영하며 원본 Git index를 보존한다.

## 다음 단계
검증된 최신 해시로 관리 스크립트를 설치하고 공식 시험 수락/운영 배포 전후 검사 및 Git 반영을 진행해야 한다. 실제 시작 소요시간과 업무 쓰기를 포함한 배포 창은 별도로 확정한다. 사용자 자료 확정, 실기기, 독립 호스트 복구 등 전체 Goal의 미완료 항목은 유지한다. 이 결과는 앱 운영 반영이나 전체 점검 완료의 증거가 아니다.

증거: `C:\Users\beene\Documents\Codex\tradeplan-release-deadline-20260923`의 rollback.json, installer.json, powershell7.json/powershell5.json, retention7.json/retention5.json, production-read-only-check.json, cleanup.json, completion.json. 초기25/26개 결과와 최초 PS5 오류도 별도 보존.
