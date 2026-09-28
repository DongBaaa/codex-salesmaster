# Android 공개 배포본과 실제 다운로드 경로 검증 — 2026-09-23

## 결과

`https://trade.2884.kr/updates/manifest?channel=stable` 및 실제 공개 APK를 읽어 검증했다. 공개 배포본은 0.2.82/code193, 앱 ID `kr.georaeplan.mobile`, 42,928,348바이트이며 SHA-256은 `82871101e42cbf906c494dd47fb64321b2fcd7a8f1bb1dd4e94622e720f54600`이다. 매니페스트·실제 다운로드·기존 로컬 배포 APK가 일치한다.

공개 APK와 현재 시험 APK 0.2.83/code194는 같은 서명 인증서다. 기존 `Test-GeoraePlanAndroidSigningContinuity.ps1`을 수정 없이 실행해 앱 ID, 더 높은 versionCode, 공개 다운로드 해시와 서명 연속성을 확인했다. 검사는 PASS지만 **실제 기기의 제자리 업데이트 성공 또는 정식 Release 배포 완료를 뜻하지 않는다.**

## 공개 서버를 사용한 공용 다운로드 코드 검증

제품의 `Shared/GeoraePlan.UpdateTransport/ResumableUpdatePackageDownloader.cs`를 그대로 링크한 독립 .NET 8 감사 실행기를 Release로 빌드했다(경고 0/오류 0). 서버 쓰기 없이 공개 HTTPS 다운로드만 사용했다.

| 시험 | 관찰 및 결과 |
| --- | --- |
| 다운로드 중 취소 | 2,101,215바이트 수신 후 취소. 최종 APK 없음, 부분 파일 유지. 해당 접두부 해시가 최종 원본과 일치 |
| 새 프로세스에서 이어받기 | 다른 PID에서 `Range: bytes=2101215-` 전송. 실제 서버 206 및 정확한 Content-Range 확인. 전체 크기·SHA-256 일치 후 최종 파일 확정 |
| 검증된 파일 재사용 | HTTP 전송 함수를 호출하면 실패하도록 둔 재호출에서 네트워크 없이 같은 파일 반환 |
| 손상된 접두부 | 시험 부분 파일의 1바이트를 변경한 뒤 실제 서버의 206 응답으로 이어받음. 최종 SHA-256 불일치로 거부, 손상 부분 파일 제거, 기존 최종 파일 역할의 시험 표식 보존 |
| 전송 완료 후 확정 전 상태 | 정상 APK 전체를 부분 파일로 두고 요청. 실제 서버 416·`Content-Range: bytes */42928348` 수신 후 전체 해시 검증을 거쳐 확정 |

각 시험 후 대상 잠금 수는 0이다. 단계별 결과·실제 요청 범위/응답 상태·PID를 별도 기록했다. 첫 분석 도구 호출은 존재하지 않는 `cmdline-tools/latest` 경로 때문에 실패했고, 설치된 `11.0` 경로로 수정해 성공했다. 제품 또는 운영 장애로 해석하지 않는다.

이 검사는 Windows에서 실행한 공용 전송 코드의 실제 서버 상호작용이다. Android 네트워크 스택·모바일 클라이언트 식별 헤더·설치 권한 화면·PackageManager 설치·DB 마이그레이션을 포함하지 않는다. 취소 후 정상 프로세스 종료와 재시작을 확인했으며 갑작스러운 전원 차단을 시험한 것은 아니다.

## 서명과 공개 APK 설정

- 공개 APK v1/v2/v3 서명 검증 성공, 서명자 1명.
- 인증서: `CN=Android Debug, O=Android, C=US`.
- 인증서 SHA-256: `dfc2e3680116ebe4291c466ba7da9491a2ecdf8502323ffafefc155e0c45dc28`.
- 공개 Manifest: `allowBackup=false`, `usesCleartextTraffic=false`, minSdk21/targetSdk34. `debuggable` 속성은 명시돼 있지 않다. Debug 인증서라는 사실과 실행 파일의 디버그 가능 여부를 혼동하지 않는다.
- 파일 provider들은 exported=false이며, profile installer receiver에는 `android.permission.DUMP`가 지정돼 있다.

정식 서명 전환은 기존 설치 앱의 업데이트와 데이터 보존을 함께 검토해야 한다. 이번에 서명키를 생성·변경하거나 앱 삭제·재설치를 수행하지 않았다. 서명 인증서 일치만으로 정식 배포 준비 완료를 주장하지 않는다.

## 영향과 남은 작업

제품 소스·앱 버전·배포 매니페스트·운영 DB·기존 설치 앱은 변경하지 않았다. 사용자 수락 후 현재 수정본의 정식 Release 빌드, 서명 정책 확정, 실제 Android 제자리 업데이트 및 업무 자료 보존 검증이 남아 있다. 실기기 검증, 원본 업무 충돌 해결, 독립 호스트 DB 복원, 워크플랜 잔여 항목도 별도 미완료다.

감사 중 생성한 APK 사본과 시험 표식만 해시·경로를 확인해 정리했다. 원본 PC DB·공식 시험 DB·앱 파일·Git 인덱스 등 기존 보호 대상 9개의 해시 보존을 확인했다. stage/commit/push 또는 운영 배포는 수행하지 않았으며 전체 Goal은 미완료다.

## 증거

`C:\Users\beene\Documents\Codex\tradeplan-android-published-continuity-20260923`

- continuity.log, continuity-exit.json: 원본 서명 연속성 도구 결과
- manifest-*.json, downloaded-signature.txt, published-AndroidManifest.xml
- cancel/resume/negative/complete-partial-result.json 및 각 로그
- probe/: 제품 소스를 링크한 감사 실행기
- independent-verification.json, cleanup.json, protected-preservation.json
