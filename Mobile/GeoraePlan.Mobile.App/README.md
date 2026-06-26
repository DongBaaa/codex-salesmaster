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
- 판매 전표 작성
- 구매 전표 작성
- 수금/지급 입력(방식 선택, 서버 반영)
- 재고이동 조회(생성/수령/반려는 PC 품목/재고 관리에서 처리)
- 렌탈 조회(청구 생성/입금 등록/프로필·자산 수정은 PC 렌탈 청구관리에서 처리)
- 동기화 상태 조회
- 거래처 계약서 조회 / PDF 열기
- 휴지통 조회 / 복원 / 영구삭제

## 현재 운영 방향
- 모바일 앱은 **거래플랜 Linux PC 서버(`trade.2884.kr`, 실제 서버 본체: `itw@192.168.0.199:2222`의 `/srv/georaeplan`)에 고정 연결**됩니다.
- **서버 주소는 사용자 화면에 표시하지 않습니다.**
- PC와 같은 서버 데이터를 사용하도록 맞춘 상태입니다.
- 모바일 입력 가능 범위는 거래처/품목/판매·구매 전표/수금·지급입니다.
- 재고이동과 렌탈은 모바일에서 조회 전용으로 제공하며, 실제 생성·확정·수정 업무는 PC에서 처리합니다.

## 최신 Android 산출물
- 최신 테스트 서명 APK:
  - `D:\거래플랜\Mobile\artifacts\android\publish_20260516_040127\kr.georaeplan.mobile-Signed.apk`
  - SHA256: `D:\거래플랜\Mobile\artifacts\android\publish_20260516_040127\kr.georaeplan.mobile-Signed.apk.sha256.txt`
  - 배포 폴더 복사본: `D:\거래플랜\배포\거래플랜-안드로이드-v0.2.12-signed.apk`
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

### 직접 `dotnet build` 할 때
- 기본 권장은 위 전용 빌드 스크립트 사용입니다.
- 그래도 직접 빌드할 때는 프로젝트가 `ANDROID_SDK_ROOT`, `ANDROID_HOME`, `%LOCALAPPDATA%\GeoraePlan.Android\android-sdk`, `JAVA_HOME`, Android Studio JBR 경로를 순서대로 자동 감지합니다.
- `XA5300: Android SDK 디렉터리를 찾을 수 없습니다`가 나오면 아래처럼 SDK/JDK 경로를 명시합니다.

```powershell
dotnet build "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj" -f net8.0-android -c Debug -p:AndroidSdkDirectory="$env:LOCALAPPDATA\GeoraePlan.Android\android-sdk" -p:JavaSdkDirectory="C:\Program Files\Android\Android Studio\jbr"
```

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
- Linux PC 거래플랜 서버 공용 사용
- Windows PC + Android 앱 공용 사용
- 스토어 미등록
- 서명된 APK 직접 전달 / 직접 설치
