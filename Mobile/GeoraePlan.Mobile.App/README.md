# 거래플랜 안드로이드 MAUI 앱

- 작성일: 2026-03-19
- 프로젝트 파일: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj`
- 앱 ID: `kr.georaeplan.mobile`

## 현재 포함 기능
- 로그인
- 홈
- 거래처 조회
- 품목 조회
- 전표 조회
- 전표 작성 초안
- 수금 입력 초안
- 동기화 상태 조회
- 서버 주소 설정

## 현재 상태
안드로이드 앱 구조와 기본 업무 흐름 초안은 들어가 있습니다.

또한 **2026-03-19 기준 이 PC에서 로컬 Android 빌드환경 구성과 서명 APK 생성까지 완료**했습니다.

현재 생성된 대표 APK:
- `D:\거래플랜\Mobile\artifacts\android\거래플랜-안드로이드-v0.1.0-signed.apk`

## 관련 문서
- 빌드/서명/직접설치 가이드: `D:\거래플랜\Mobile\안드로이드_빌드_서명_설치_가이드_2026-03-19.md`
- 기능 명세: `D:\거래플랜\tasks\안드로이드_MVP_기능명세_2026-03-19.md`

## 관련 스크립트
- 빌드환경 부트스트랩: `D:\거래플랜\tools\mobile\Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1`
- 환경 점검: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1`
- keystore 생성: `D:\거래플랜\tools\mobile\New-GeoraePlanAndroidKeystore.ps1`
- 서명 APK 빌드: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1`

## 서명 설정 예시
- 예시 파일: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.example.json`
- 실제 로컬 파일 권장명: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json`

## 목표 배포 방식
- NAS 서버 공용 사용
- Windows PC + Android 앱 공용 사용
- 스토어 미등록
- 서명된 APK 직접 전달 및 직접 설치
