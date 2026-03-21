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
- 거래처 계약서 조회 / PDF 열기
- 휴지통 조회 / 복원 / 영구삭제

## 현재 운영 방향
- 모바일 앱은 **거래플랜 NAS 서버에 고정 연결**됩니다.
- **서버 주소는 사용자 화면에 표시하지 않습니다.**
- PC와 같은 서버 데이터를 사용하도록 맞춘 상태입니다.

## 최신 Android 산출물
- 최신 서명 APK 예:
  - `D:\거래플랜\Mobile\artifacts\android\publish_20260320_141634\kr.georaeplan.mobile-Signed.apk`
  - SHA256: `D:\거래플랜\Mobile\artifacts\android\publish_20260320_141634\kr.georaeplan.mobile-Signed.apk.sha256.txt`
- 최신 서명 AAB 예:
  - `D:\거래플랜\Mobile\artifacts\android\aab_20260320_141421\kr.georaeplan.mobile-Signed.aab`
  - SHA256: `D:\거래플랜\Mobile\artifacts\android\aab_20260320_141421\kr.georaeplan.mobile-Signed.aab.sha256.txt`

## 관련 문서
- 빌드/서명/직접설치 가이드: `D:\거래플랜\Mobile\안드로이드_빌드_서명_설치_가이드_2026-03-19.md`
- 기능 명세: `D:\거래플랜\tasks\안드로이드_MVP_기능명세_2026-03-19.md`

## 관련 스크립트
- 빌드환경 부트스트랩: `D:\거래플랜\tools\mobile\Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1`
- 환경 점검: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1`
- keystore 생성: `D:\거래플랜\tools\mobile\New-GeoraePlanAndroidKeystore.ps1`
- 서명 APK 빌드: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1`
- 서명 AAB 빌드: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidBundle.ps1`
- Android Studio 직접 테스트 실행: `D:\거래플랜\tools\mobile\Start-GeoraePlanAndroidStudioTest.ps1`

## 빌드 명령 예시
- APK:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"`
- AAB:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidBundle.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"`
- APK+AAB 동시 생성:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" -PackageFormat both`

## Android Studio로 직접 확인하는 방법
- 이 프로젝트는 **.NET MAUI Android 앱**이라서 Android Studio가 앱을 직접 Gradle 빌드하는 구조는 아닙니다.
- 대신 Android Studio를 **에뮬레이터(Device Manager) / Logcat / 장치 확인** 용도로 쓰고, APK 빌드/설치는 거래플랜 스크립트가 자동으로 처리합니다.

### 가장 쉬운 방법
- `D:\거래플랜\배포\안드로이드스튜디오-테스트.cmd` 더블클릭

동작:
1. Android Studio 실행
2. Android Studio SDK 기준 에뮬레이터 확인/부팅
3. 최신 APK 빌드
4. 에뮬레이터에 APK 설치
5. 거래플랜 앱 자동 실행

### PowerShell 직접 실행
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Start-GeoraePlanAndroidStudioTest.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"
```

### 빠르게 재설치만 할 때
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Start-GeoraePlanAndroidStudioTest.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" -SkipBuild
```

## 배포 방식
- NAS 서버 공용 사용
- Windows PC + Android 앱 공용 사용
- 스토어 미등록
- 서명된 APK 직접 전달 / 직접 설치
