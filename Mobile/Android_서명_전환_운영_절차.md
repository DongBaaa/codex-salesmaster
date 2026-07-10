# Android 서명 전환 운영 절차

## 현재 기준

- 현재 stable Android APK는 기존 설치본과의 업데이트 연속성을 위해 Android debug 인증서로 서명되어 있다.
- 운영 점검에서 이 경고만 수용할 때는 `-AcceptLegacyAndroidDebugSigningWarning`을 명시한다.
- 이 스위치는 인증서 경고를 숨기지 않는다. 점검 보고서에 인증서 DN과 SHA-256을 `ACCEPTED`로 기록하며, 다른 운영 경고는 계속 실패 처리할 수 있다.
- 신규 유료 배포나 신규 설치 체계에는 debug 인증서를 사용하지 않는다.

## 기존 업데이트 체인 유지 배포

서버 배포 또는 기존 Android 설치본용 full release에서 다음 조건을 함께 지킨다.

1. `-FailOnOperationalWarnings`를 유지한다.
2. 현재 인증서 경고 1건만 수용하려면 Linux 배포 스크립트에 `-AcceptLegacyAndroidDebugSigningWarning`을 전달한다.
3. full release에서 `-AllowLegacyAndroidDebugSigning`을 사용하면 위 수용 값이 Linux 운영 게이트에도 자동 전달된다.
4. `-AcceptAndroidSigningCertificateChange`는 인증서가 실제로 바뀌는 별도 전환 작업에서만 사용한다.

## 릴리스 인증서로 전환하기 전 필수 결정

릴리스 keystore 생성 또는 인증서 교체 전에 다음 항목을 사용자와 확정한다.

1. keystore 원본과 백업의 보관 책임자
2. store/key 비밀번호의 보관 위치와 복구 절차
3. 기존 debug 서명 앱 삭제 후 재설치가 필요한 사용자 범위와 안내 일정
4. 로컬 데이터·dirty 동기화 완료 여부 및 재설치 전 백업 방법
5. 에뮬레이터와 실제 기기에서 품목, 거래처→전표작성, 동기화 흐름 검증
6. 새 인증서 SHA-256의 승인 및 운영 manifest 연속성 검증

인증서가 달라지면 Android는 기존 앱 위에 업데이트 설치를 허용하지 않는다. 따라서 위 결정과 테스트 없이 새 keystore를 생성하거나 stable APK 인증서를 교체하지 않는다.

## 전환 실행 순서

1. dirty 데이터 동기화 및 로컬 데이터 백업
2. 릴리스 keystore 생성 후 오프라인 이중 백업
3. signing 설정을 릴리스 keystore로 변경
4. 새 버전 APK 로컬 빌드
5. `apksigner verify --print-certs`로 DN·SHA-256 기록
6. 에뮬레이터 신규 설치 검증
7. 실제 기기에서 기존 앱 제거·재설치와 로그인·핵심 흐름 검증
8. 사용자의 `이상 없다` 확인
9. 모바일 버전 증가, stable 자산 생성, 운영 배포
10. manifest·APK 다운로드·인증서·로그 즉시 검증

## 금지 사항

- keystore 또는 비밀번호를 Git에 커밋하지 않는다.
- 인증서 변경을 단순 경고 수용 스위치로 우회하지 않는다.
- 실제 기기 검증과 사용자 확인 전에 모바일 버전을 올리거나 stable APK를 교체하지 않는다.
- 전체 운영 경고 실패 처리를 끄는 방식으로 debug 서명 경고만 회피하지 않는다.
