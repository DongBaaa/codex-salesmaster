# 거래플랜

- 문서 기준시점: 2026-08-27
- 반영 범위: 현재 작업트리의 소스·테스트 상태와 `trade.2884.kr` 공개 stable manifest를 분리해 기록
- 상태 태그: `[공개]`, `[로컬검증]`, `[검증필요]`, `[승인대기]`
- 상세 진행 상태와 검증 증거: `D:\거래플랜\tasks\거래플랜-전체-품질화-Goal-현황.md`

## 프로젝트 개요
- 오프라인 우선 Windows ERP
- 기술 스택: .NET 8 WPF(MVVM), SQLite, ASP.NET Core API, ClosedXML
- 목표: 전표/거래처/인쇄/집계 업무를 레거시 흐름과 호환되게 안정 운영

## 현재 버전 상태
### 공개 stable
- `[공개]` Windows PC `1.1.697`
- `[공개]` Android 표시 버전은 `0.2.82`입니다. APK 내부 versionCode는 공개 매니페스트에 없으므로 공개값으로 단정하지 않습니다.
- `[공개]` 2026-08-27 확인에서 `trade.2884.kr/healthz`와 stable manifest는 HTTP 200·redirect 0이며 `fileDeletionLeaseProtocol=shared-flock-v1`입니다.
- 저장소에서 재현 가능한 공개 버전·파일명·SHA-256 기준은 `D:\거래플랜\배포\stable.json`입니다. `배포\업데이트\manifest\stable.json`은 게시 과정의 로컬 산출물이며 live manifest와는 별도로 확인합니다.

### 현재 소스·테스트판
- `[공개/로컬검증]` Windows PC 소스 `1.1.697`, FileVersion `1.1.697.0`은 공개 stable과 일치합니다.
- `[로컬검증]` Android 소스 `0.2.83`, versionCode `194`
- `[로컬검증]` 서버 전체 1,478건 통과, PostgreSQL 전용 20건 건너뜀, 실패 0
- `[로컬검증]` 별도 ephemeral PostgreSQL 업무 회귀 22/22, 데스크톱 전체 3,568/3,568 통과
- `[로컬검증]` 격리 `Run-All.cmd`, 우선 업무 창 7/7, Multi-PC 24/24, 제한 계정 허용 11/11·차단 2/2 통과
- `[로컬검증]` WPF 36개 창 768/768, Windows native/앱 프린터 목록 11/11 exact, Android 실제 에뮬레이터 18개 화면 1,044/1,044 통과
- 현재 소스 `1.1.697`의 정식 패키지 생성과 Linux PC live 반영을 완료했습니다. 공개 stable ZIP SHA-256은 `8A0BF3F3918A39C80BD9CEDA81380BE2BC0E4EBF9DEDB76AC5FE3056BB858BA0`입니다.
- 버전 게시와 Git stage/commit/push는 수행했습니다. Windows Authenticode 공개 신뢰 서명과 실제 기기 설치는 수행하지 않았습니다.

### 승인·실사용 검증 대기
- `[승인대기]` Windows Authenticode/RFC3161 정식 서명과 기존 설치본 덮어쓰기·롤백 설치
- `[승인대기]` 현재 Android 후보의 버전 게시, production Release signing, 기존 설치본 update-in-place, 실제 기기 설치·회전·권한·ANR 확인
- `[로컬검증]` Linux PC 거래플랜 전용 자동 백업 schedule 설치·활성화와 complete set 상태는 확인했으며, 외부 replica restore drill은 승인·실행 증거가 필요합니다.
- `[로컬검증/실사용확인대기]` PDF 생성·Excel 왕복·계정 scope·100~200% WPF 렌더링 감사는 통과했습니다. 거래플랜 화면에서의 실제 종이 출력 결과는 사용자 확인이 필요합니다.
- `[공개]` 현재 Goal 변경의 선택 Git stage/commit/push와 원격 SHA 확인을 완료했습니다.
- `[공개]` HSTS는 `max-age=15768000; includeSubdomains; preload` 단일 값으로 확인했습니다.
- `[공개]` PC 패키지는 `Accept-Ranges: bytes`, 유효 Range `206`, 범위 초과 `416`을 반환합니다.

## Windows PC와 Android 기능 경계

### Android 지원
- 로그인, 홈, 거래처·품목 조회/입력, 판매·구매 전표, 수금·지급, 계약서 PDF, 동기화, 휴지통
- 재고이동과 렌탈은 조회 중심

### PC 전용 / Android 미지원
- 사용자·역할·권한 관리
- 일반 백업·복원
- Excel 내보내기와 자료 기간별 집계
- 재고이동 생성·수령·반려
- 렌탈 청구 생성·입금 등록·청구 프로필과 자산 수정
- 회사 설정과 업체 / 데이터 권한 관리

## 실행 방법
### 권장: 분리된 테스트 시행 환경

- 일반 수동 실행은 `D:\거래플랜\테스트 시행\실행환경\Launch-Test-App.vbs`를 더블클릭합니다. 테스트 서버와 앱은 CMD 창 없이 시작됩니다.
- 자동화·진단·종료 코드 수집은 아래처럼 `Run-All.cmd`를 동기 실행합니다. 이 경우 호출한 터미널 창은 의도적으로 유지됩니다.

```powershell
cd "D:\거래플랜"
cmd /c "테스트 시행\실행환경\Run-All.cmd"
```

- 모든 데스크톱·서버 변경은 `D:\거래플랜\테스트 시행`의 분리된 데이터와 포트에서 먼저 검증합니다.
- 일반 사용자는 `Launch-Test-App.vbs`, 자동 검증은 `Run-All.cmd`를 사용합니다. `Run-App.cmd`는 호환 wrapper이고 `Run-Server.cmd`는 구성요소 진단용이므로 일반 실행에 사용하지 않습니다.
- 앱 창이 실제로 열리고 테스트 서버 health/ready가 통과한 뒤 주요 업무 흐름을 확인합니다.
- 격리 테스트 런타임은 원격 세션·그래픽 드라이버의 WPF 하드웨어 합성 오류가 화면 검증을 가리지 않도록 소프트웨어 렌더링을 사용하며, 일반 개발·운영 실행의 렌더링 방식은 바꾸지 않습니다.
- 테스트판 승인 전에는 아래 live 배포 명령을 실행하지 않습니다.

### 개발 모드 실행
서버:
```powershell
cd "D:\거래플랜\Server\거래플랜.Server.Api"
dotnet run
```

데스크톱:
```powershell
cd "D:\거래플랜\Desktop\거래플랜.Desktop.App"
dotnet run
```

### 공개 배포본 읽기 전용 확인
```powershell
cd "D:\거래플랜\배포\거래플랜"
.\거래플랜.exe
```

- 위 실행본은 배포 설정에 따라 `https://trade.2884.kr` live API를 바라보는 포터블 배포본이다.
- 운영 데이터를 생성·수정·삭제하는 테스트에는 사용하지 않습니다.

## 빌드/테스트
```powershell
cd "D:\거래플랜"
dotnet build "거래플랜.sln" -c Release
```

```powershell
cd "D:\거래플랜"
dotnet test "거래플랜.sln" -c Release --no-build
```

- 서버와 데스크톱 테스트 프로젝트를 각각 실행하고, PostgreSQL 전용 회귀는 `D:\거래플랜\tools\verification\Invoke-GeoraePlanEphemeralPostgreSqlTests.ps1`로 실제 임시 DB에서 확인합니다.
- 정적 검사나 자동 테스트는 실제 WPF 화면, 프린터, Android emulator/실기기 검증을 대체하지 않습니다.

## Linux PC 주기 점검 / 백업 / 인증서 갱신
- 현재 거래플랜 서버 본체는 Linux PC `itw@192.168.0.199:2222`의 `/srv/georaeplan` 기준으로 운영합니다.
- 운영 공개 URL:
  - https://trade.2884.kr/healthz
  - https://trade.2884.kr/updates/manifest?channel=stable
- live 반영 전후 공통 Linux PC/네트워크 인프라 영향 여부를 조기에 확인하기 위해 함께 확인할 URL:
  - https://work.2884.kr/healthz
  - https://itw.2884.kr/
- Linux PC 상태 파일/로그 기준 경로:
  - `/srv/georaeplan/ops/state/daily-check-status.txt`
  - `/srv/georaeplan/ops/state/weekly-check-status.txt`
  - `/srv/georaeplan/ops/state/backup-status.txt`
  - `/srv/georaeplan/ops/state/external-replica-status.txt`
  - `/srv/georaeplan/ops/state/cert-status.txt`
  - `/srv/georaeplan/ops/state/routine-ops.log`
  - DB 백업 폴더: `/srv/georaeplan/backups/db`
  - 파일 백업 폴더: `/srv/georaeplan/backups/files`
- 2026-08-12 `georaeplan-backup.timer`를 Linux PC에 설치해 enabled/active 상태로 확인했고, 실제 1회 실행에서 중앙·업체 DB dump와 파일·Data Protection key archive가 하나의 검증된 complete set으로 게시됐습니다.

## 사용자 승인 후 Linux PC 배포
PC 설치파일, Android APK, 업데이트 자산 생성 후 Linux PC에 **release 업로드 + `apply-release.sh` 실행 + 거래플랜 서비스 단위 반영**까지 한 번에 처리하려면 아래 명령을 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\release\Publish-GeoraePlanFullRelease.ps1" `
  -ProjectRoot "D:\거래플랜" `
  -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" `
  -DeployToLinuxPc `
  -FailOnOperationalWarnings
```

서버 publish/live 반영만 다시 할 때는 아래 Linux PC 전용 래퍼를 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\linux\Publish-GeoraeplanLinuxPcRelease.ps1" `
  -ProjectRoot "D:\거래플랜" `
  -MirrorToLive `
  -FailOnOperationalWarnings
```

사전 조건:
- Windows 배포 PC에 `C:\Users\beene\.ssh\itwserver_codex_ed25519` 키가 있어야 합니다.
- Linux PC의 `/srv/georaeplan/ops/apply-release.sh`가 존재하고 `bash -n` 검사를 통과해야 합니다.
- 새 작업에서는 `tools\\linux` 스크립트만 사용합니다.
- 유료 납품/엄격 release에서는 operational warning을 배포 차단으로 보기 위해 `-FailOnOperationalWarnings`를 유지합니다.
- Android APK를 live에 반영할 때는 현재 live APK와 새 APK의 signing certificate SHA-256이 자동 비교됩니다. 값이 바뀌면 기존 설치본은 제자리 업데이트가 불가능하므로, 재설치/전환 계획이 검증된 경우에만 `-AcceptAndroidSigningCertificateChange`를 명시합니다.
- 사용자 PC 로컬 캐시까지 납품 증거에 포함할 때는 `-LocalCacheAppDataRoot "<사용자 AppData 루트>" -RequireLocalCacheConsistencyCheck`를 추가합니다. 이 옵션이 켜진 상태에서 로컬 캐시 점검이 skip되면 live 관찰/운영 게이트가 실패합니다.
- Android 기존 설치본 업데이트 검증이 필요하면 실기기/에뮬레이터에 기존 앱을 설치한 뒤 `D:\거래플랜\tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1 -ApkPath <새 APK> -RequireUpdateInPlace`를 실행합니다. 이 모드는 서명 불일치/버전 다운그레이드/업데이트 실패 시 삭제 후 재설치로 우회하지 않습니다.
- 납품 직전에는 `D:\거래플랜\tools\verification\Invoke-GeoraePlanPaidDeliveryGate.ps1 -Strict -LocalCacheAppDataRoot "<사용자 AppData 루트>" -AndroidApkPath "<새 APK>"`로 live 관찰, 로컬 캐시, 프린터, Android update-in-place 증거를 한 리포트로 묶어 확인합니다. `-Strict`는 하위 점검의 WARN도 전체 실패로 올려 숨은 경고가 PASS로 보이지 않게 합니다. Includes API visibility smoke for login/scope/core list/integrity evidence.
- 24시간 장시간 관찰은 `D:\거래플랜\tools\verification\Invoke-GeoraePlanSoakObservation.ps1 -ProjectRoot D:\거래플랜 -SampleCount 1440 -IntervalSeconds 60`으로 수행합니다. 이 도구는 `healthz`, update manifest, 로컬 거래플랜 프로세스 응답/메모리만 읽고 운영 데이터 생성·수정·삭제 API는 호출하지 않습니다.
- 장시간 관찰 PASS를 최종 납품 게이트에서 필수로 만들려면 `Invoke-GeoraePlanPaidDeliveryGate.ps1`에 `-RequireSoakPass -SoakEvidencePath "<soak-observation.md>"`를 추가합니다. 기본 허용 증거 나이는 168시간이며 `-MaxSoakEvidenceAgeHours`로 조정할 수 있습니다.

## 인쇄 기본 동작
- `[완료]` 판매(매출) 창에서 `출력물 편집` 후 데이터 저장
- `[완료]` `인쇄하기(F9)` 클릭 시 미리보기 창 우선 표시
- `[완료]` 미리보기에서 인쇄 클릭 시 거래플랜 전용 인쇄창 표시
- `[완료]` 전용 인쇄창에서 Windows에 설치된 로컬·연결 프린터 전체 목록, 새로고침, 프린터 관리, PDF 저장, 파일 저장(XPS) 제공
- `[완료]` 모든 WPF 보조 창에 작업영역 기준 반응형 크기 제한과 스크롤 도달 경로를 적용하고, 1366x728 및 100~200% 배율 회귀로 주요 조작부 잘림을 방지
- `[완료]` 외부 PDF 자동 오픈 없이 앱 내부 미리보기 중심 동작
- 납품 PC의 프린터/복합기 상태 증거가 필요하면 `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\verification\Test-GeoraePlanPrintEnvironment.ps1" -ProjectRoot "D:\거래플랜" -RequirePrinter -RequireOnlinePrinter -FailOnWarnings`로 기본 프린터, 오프라인 여부, PDF/XPS fallback source guard 리포트를 남깁니다.

## 자료 기간별 집계(엑셀)
- `[완료]` 지원 유형: 판매+구매, 판매/매출, 구매/매입, 수금/지불, 연수구 납품내역
- `[완료]` 저장 경로: `내문서\거래플랜\Exports` (또는 설정 경로)
- `[완료]` 파일명: `{From}~{To} 의 {원장종류} 거래원장_{yyyyMMdd_HHmmss}.xlsx`
- `[검증필요]` 일부 실운영 데이터셋에서 산식 검증 필요

## 변경 근거
- 현재 장기 품질화 Goal의 기준 HEAD는 `b9f1b058ec121ff6135661ab57679a73b0f09c0b`입니다.
- 작업트리는 기존 사용자 변경과 Goal 변경을 함께 보존하는 dirty 상태입니다. 정리된 clean tree라고 가정하지 않습니다.
- 배치별 변경·검증·남은 위험은 `업데이트 내역.md`와 `tasks\거래플랜-전체-품질화-Goal-현황.md`를 기준으로 확인합니다.

## 관련 문서
- 통합 진행 문서: `D:\거래플랜\기획.md`
- Linux PC 운영 런북: `D:\거래플랜\infra\LinuxPC-운영-런북.md`
- Linux PC 설정 예시: `D:\거래플랜\infra\linux\.env.example`
- Linux PC compose 예시: `D:\거래플랜\infra\linux\docker-compose.yml`
- 안드로이드 MVP 기능명세: `D:\거래플랜\tasks\안드로이드_MVP_기능명세_2026-03-19.md`
- 안드로이드 MAUI 스캐폴드: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\README.md`
- 안드로이드 빌드/서명/직접설치 가이드: `D:\거래플랜\Mobile\안드로이드_빌드_서명_설치_가이드_2026-03-19.md`
- 안드로이드 빌드환경 부트스트랩 스크립트: `D:\거래플랜\tools\mobile\Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1`
- 안드로이드 환경 점검 스크립트: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1`
- 안드로이드 keystore 생성 스크립트: `D:\거래플랜\tools\mobile\New-GeoraePlanAndroidKeystore.ps1`
- 안드로이드 서명 APK 빌드 스크립트: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1`
- 안드로이드 live 서명 연속성 점검 스크립트: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidSigningContinuity.ps1`
- 안드로이드 공개 stable 저장소 기준: `D:\거래플랜\배포\stable.json`의 `android` 노드
- 사용자 메뉴얼 PDF 공식 생성: `D:\거래플랜\tools\manual\Build-GeoraePlanUserManualPdf.ps1`
- 안드로이드 스튜디오 직접 테스트 런처:
  - `D:\거래플랜\배포\안드로이드스튜디오-테스트.cmd`
- PC 설치 패키지 생성 스크립트: `D:\거래플랜\tools\release\Build-GeoraePlanDesktopInstaller.ps1`
- PC EXE/MSI 설치 패키지 생성 스크립트: `D:\거래플랜\tools\release\Build-GeoraePlanDesktopNativeInstallers.ps1`
- PC+모바일+업데이트 자산 통합 릴리스 스크립트: `D:\거래플랜\tools\release\Publish-GeoraePlanFullRelease.ps1`
- PC 실사용 설치 파일(권장):
  - `D:\거래플랜\배포\거래플랜-PC-설치패키지.exe`
- PC 관리자용 보관 파일:
  - `D:\거래플랜\배포\관리자용\거래플랜-PC-설치패키지.msi`
  - `D:\거래플랜\배포\관리자용\거래플랜-PC-설치패키지.zip`
- 수정/업데이트 가이드:
  - `D:\거래플랜\수정_업데이트_가이드_2026-03-20.md`
