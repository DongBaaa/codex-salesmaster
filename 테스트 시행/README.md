# 테스트 시행

거래플랜 수정은 앞으로 이 폴더 기준으로 **내 PC 로컬 테스트 서버 선검증 → 이상 없으면 메인(live) 버전을 Linux PC 반영 → Git 반영** 순서로 진행합니다.

## 지금 방식의 핵심
- 운영 Linux PC 서버와 **완전히 다른 로컬 테스트 서버**를 사용합니다.
- 운영용 로컬 앱 데이터 `%LOCALAPPDATA%\거래플랜` 을 테스트용으로 **복사한 뒤 분리**해서 사용합니다.
- 테스트 앱은 `D:\거래플랜\테스트 시행\실행환경\AppData` 만 사용합니다.
- 테스트 서버는 `D:\거래플랜\테스트 시행\실행환경\Server\거래플랜-local.db` 와 `D:\거래플랜\테스트 시행\실행환경\ServerData` 만 사용합니다.
- 즉, 테스트 중 저장/수정/동기화는 운영 Linux PC 데이터에 직접 닿지 않습니다.
- 테스트용 AppData 스냅샷과 MultiPC 복사본은 용량 증가를 줄이기 위해 backup / diagnostics / logs / temp 폴더를 빈 상태로 다시 만듭니다.

## 기본 흐름
1. `D:\거래플랜\테스트 시행\테스트-환경-준비.ps1` 실행
2. 현재 로컬 데이터 스냅샷을 테스트 앱 데이터로 복사
3. 복사된 테스트 앱 데이터를 이용해 **분리된 테스트 서버 DB**를 다시 시드
4. 생성된 `D:\거래플랜\테스트 시행\최근 수정 파일.md` 확인
5. 생성된 `D:\거래플랜\테스트 시행\검증 체크리스트.md` 기준으로 점검
6. 일반 수동 확인은 `D:\거래플랜\테스트 시행\실행환경\Launch-Test-App.vbs`를 더블클릭해 CMD 창 없이 테스트 서버+앱 실행
   - 자동화·진단·종료 코드 증거가 필요할 때만 `Run-All.cmd`를 동기 실행하며, 이 경우 호출한 터미널 창은 유지됩니다.
7. 수정사항 확인 완료 후 `D:\거래플랜\테스트 시행\검증완료-반영.cmd` 또는 `D:\거래플랜\테스트 시행\검증완료-반영.ps1` 로 **현재 소스 기준 메인(live) 버전**을 Linux PC/Git 반영

## 생성되는 항목
- `실행환경\App`
  - 현재 소스 기준 테스트용 데스크톱 앱 publish 결과
- `실행환경\Server`
  - 현재 소스 기준 테스트용 서버 publish 결과
  - 테스트 서버 SQLite: `실행환경\Server\거래플랜-local.db`
- `실행환경\AppData`
  - 운영 로컬 앱 데이터 복사본
  - 테스트 앱은 이 경로만 사용
- `실행환경\ServerData`
  - 테스트 서버 전용 파일 저장소/업데이트 저장소
- `최근 수정 파일.md`
  - 현재 Git 기준 수정/추가 파일 목록
- `검증 체크리스트.md`
  - 이번 테스트용 점검 체크리스트
- `기록\yyyyMMdd-HHmmss`
  - 준비 시점의 변경 파일 목록, 체크리스트, 준비 로그, 서버 시드 로그 보관
- `기록\deploy-yyyyMMdd-HHmmss`
  - 반영 시점의 체크리스트, 변경 파일 목록, 반영 로그, 반영 결과 보관
  - live 사전/사후 점검 리포트(`live-preflight.md`, `live-postflight.md`) 포함
- `실행환경\MultiPC`
  - PC-A / PC-B 분리 AppData로 같은 서버를 동시에 검증하는 실행 스크립트 세트
- `live 반영 롤백 절차.md`
  - live 반영 후 문제 시 이전 stable 버전/manifest/백업 기준으로 되돌릴 때 참고하는 운영 메모

## 권장 실행 예시
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1"
```

준비가 끝난 뒤 일반 수동 확인은 `실행환경\Launch-Test-App.vbs`를 더블클릭합니다. `Run-App.cmd`는 호환 wrapper이고 `Run-All.cmd`와 `Run-Server.cmd`는 자동화·진단용입니다.

### 준비 후 바로 실행까지 하고 싶으면
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1" -Launch
```

### 데이터 복사만 건너뛰고 기존 테스트 스냅샷을 유지하고 싶으면
```powershell
$snapshotPath = 'D:\DevCaches\georaeplan-v1-user-snapshots\source-users-<내보내기 시각>.json'
$snapshotSha256 = '<내보내기 기록에 남은 64자리 SHA-256>'
powershell -NoProfile -ExecutionPolicy Bypass `
  -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1" `
  -SkipDataCopy `
  -SourceUsersSnapshotPath $snapshotPath `
  -SourceUsersSnapshotSha256 $snapshotSha256
```
- bare `-SkipDataCopy`는 원격 Source API 사용을 암묵적으로 허용하지 않습니다.
  사용자 스냅샷 없이 설정된 Source API가 원격이면 출력 변경 전에 실패하며,
  원격 조회가 꼭 필요한 별도 작업에서만 `-AllowRemoteSourceApi`를 명시합니다.
- `-SkipDataCopy`는 기존 격리 `실행환경\AppData`를 삭제하거나 원본
  `%LOCALAPPDATA%\거래플랜`에서 다시 복사하지 않습니다. 기존
  `AppData\data\거래플랜.db`와 `.georaeplan-isolated-seed-root`가 모두
  정상이어야 하며, 없거나 경로가 일치하지 않거나 SQLite sidecar가 남아
  있으면 App/Server 교체 전에 실패합니다.
- 이 옵션도 App/Server 실행 파일과 격리 서버 DB는 새로 만들고, 보존한
  AppData를 다시 동기화·시드합니다. 보존 데이터에 dirty/충돌 문제가 있으면
  완성된 실행환경으로 인증하지 않습니다.
- `SkipDataCopy`의 보존 계약은 **복사 단계가 AppData를 삭제·대체하지
  않는다**는 뜻입니다. 서버 시드가 시작된 뒤에는 동기화 리비전, outbox,
  테스트 전용 비밀번호 상태 같은 격리 로컬 메타데이터가 정상적으로 갱신될
  수 있으므로 시드 이후 파일의 바이트 불변을 의미하지 않습니다. 출력 교체가
  시작된 뒤 시드/인증이 실패하면 `.georaeplan-runtime-invalid` 차단 마커를
  남깁니다. 기존 준비완료 마커가 다른 프로세스에 잠겨 삭제되지 않아도 모든
  launcher가 차단 마커를 먼저 거부하며, 새 인증이 완전히 성공한 경우에만
  차단 마커를 제거합니다.
- 보호된 사용자 스냅샷 파일을 함께 사용할 때 비밀번호 재설정을 지정하지
  않으면, 저장 로그인 사전검사는 원본 AppData가 아닌 보존된 격리 AppData에서만
  수행됩니다. 저장 로그인이 부족하면 출력 교체 전에 실패합니다.
- 원격 Source API를 사용하지 않으려면 검증된
  `-SourceUsersSnapshotPath`와 `-SourceUsersSnapshotSha256`을 함께
  지정합니다. 해결할 수 없는 사용자의 비밀번호를 테스트 서버에서만
  재설정할 때는 `-ResetUnresolvedUserPasswordsForIsolatedTest`를 명시해야
  합니다.

### 같은 서버를 두 개의 독립 PC 캐시로 검증하고 싶으면
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\준비-다중PC-검증.ps1" -ResetClientData
```
- 실행 후 `D:\거래플랜\테스트 시행\실행환경\MultiPC\Run-All-MultiPC.cmd` 를 사용합니다.
- 세부 시나리오는 `D:\거래플랜\테스트 시행\다중PC-검증 시나리오.md` 를 따릅니다.
- 기본 `Contract` 모드는 xUnit 계약 회귀만 실행합니다. 실제 PC-A/PC-B Desktop 프로세스나 분리 AppData 실행을 뜻하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\Invoke-MultiPcConflictCheck.ps1" -Mode Contract
```

- 실제 두 Desktop 프로세스 대표 거래처 경로는 새롭거나 빈 증거 폴더를 지정해 별도로 실행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\Invoke-MultiPcConflictCheck.ps1" -Mode DesktopE2E -DesktopE2EEvidenceDirectory "D:\거래플랜\테스트 시행\기록\<새-실행-ID>"
```

## 검증 완료 후 반영 실행

### 1클릭 실행
- 더블클릭: `D:\거래플랜\테스트 시행\검증완료-반영.cmd`
- 실행 시 체크리스트가 통과된 경우에만 **테스트 버전이 아닌 현재 소스 기준 메인(live) 버전**의 Linux PC/Git 반영을 진행합니다.

### PowerShell 직접 실행
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -FailOnOperationalWarnings
```
- 이 스크립트는 live 반영 전에 `Invoke-LiveReleaseReadinessCheck.ps1` 사전 점검을 수행하고,
  패키지/manifest/Linux PC 반영 뒤에는 사후 점검 리포트까지 남깁니다.
- 유료 납품/엄격 release에서는 `-FailOnOperationalWarnings`로 live 관찰/운영 게이트 warning도 배포 차단으로 처리합니다.
- Android APK가 live 업데이트 자산에 포함되면 새 APK와 현재 live APK의 signing certificate SHA-256을 비교합니다. 불일치 시 기존 설치본 업데이트가 불가능하므로 기본 차단되며, 재설치/전환 계획 검증 후에만 `-AcceptAndroidSigningCertificateChange`를 사용합니다.
- 실제 사용자 PC 로컬 캐시까지 증거로 남길 때는 `-LocalCacheAppDataRoot "<사용자 AppData 루트>" -RequireLocalCacheConsistencyCheck`를 같이 지정합니다. 이 옵션이 켜진 상태에서 로컬 캐시 점검이 skip되면 실패 처리됩니다.
- 현장 PC 프린터/복합기까지 납품 증거로 남길 때는 `D:\거래플랜\tools\verification\Test-GeoraePlanPrintEnvironment.ps1 -ProjectRoot D:\거래플랜 -RequirePrinter -RequireOnlinePrinter -FailOnWarnings`를 실행해 기본 프린터, 온라인 프린터, 전용 인쇄창 PDF/XPS 대체 저장 source guard를 함께 확인합니다.
- Android 실기기/에뮬레이터 업데이트 증거가 필요하면 기존 설치본이 있는 상태에서 `D:\거래플랜\tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1 -ApkPath <새 APK> -RequireUpdateInPlace`를 실행합니다. 이 옵션은 update-in-place 실패를 uninstall fallback으로 숨기지 않습니다.
- 위 항목을 한 번에 묶어 납품 리포트로 남길 때는 `D:\거래플랜\tools\verification\Invoke-GeoraePlanPaidDeliveryGate.ps1 -Strict -LocalCacheAppDataRoot "<사용자 AppData 루트>" -AndroidApkPath "<새 APK>"`를 사용합니다. `-Strict`에서는 하위 경고도 실패로 전파되어 경고가 상위 PASS로 숨지 않습니다. Includes API visibility smoke for login/scope/core list/integrity evidence.
- 장시간 실행 안정성은 `D:\거래플랜\tools\verification\Invoke-GeoraePlanSoakObservation.ps1 -ProjectRoot D:\거래플랜 -SampleCount 1440 -IntervalSeconds 60`으로 24시간 읽기 전용 관찰 증거를 남깁니다. 최종 납품 게이트에는 `-RequireSoakPass -SoakEvidencePath "<soak-observation.md>"`를 전달해 PASS 및 증거 유효기간을 검증합니다.

### 드라이런 확인
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -DryRun
```

### 운영 데이터 후보를 이번 반영 판단에서 제외해야 할 때
- 기본값은 live 전/후 운영 게이트와 렌탈 템플릿 품목 참조 게이트를 모두 실행하는 것입니다.
- 아래 옵션은 사용자가 운영 데이터 후보를 이번 배포 판단에서 제외한다고 명시했고, 별도 검증 근거가 있을 때만 사용합니다.
- `검증완료-반영.ps1` 래퍼에서도 하위 Linux PC 배포 스크립트까지 같은 옵션이 그대로 전달됩니다.
- 유료 납품/엄격 release에서는 아래 gate 생략 예시를 사용하지 말고, 위 기본 명령처럼 `-FailOnOperationalWarnings`를 사용합니다.
- `-SkipAndroidSigningContinuityCheck`는 Android APK 변경이 없거나 별도 서명 연속성/재설치 계획이 이미 검증된 경우가 아니면 사용하지 않습니다.
- `-RequireLocalCacheConsistencyCheck`는 `-LocalCacheAppDataRoot`와 함께 사용해야 하며, 실제 사용자 PC AppData를 확인할 수 없을 때는 “검증 미완료”로 보고해야 합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -AcceptRentalTemplateItemReferenceRisk -SkipPreDeployOperationalGate -SkipPostDeployOperationalGate
```

### 새 untracked 파일이 포함될 때
- 스크립트는 실수 방지를 위해 새 파일/폴더를 자동 Git 반영하지 않습니다.
- 필요한 새 파일이 있으면 `-IncludeUntrackedPaths` 로 명시해야 합니다.

예시:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -IncludeUntrackedPaths "테스트 시행"
```

## MainWindow DPI·저해상도 런타임 감사

실제 Desktop `MainWindow` BAML 트리를 100%·125%·150%·175%·200%, 실제 최소 창, 업데이트 표시 조합으로 렌더링하고 버튼 겹침·본문 최소 높이·스크롤 도달성을 확인할 때 사용합니다.

```powershell
dotnet run --project "D:\거래플랜\tasks\MainWindowDpiRuntimeAudit\MainWindowDpiRuntimeAudit.csproj" -- --output "D:\거래플랜\테스트 시행\기록\<새 증거 폴더>\runtime-audit"
```

- `--output`은 반드시 새 폴더 또는 빈 폴더여야 합니다. 기존 증거가 있는 폴더는 실패 처리됩니다.
- 결과 JSON/Markdown에는 제품 DLL·EXE·레이아웃 소스와 감사 Program/csproj/DLL/EXE의 SHA-256, MVID, freshness, Git HEAD/dirty 상태가 기록됩니다.
- 스크롤 폴백 프로필은 원점 PNG와 최대 가로·세로 offset의 `scroll-end` PNG를 함께 만들고, 필수 업무 버튼을 실제 `BringIntoView`한 뒤 viewport 진입과 원점 복귀를 검사합니다.
- 이 도구는 결정적 offscreen BAML 감사입니다. 실제 물리 모니터의 `SourceInitialized`/HWND 배치, `WM_DPICHANGED`, Popup·ContextMenu 입력을 대신하지 않습니다.

## 주의사항
- 일반 수동 실행은 CMD 창을 남기지 않는 `Launch-Test-App.vbs`를 사용하세요. `Run-All.cmd`는 자동화·진단·종료 코드 증거가 필요한 경우의 진입점이며 호출한 CMD/터미널 창은 유지됩니다. `Run-App.cmd`는 기존 호출 호환용이고 `Run-Server.cmd`는 구성요소 진단용입니다.
- `실행환경\App\거래플랜.Desktop.App.exe` 를 직접 실행하면 운영 로컬 데이터 경로를 사용할 수 있습니다.
- 테스트 준비를 다시 실행하면 테스트 앱 데이터와 테스트 서버 데이터는 최신 운영 로컬 스냅샷 기준으로 다시 만들어집니다.

## 반영 원칙
- 테스트 서버 확인 전에는 Linux PC 반영 금지
- 테스트 서버 확인 전에는 Git 반영 금지
- Linux PC에는 테스트 서버/테스트 앱이 아니라 **현재 소스에서 다시 생성한 stable/live 배포본만** 반영
- 데스크톱 앱 수정이 포함된 live 반영이면 `D:\거래플랜\Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj` 의 `Version` / `FileVersion` 을 현재 stable 배포본보다 높게 올린 뒤 반영
- 필수 업데이트로 배포할 경우 manifest 의 `minimumSupportedVersion` 이 함께 기록되어야 하며, 반영 스크립트가 이를 점검함
- 설치 패키지 안에는 `Updater\거래플랜.Updater.exe` 와 `appsettings.json` 이 모두 포함되어야 하며, 반영 스크립트가 이를 점검함
- 체크리스트의 `문제 없음 → Linux PC 반영 가능`, `문제 없음 → Git 반영 가능` 이 체크되어야만 반영 스크립트가 실행됨
- 사용자가 문제 없다고 확인한 뒤 Linux PC/Git 진행

## 추가 점검 도구
- `D:\\거래플랜\\테스트 시행\\Invoke-MultiPcReadinessCheck.ps1`
  - `준비-다중PC-검증.ps1` 실행 후 생성물, AppData 분리 경로, 상대경로 기반 실행 스크립트 구성이 정상인지 빠르게 확인합니다.
- `D:\\거래플랜\\테스트 시행\\Invoke-MultiPcConflictCheck.ps1`
  - `-Mode Contract`: 서버·Desktop xUnit 계약 회귀입니다. 9개 충돌 범주의 revision·삭제·자동저장 계약을 검사하지만 실제 두 Desktop 프로세스 증거는 아닙니다.
  - `-Mode DesktopE2E`: 같은 Windows PC 안에서 물리적으로 복제한 App, 분리 AppData/temp/download/Sync.DeviceId, 서로 다른 PID 두 개를 실행합니다.
  - 현재 실제 DesktopE2E는 거래처 생성 → PC-B 최신 저장 → PC-A 실제 stale push 거절 → PC-A draft·선택 보존 → 삭제 전파 → 서버 purge → 양쪽 dirty/outbox 0인 대표 경로 1개입니다.
  - 아래 8개 업무 경로의 실제 두 DesktopE2E는 아직 `BLOCKED (미실행)`입니다. `Contract` PASS를 이 경로들의 실제 프로세스 PASS로 해석하지 않습니다.
    - 품목 stale 저장/삭제
    - 렌탈 청구 stale 저장/삭제
    - 렌탈 자산 stale 저장/삭제
    - 재고이동 stale 저장/삭제
    - 렌탈 청구 시작 stale 충돌
    - 재고이동 수령확정 stale 충돌
    - 품목 자동저장 stale 충돌
    - 재고이동 자동저장 stale 충돌
  - 예시:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-MultiPcConflictCheck.ps1" -Mode Contract
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-MultiPcConflictCheck.ps1" -Mode DesktopE2E -DesktopE2EEvidenceDirectory "D:\\거래플랜\\테스트 시행\\기록\\<새-실행-ID>"
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-MultiPcConflictCheck.ps1" -Mode All -DesktopE2EEvidenceDirectory "D:\\거래플랜\\테스트 시행\\기록\\<새-실행-ID>"
```

- `D:\\거래플랜\\테스트 시행\\Invoke-SyncRecoveryCheck.ps1`
  - 강제종료/네트워크 끊김 이후를 가정한 복구 경로를 자동 점검합니다.
  - 현재는 아래 항목을 JSON/Markdown 리포트로 검증합니다.
    - 시작 시 오래 멈춘 `Sent` sync outbox 자동 복구
    - 로컬 증빙 파일 누락 무결성 감지
    - 업데이트 전 pending sync outbox 차단
    - 오래된 업데이트 임시 폴더 정리
  - 예시:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-SyncRecoveryCheck.ps1"
```

- `D:\\거래플랜\\테스트 시행\\Invoke-AccountScopeRegressionCheck.ps1`
  - ITWORLD / USENET / YEONSU 계정으로 실제 로그인해 현재 계정의 tenant/office/scope와 조회 가능한 영역이 맞는지 회귀 점검합니다.
  - 자동 실행에는 계정 정보를 환경변수 `GEORAEPLAN_SCOPE_ITWORLD_USERNAME`, `GEORAEPLAN_SCOPE_ITWORLD_PASSWORD`, `GEORAEPLAN_SCOPE_USENET_USERNAME`, `GEORAEPLAN_SCOPE_USENET_PASSWORD`, `GEORAEPLAN_SCOPE_YEONSU_USERNAME`, `GEORAEPLAN_SCOPE_YEONSU_PASSWORD` 로 넘기면 됩니다.
  - 전체 검증 스크립트에서는 `tasks\\Run-OptionalAccountScopeRegression.ps1` 를 통해 자격정보가 있을 때만 자동 실행되고, 없으면 skip 처리됩니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-AccountScopeRegressionCheck.ps1" -ItworldUsername "itw" -ItworldPassword "***" -UsenetUsername "admin" -UsenetPassword "***"
```

- 데스크톱 실행 중 메뉴/화면이 느릴 때는 `%LOCALAPPDATA%\\거래플랜\\logs\\yyyyMMdd.log` 의 `[PERF]` 로그를 먼저 확인합니다.
  - 시작 초기화, 화면 전환 전 dirty flush, 창별 초기화, 주기 재동기화처럼 800ms 이상 걸린 구간이 기록됩니다.

- `D:\\거래플랜\\테스트 시행\\Invoke-LiveObservationCheck.ps1`
  - Linux PC live 반영 직후 `healthz`, `updates/manifest`, 실제 패키지 URL이 몇 차례 연속으로 정상 응답하는지 기록합니다.
  - 패키지 URL이 인증을 요구하는 환경이면 `-ProbeUsername/-ProbePassword` 또는 `-BearerToken` 으로 인증 fallback probe까지 함께 확인할 수 있습니다.
  - 예시:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\\거래플랜\\테스트 시행\\Invoke-LiveObservationCheck.ps1" -BaseUrl "https://live.example.com" -SampleCount 3 -IntervalSeconds 15 -ProbeUsername "itworld" -ProbePassword "1234"
```
