# 테스트 시행

거래플랜 수정은 앞으로 이 폴더 기준으로 **내 PC 로컬 테스트 서버 선검증 → 이상 없으면 메인(live) 버전을 NAS 반영 → Git 반영** 순서로 진행합니다.

## 지금 방식의 핵심
- 운영/NAS 서버와 **완전히 다른 로컬 테스트 서버**를 사용합니다.
- 운영용 로컬 앱 데이터 `%LOCALAPPDATA%\거래플랜` 을 테스트용으로 **복사한 뒤 분리**해서 사용합니다.
- 테스트 앱은 `D:\거래플랜\테스트 시행\실행환경\AppData` 만 사용합니다.
- 테스트 서버는 `D:\거래플랜\테스트 시행\실행환경\Server\거래플랜-local.db` 와 `D:\거래플랜\테스트 시행\실행환경\ServerData` 만 사용합니다.
- 즉, 테스트 중 저장/수정/동기화는 운영/NAS 데이터에 직접 닿지 않습니다.

## 기본 흐름
1. `D:\거래플랜\테스트 시행\테스트-환경-준비.ps1` 실행
2. 현재 로컬 데이터 스냅샷을 테스트 앱 데이터로 복사
3. 복사된 테스트 앱 데이터를 이용해 **분리된 테스트 서버 DB**를 다시 시드
4. 생성된 `D:\거래플랜\테스트 시행\최근 수정 파일.md` 확인
5. 생성된 `D:\거래플랜\테스트 시행\검증 체크리스트.md` 기준으로 점검
6. `D:\거래플랜\테스트 시행\실행환경\Run-All.cmd` 로 테스트 서버+앱 실행
7. 수정사항 확인 완료 후 `D:\거래플랜\테스트 시행\검증완료-반영.cmd` 또는 `D:\거래플랜\테스트 시행\검증완료-반영.ps1` 로 **현재 소스 기준 메인(live) 버전**을 NAS/Git 반영

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

## 권장 실행 예시
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1"
```

### 준비 후 바로 실행까지 하고 싶으면
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1" -Launch
```

### 데이터 복사만 건너뛰고 기존 테스트 스냅샷을 유지하고 싶으면
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\테스트-환경-준비.ps1" -SkipDataCopy
```

## 검증 완료 후 반영 실행

### 1클릭 실행
- 더블클릭: `D:\거래플랜\테스트 시행\검증완료-반영.cmd`
- 실행 시 체크리스트가 통과된 경우에만 **테스트 버전이 아닌 현재 소스 기준 메인(live) 버전**의 NAS/Git 반영을 진행합니다.

### PowerShell 직접 실행
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영"
```

### 드라이런 확인
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -DryRun
```

### 새 untracked 파일이 포함될 때
- 스크립트는 실수 방지를 위해 새 파일/폴더를 자동 Git 반영하지 않습니다.
- 필요한 새 파일이 있으면 `-IncludeUntrackedPaths` 로 명시해야 합니다.

예시:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\테스트 시행\검증완료-반영.ps1" -CommitMessage "테스트 확인 후 반영" -IncludeUntrackedPaths "테스트 시행"
```

## 주의사항
- 테스트 앱은 **반드시** `Run-All.cmd` 또는 `Run-App.cmd` 로 실행하세요.
- `실행환경\App\거래플랜.Desktop.App.exe` 를 직접 실행하면 운영 로컬 데이터 경로를 사용할 수 있습니다.
- 테스트 준비를 다시 실행하면 테스트 앱 데이터와 테스트 서버 데이터는 최신 운영 로컬 스냅샷 기준으로 다시 만들어집니다.

## 반영 원칙
- 테스트 서버 확인 전에는 NAS 반영 금지
- 테스트 서버 확인 전에는 Git 반영 금지
- NAS에는 테스트 서버/테스트 앱이 아니라 **현재 소스에서 다시 생성한 stable/live 배포본만** 반영
- 데스크톱 앱 수정이 포함된 live 반영이면 `D:\거래플랜\Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj` 의 `Version` / `FileVersion` 을 현재 stable 배포본보다 높게 올린 뒤 반영
- 체크리스트의 `문제 없음 → NAS 반영 가능`, `문제 없음 → Git 반영 가능` 이 체크되어야만 반영 스크립트가 실행됨
- 사용자가 문제 없다고 확인한 뒤 NAS/Git 진행
