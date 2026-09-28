# Android 서명 검사 보완과 전환 준비 — 2026-09-23

## 확인한 문제와 수정

기존 `Test-GeoraePlanAndroidSigningContinuity.ps1`은 apksigner 출력에서 첫 번째 서명자의 인증서만 추출했다. 첫 인증서가 같고 두 번째 인증서가 추가된 출력도 첫 인증서만 반환하는 것을 격리된 모의 출력으로 재현했다. 이것은 파서의 누락 증거이며 실제 다중 서명 APK 설치 시험은 아니다.

현재 단일 서명자 배포를 유지하도록 파서를 보완했다. 인증서 주체와 SHA-256이 각각 정확히 하나이고 서명자 번호가 1이어야 한다. 중복/복수 서명자, 명시된 서명자 수 불일치, 누락/빈 주체, 64자리 16진수가 아닌 해시는 중단한다. 정상 단일 서명·기존 Debug 인증서 경고·앱 ID/버전/파일 해시 검증은 유지한다. 서명 전환 이력의 호환성을 새로 지원한 변경은 아니다.

- 수정: `tools/mobile/Test-GeoraePlanAndroidSigningContinuity.ps1`
- 신규 회귀 검사: `tools/verification/Test-GeoraePlanAndroidSignerOutput.ps1`
- 영향: 배포 전 서명 메타데이터 검사만 강화. 업무 화면, 업체/지점 데이터, 동기화, 운영 DB, APK, 버전, 서명키 변경 없음.

## 검증

- PowerShell 7: 15개 출력 사례 및 실제 공개본과 동일한 로컬 APK 서명 확인, 16/16 PASS.
- Windows PowerShell 5.1: 15개 출력 사례, 15/15 PASS.
- 기존 Release 테스트 DLL의 AndroidSigningContinuity 관련 검사 2/2 PASS(no-build/no-restore). 전체 새 빌드 증거는 아니다.
- 수정한 원본 도구 전체를 공개 서버 대상으로 실행: 매니페스트·실제 APK 다운로드·앱 ID·193→194 버전 코드·서명 비교 PASS. 공개 배포본 인증서는 여전히 `dfc2e3680116ebe4291c466ba7da9491a2ecdf8502323ffafefc155e0c45dc28`이다.
- 원본 PC/공식 시험 DB·앱 파일·Git index 등 보호 대상 9개 해시 보존. 검토 worktree에는 한정된 변경만 복사했다. stage/commit/push/live 미실행.

## 실제 설치 시험이 멈춘 지점

기존 기기를 보존하고 새 Android 34 AVD를 별도 경로에 준비했다. 요청 데이터 파티션은 2GiB였지만 Emulator 37.1.11은 설정을 6GiB로 변경하고 약 7372.80MB의 여유를 요구했다. 당시 D: 여유 4717.11MB로 부팅 전에 exit1 종료했다. 데이터 이미지 생성·APK 설치·기존 앱 삭제는 수행되지 않았다. 새 경로에는 설정/로그 보조 파일만 남았다.

경로: `D:\DevCaches\tradeplan-upgrade-avd-20260923`. 기존 AVD와 청구용PC 파일은 변경하지 않았다. 종료 후 emulator/qemu 프로세스 및 ADB 기기 부재를 확인했다. 최소 요구량을 우회하지 않았으며, 설치와 자료 보존 검증은 아직 미완료다. 여유 20GB 이상의 시험 저장 공간을 확보하면 복원 시험과 함께 진행하기 좋다.

## 서명 전환 적용안 — 아직 미실행

현재 공개 APK는 minSdk21이며 Android Debug 인증서로 서명돼 있다. 새 키만 사용한 APK를 배포하는 것으로 기존 설치 앱의 업데이트를 보장할 수 없다. 직접 APK 배포 경로를 기준으로 아래 절차를 준비한다.

1. 설치된 업무용 휴대폰 유무·Android 버전·기존 APK 인증서를 확인하고, 미전송 업무·첨부를 보존할 복구 절차를 검증한다. 질문은 대기 중이며 설치 기기가 없다고 가정하지 않는다.
2. 기존 서명키의 보존과 사용 가능성을 확인하고, 정식 키는 비밀 입력·접근 제한·복구 가능한 별도 보관을 포함해 관리한다. 키의 장기 유효기간도 정한다. 이번 점검에서 키 생성·조회·교체는 실행하지 않았다.
3. 기존 설치가 있으면 구 키에서 새 키로 연결되는 서명 이력을 사용한 회전 후보를 만든다. 회전 시작 SDK 아래에서는 원래 키를 사용하는 구성이 필요하다. Android 9(API28)부터 회전할지, 기본 API33 경계를 사용할지 실제 지원 기기와 함께 결정한다. [공식 apksigner 옵션](https://developer.android.com/tools/apksigner)
4. API21–27 지원을 유지한다면 해당 기기의 원래 인증서 호환성을 보존한다. API28–32와 API33 이상은 선택한 회전 경계에 맞춰 검사한다. 서로 다른 Android 세대의 회전/롤백 동작을 한 기기 성공으로 대체하지 않는다. [AOSP 서명 v3](https://source.android.com/docs/security/features/apksigning/v3), [v3.1](https://source.android.com/docs/security/features/apksigning/v3-1)
5. 현 빌드 도구는 서명 이력을 전달하지 않고, 연속성 검사는 인증서의 정확한 일치를 기준으로 한다. 서명 회전을 도입할 때는 이 경로에 이력 검증과 SDK별 기대 인증서 검사를 추가해야 한다. `AcceptCertificateChange`는 차이를 허용하는 옵션일 뿐 이력·설치·자료 보존 검증의 대체물이 아니다.
6. 공개 구 APK 설치 → 실제 시험 업무/미전송 자료 준비 → 새 APK 제자리 업데이트 → 앱 ID/UID·버전·업무·첨부·계정별 대기 보존 → 실제 저장/동기화 → 실패 복구를 검증한다. 삭제 후 재설치를 성공적인 제자리 업데이트로 기록하지 않는다.
7. 사용자 수락, 실제 Release 빌드·패키지 해시, 배포 직전 백업/복구 조건을 충족한 뒤 별도 운영 반영한다. 새 키로 전환한 뒤 구 APK를 단순 재배포하는 방식의 원복은 보장하지 않으며 검증된 복구 방식을 먼저 확정한다.

설치된 업무 기기가 전혀 없다면 신규 설치를 위한 정식 키 도입이 가능한지 검토할 수 있지만, 그 사실이 확인되기 전에는 기존 사용자 보존 조건을 제거하지 않는다. Android 공식 문서는 키의 보존과 앱 수명을 충분히 넘는 인증서 유효기간을 강조한다. [앱 서명 관리](https://developer.android.com/studio/publish/app-signing)

## 증거와 남은 작업

`C:\Users\beene\Documents\Codex\tradeplan-android-upgrade-20260923`

- continuity-before.ps1, reproduce-parser.ps1, parser-gap.json
- parser-ps7.json, parser-ps5.json, tests/signing-continuity.trx
- patched-continuity.log, patched-continuity-exit.json
- emulator-state.json, emulator.stdout.log, emulator.stderr.log, start-emulator.py
- final-verification.json: 보존·종료·검토 범위 확인

실제 설치 시험 공간, 물리 기기, 서명 전환의 실제 검증, 사용자 수락·Release·운영/Git 반영, 원본 업무 충돌과 독립 호스트 복원, 워크플랜 잔여 검증은 미완료다. 전체 Goal을 완료하지 않는다.
