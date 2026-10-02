# 거래플랜

- 문서 기준시점: 2026-07-02 (main 소스 기준)
- 반영 범위: 커밋 이력 + main `8c6fd4670b7b5fec9db0326403fd1612fa4052fe` 기준 구현 상태
- 상태 태그: `[완료]`, `[작업중]`, `[검증필요]`, `[보류]`

## 프로젝트 개요
- 오프라인 우선 Windows ERP
- 기술 스택: .NET 8 WPF(MVVM), SQLite, ASP.NET Core API, ClosedXML
- 목표: 전표/거래처/인쇄/집계 업무를 레거시 흐름과 호환되게 안정 운영

## 현재 버전 상태
- 데스크톱 소스 버전: `1.1.647` / `FileVersion 1.1.647.0` (`Desktop/거래플랜.Desktop.App/거래플랜.Desktop.App.csproj`)
- 최신 커밋은 소스 버전을 `1.1.646`에서 `1.1.647`로 올림. 아래 구현 상태와 실제 설치본/live 배포 상태는 별도로 확인해야 함.
### 구현 반영 완료 (소스 기준)
- `[완료]` 판매/거래처/수금 기본 업무 흐름
- `[완료]` 거래명세서/세금계산서/견적서/대금청구서 미리보기 + 인쇄
- `[완료]` 로그인 아이디/비밀번호 저장 옵션 + 오프라인 로그인 fallback
- `[완료]` 자료 기간별 집계(5종: 판매+구매, 판매/매출, 구매/매입, 수금/지불, 연수구 납품내역) 엑셀 생성/저장/자동 열기
- `[완료]` 환경설정 운영 화면(회사정보, 선택값 관리, 담당지점 관리, 사용자 관리)
- `[완료]` 지점별 재고 조회 + 내부 재고이동 기본 흐름
- `[완료]` 시작/종료 동기화 및 자동 저장 기본 흐름
- `[완료]` WPF 기본 인쇄 경로로 단일화
- `[완료]` 로컬 확장 마스터(담당지점/창고/선택값)와 재고이동의 서버 동기화 범위 확대 및 핵심 회귀 검증 (`TODO_PHASE2.md`, 2026-06-27 기록)
- `[완료]` 렌탈 대시보드/청구관리/자산·설치현황/설정 창의 비모달 표시와 화면 표시 후 로딩, 닫힘 후 메인 거래내역 새로고침
- `[완료]` 렌탈 청구 시작 후 청구관리 창을 유지한 채 연결 전표 열기, 수금/거래처 편집 창 비모달 표시 및 닫힘 후 새로고침
- 렌탈 자산 연결 선택창은 모달을 유지하며, 후보 로딩은 화면 표시 후 시작하고 닫힐 때 취소함.

### 개발 중
- `[작업중]` 지점/권한 정책의 전 구간 검증: PC/서버 핵심 범위 회귀 및 Android 에뮬레이터 조회/저장/동기화/권한 거부 E2E는 기존 기록상 완료. 전체 업무 화면의 실제 수동 QA와 운영망/실기기 증거 누적은 남아 있음 (`TODO_PHASE2.md`)
- `[작업중]` 원가계층의 서버 동기화 범위 확장 (확장 마스터/재고이동 동기화 완료와 별도 범위)
- `[작업중]` FIFO 원가/시리얼/부분납품 운영 UX 고도화

### 검증 필요/보류
- `[검증필요]` 프린터별 여백 오차/직인 이미지 예외 케이스
- `[검증필요]` 자료기간별 집계 및 연수구 납품내역의 실데이터 정합성(누적/중복 제거)
- `[검증필요]` 장시간 운영 시 동기화/백업 알림 노이즈
- `[검증필요]` USENET/YEONSU/ITWORLD 계정별 운영 화면의 거래처/출고창고/재고이동 선택 제한, 실기기·운영망 장시간 실행·재로그인 증거
- `[검증필요]` 1.1.647 렌탈 비모달 전환의 실제 Windows UI 동작(창 병행 사용, 청구 전표 열기, 닫힘 후 새로고침) 및 설치본/live 버전 일치

## 실행 방법
### 권장 실행(로컬 빠른 확인)
```powershell
cd "D:\거래플랜"
cmd /c "배포\전체실행.cmd"
```

- 2026-03-20 실제 검증 결과:
  - `배포\전체실행.cmd` 는 로컬 테스트용으로 `Development` 환경 + SQLite fallback 기준에서 실행되도록 조정함
  - 서버는 `http://127.0.0.1:19080` 계열이 아니라, 스크립트가 잡은 실제 포트로 기동
  - 외부 live 실서버 확인은 `https://trade.2884.kr` 기준으로 별도 검증

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

### 배포본 직접 실행(가장 단순한 PC 테스트)
```powershell
cd "D:\거래플랜\배포\거래플랜"
.\거래플랜.exe
```

- 위 실행본은 배포 설정에 따라 `https://trade.2884.kr` live API를 바라보는 포터블 배포본이다.
- 로컬 서버 없이 Linux PC live API에 붙여서 UI/로그인 테스트할 때 가장 단순하다.

## 빌드/테스트
```powershell
cd "D:\거래플랜"
dotnet build "거래플랜.sln" -c Release
```

```powershell
cd "D:\거래플랜"
dotnet test "거래플랜.sln" -c Release --no-build
```

- 참고: 현재는 `Tests\GeoraePlan.Server.Api.Tests` 서버 자동 테스트, `Tests\GeoraePlan.Desktop.App.Tests` 데스크톱 회귀/소스 가드 테스트와 task 기반 스모크 검증이 포함되어 있어 `dotnet test` 는 최소 서버 회귀 검증까지 수행합니다.

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

## Linux PC 자동 배포(권장)
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

## 인쇄 기본 동작
- `[완료]` 판매(매출) 창에서 `출력물 편집` 후 데이터 저장
- `[완료]` `인쇄하기(F9)` 클릭 시 미리보기 창 우선 표시
- `[완료]` 미리보기에서 인쇄 클릭 시 거래플랜 전용 인쇄창 표시
- `[완료]` 전용 인쇄창에서 프린터 선택/새로고침/프린터 관리/PDF 저장/파일 저장(XPS) 제공
- `[완료]` 외부 PDF 자동 오픈 없이 앱 내부 미리보기 중심 동작
- 납품 PC의 프린터/복합기 상태 증거가 필요하면 `powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\verification\Test-GeoraePlanPrintEnvironment.ps1" -ProjectRoot "D:\거래플랜" -RequirePrinter -RequireOnlinePrinter -FailOnWarnings`로 기본 프린터, 오프라인 여부, PDF/XPS fallback source guard 리포트를 남깁니다.

## 자료 기간별 집계(엑셀)
- `[완료]` 지원 유형: 판매+구매, 판매/매출, 구매/매입, 수금/지불, 연수구 납품내역
- `[완료]` 저장 경로: `내문서\거래플랜\Exports` (또는 설정 경로)
- `[완료]` 파일명: `{From}~{To} 의 {원장종류} 거래원장_{yyyyMMdd_HHmmss}.xlsx`
- `[검증필요]` 일부 실운영 데이터셋에서 산식 검증 필요

## 변경 근거
### 최근 커밋
- `[완료]` 2026-07-02 `8c6fd467` 렌탈 창 비모달 로딩 개선 — 데스크톱 `1.1.647`, `MasterUiWiringGuardTests`에 비모달/청구관리 유지/후속 창 동작 가드 3건 추가
- `[완료]` 2026-07-02 `7c92acd1` 렌탈 청구기간 계산 기준 분리 — `RentalBillingRunStateTests` 회귀 추가
- `[완료]` 2026-07-02 `cbb7a536` 렌탈 조회 응답성과 거래처 정산 반영 개선 — 로딩 응답성/정산 반영 테스트 추가
- `[완료]` 2026-07-01 `bb665e19` 메인 로딩 성능 개선 및 모바일 연동 점검 — 메인 로딩 성능 테스트 추가
- `[완료]` 2026-06-27 `622529cb` 확장 마스터/재고이동 동기화 재검증 기록 — Desktop 28/28, Server 56/56 (기존 실행 기록이며 이번 문서 수정에서 재실행한 결과가 아님)

### 기존 변경 근거 (보존)
- `[완료]` 2026-03-12 `42a0d21` office-based settings and inventory management updates
- `[완료]` 2026-03-11 `ec50b37` 거래구분과 내부 재고이동 흐름 추가
- `[완료]` 2026-03-11 `b64ce1c` 지점 운영과 네이티브 인쇄 기반 정리
- `[완료]` 2026-03-05 `dc47549` docs update
- `[완료]` 2026-03-01 `ead6e68` period ledger aggregation/export

### 현재 리포지토리 상태
- `[완료]` 현재 tracked 소스 트리는 HEAD 기준으로 정리된 상태
- `[완료]` 현재 사용자 노출 브랜딩 문자열은 거래플랜 기준으로 정리됨

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
- 안드로이드 실사용 APK: `D:\거래플랜\배포\거래플랜-안드로이드-v0.2.4-signed.apk`
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
