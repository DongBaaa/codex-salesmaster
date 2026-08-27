# 거래플랜 안드로이드 MAUI 앱
- 문서 기준: 2026-07-28
- 프로젝트 파일: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj`
- 앱 ID: `kr.georaeplan.mobile`
- Android 현재 소스: `0.2.83`, versionCode `194`
- 공개 stable 표시 버전: `0.2.82`; APK 내부 versionCode는 매니페스트에 없으며 게시 연속성 게이트 증거로 별도 확인
- 연동 Windows 기준: 현재 소스 `1.1.697` / FileVersion `1.1.697.0` / 공개 stable `1.1.697`

Android 현재 소스 versionCode는 `194`입니다.
공개 APK의 내부 versionCode는 게시 연속성 게이트에서 검사하며, 새 후보는 그 값보다 큰 versionCode, Release signing, 기존 stable과 서명 연속성, emulator/실기기 `adb install -r` 검증이 필요합니다.

## 현재 포함 기능
- 로그인
- 홈
- 거래처 조회 / 입력
- 품목 조회 / 입력
- 전표 조회
- 판매 전표 작성
- 구매 전표 작성
- 수금/지급 입력(방식 선택, 서버 반영)
- 재고이동 조회(생성/수령/반려는 PC 품목/재고 관리에서 처리)
- 렌탈 조회(청구 생성/입금 등록/프로필·자산 수정은 PC 렌탈 청구관리에서 처리)
- 동기화 상태 조회
- 거래처 계약서 조회 / PDF 열기
- 휴지통 조회 / 복원 / 영구삭제
- 무결성 상태 조회

## 현재 운영 방향
- 모바일 앱은 **거래플랜 Linux PC 서버(`trade.2884.kr`, 실제 서버 본체: `itw@192.168.0.199:2222`의 `/srv/georaeplan`)에 고정 연결**됩니다.
- **서버 주소는 사용자 화면에 표시하지 않습니다.**
- PC와 같은 서버 데이터를 사용하도록 맞춘 상태입니다.
- 모바일 입력 가능 범위는 거래처/품목/판매·구매 전표/수금·지급입니다.
- 재고이동과 렌탈은 모바일에서 조회 전용으로 제공하며, 실제 생성·확정·수정 업무는 PC에서 처리합니다.

## PC에서 해야 하는 기능
- 사용자·역할·권한 관리
- 회사 설정과 업체 / 데이터 권한 관리
- 일반 백업 / 복원
- Excel 내보내기와 자료 기간별 집계
- 재고이동 생성 / 수령 / 반려
- 렌탈 청구 생성 / 입금 등록 / 청구 프로필·자산 수정

모바일에서 메뉴가 보이지 않는 위 기능을 누락이나 권한 오류로 오해하지 마세요. 현장 조회·입력에 필요한 범위만 제공하고, 관리·대량 처리·복구 기능은 PC에서 수행합니다.

## Android 산출물 기준
- 저장소에서 재현 가능한 공개 버전·파일명·SHA-256 기준: `D:\거래플랜\배포\stable.json`의 `android` 노드
- 게시 과정에서 생성되는 `D:\거래플랜\배포\업데이트\manifest\stable.json`과 live manifest는 별도 운영 산출물로 확인
- 2026-07-28 당시 배포 기록의 Android 표시 버전: `0.2.81`; 실제 APK 내부 versionCode는 게시 연속성 게이트에서 확인
- Android 공개 stable 표시 버전: `0.2.82`
- Android 현재 소스: `0.2.83`, versionCode `194`; 다음 정식 배포는 production signing과 공개 stable보다 높은 versionCode 연속성을 함께 검증해야 합니다.
- 서명 키·keystore 비밀번호·토큰은 README, 로그, Git에 기록하지 않습니다.

## 관련 문서
- 빌드/서명/직접설치 가이드: `D:\거래플랜\Mobile\안드로이드_빌드_서명_설치_가이드_2026-03-19.md`
- 기능 명세: `D:\거래플랜\tasks\안드로이드_MVP_기능명세_2026-03-19.md`

## 관련 스크립트
- 빌드환경 부트스트랩: `D:\거래플랜\tools\mobile\Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1`
- 환경 점검: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1`
- keystore 생성: `D:\거래플랜\tools\mobile\New-GeoraePlanAndroidKeystore.ps1`
- 서명 APK 빌드: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1`
- 서명 AAB 빌드: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidBundle.ps1`
- 서명 연속성 검증: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidSigningContinuity.ps1`
- 기존 설치본 제자리 업데이트 smoke: `D:\거래플랜\tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1`
- Android Studio 직접 테스트 실행: `D:\거래플랜\tools\mobile\Start-GeoraePlanAndroidStudioTest.ps1`

## 빌드 명령 예시
- APK:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"`
- AAB:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidBundle.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json"`
- APK+AAB 동시 생성:
  - `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" -PackageFormat both`
- 서명/AOT 실패 원인 진단이 필요할 때만 `-DetailedBuildLog`를 추가합니다. 이 옵션은 MSBuild `normal` 로그와 보호된 임시 비밀파일의 경로만 표시하며 비밀번호 값은 출력하지 않고, 비밀파일은 빌드 성공·실패와 관계없이 종료 시 삭제합니다.
- 운영 Release signing 설정에는 비밀번호 값 대신 `storePassEnvironmentVariable`, `keyPassEnvironmentVariable` 이름만 기록합니다. 기본 이름은 `GEORAEPLAN_ANDROID_STORE_PASSWORD`, `GEORAEPLAN_ANDROID_KEY_PASSWORD`이며 값은 현재 실행 프로세스의 보안 주입으로만 제공하고 명령행·JSON·로그·영구 사용자 환경변수에는 저장하지 않습니다.
- 운영 Release는 평문 `storePass`/`keyPass` 설정과 `-StorePass`/`-KeyPass` 인수를 fail-closed로 거부합니다. 기존 debug 서명은 명시적인 로컬 테스트/legacy 연속성 경로에서만 호환됩니다.

### 직접 `dotnet build` 할 때
- 기본 권장은 위 전용 빌드 스크립트 사용입니다.
- 현재 Windows PATH의 시스템 `dotnet`에 `maui-android` 워크로드가 없으면 `NETSDK1147`로 실패할 수 있습니다. 이 경우 아래처럼 거래플랜 전용 dotnet을 먼저 사용하세요.
- 전용 dotnet 후보:
  - `D:\거래플랜\.dotnet\dotnet.exe`
  - `%LOCALAPPDATA%\GeoraePlan.Android\dotnet8\dotnet.exe`
- 그래도 직접 빌드할 때는 프로젝트가 `ANDROID_SDK_ROOT`, `ANDROID_HOME`, `%LOCALAPPDATA%\GeoraePlan.Android\android-sdk`를 감지합니다. Java는 .NET 8 Android API 34 빌드와 경고 없이 맞는 **Microsoft OpenJDK 17**을 사용하며, `GEORAEPLAN_ANDROID_JAVA_SDK`, `D:\DevCaches\georaeplan-android-jdk\microsoft-jdk-17.0.20`, `JAVA_HOME` 순서로 확인합니다. Android Studio JBR 21은 Java 8 소스/대상 옵션의 obsolete 경고가 발생하므로 납품 빌드에 사용하지 않습니다.
- 현재 고정한 Microsoft OpenJDK 17.0.20 Windows x64 ZIP의 SHA-256은 `E46FD292317C6BB0A8FE9DC63115021329F3A63CAEBA791C185F89F3666A68E5`입니다.
- `XA5300: Android SDK 디렉터리를 찾을 수 없습니다`가 나오면 아래처럼 SDK/JDK 경로를 명시합니다.
- Release 직접 `build`는 Android AOT 응답파일 이슈로 실패할 수 있으므로 납품 APK/AAB 생성은 `Build-GeoraePlanAndroidApk.ps1`/`Build-GeoraePlanAndroidBundle.ps1`를 사용하세요. 해당 스크립트는 알려진 AOT 응답파일 오류가 나면 AOT 비활성 재시도를 수행합니다.

```powershell
$mobileDotnet = if (Test-Path "D:\거래플랜\.dotnet\dotnet.exe") { "D:\거래플랜\.dotnet\dotnet.exe" } else { "$env:LOCALAPPDATA\GeoraePlan.Android\dotnet8\dotnet.exe" }
$env:GEORAEPLAN_ANDROID_JAVA_SDK = "D:\DevCaches\georaeplan-android-jdk\microsoft-jdk-17.0.20"
& $mobileDotnet build "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj" -f net8.0-android -c Debug -p:AndroidSdkDirectory="$env:LOCALAPPDATA\GeoraePlan.Android\android-sdk" -p:JavaSdkDirectory="$env:GEORAEPLAN_ANDROID_JAVA_SDK"
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
- 새 APK는 공개 stable보다 큰 versionCode, 동일 applicationId, 동일 signing certificate, 실제 파일 SHA-256과 manifest 일치를 모두 통과해야 합니다.
- update-in-place는 기존 앱을 삭제하거나 데이터를 지우지 않고 정확히 한 번의 `adb install -r`로 검증합니다.
- `adb install -d`, uninstall, clear, device-wide cache 정리로 실패를 우회하지 않습니다.
- 테스트판은 `D:\거래플랜\테스트 시행`의 분리 환경에서 먼저 검증합니다.
- 사용자 테스트판 승인 전에는 Android live 버전 공개, stable manifest 변경, Git commit/push를 수행하지 않습니다.
