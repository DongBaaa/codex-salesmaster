# 다중PC 검증 시나리오

## 목적
- 같은 서버를 두 개의 독립 로컬 캐시(PC-A / PC-B)로 동시에 사용하는 상황을 점검합니다.
- stale 저장, stale 삭제, 상태 변경 충돌, 자동저장 충돌이 실제로 차단되는지 확인합니다.
- 강제 종료, dirty 데이터, 범위 변경 시나리오도 수동 점검 기준으로 정리합니다.

## 준비
1. `D:\거래플랜\테스트 시행\테스트-환경-준비.ps1` 실행
2. `D:\거래플랜\테스트 시행\준비-다중PC-검증.ps1 -ResetClientData` 실행
3. `D:\거래플랜\테스트 시행\실행환경\MultiPC\Run-All-MultiPC.cmd` 로 서버 + PC-A + PC-B 실행
4. xUnit 계약 회귀는 `Invoke-MultiPcConflictCheck.ps1 -Mode Contract`로 실행
5. 실제 대표 DesktopE2E는 새롭거나 빈 증거 폴더를 지정해 `Invoke-MultiPcConflictCheck.ps1 -Mode DesktopE2E -DesktopE2EEvidenceDirectory "<증거 폴더>"`로 실행

## Contract xUnit 회귀 범위

`Contract` 모드는 아래 9개 충돌 범주의 코드·서비스 계약을 xUnit으로 검증합니다. 실제 PC-A/PC-B Desktop 프로세스나 분리 AppData 동시 실행 증거가 아닙니다.

1. 거래처 stale 저장/삭제 충돌
2. 품목 stale 저장/삭제 충돌
3. 렌탈 청구 stale 저장/삭제 충돌
4. 렌탈 자산 stale 저장/삭제 충돌
5. 재고이동 stale 저장/삭제 충돌
6. 렌탈 청구 시작 stale 충돌
7. 재고이동 수령확정 stale 충돌
8. 품목 자동저장 stale 충돌
9. 재고이동 자동저장 stale 충돌

## 실제 DesktopE2E 대표 경로

현재 `DesktopE2E`가 실제 두 Desktop 프로세스로 실행하는 범위는 거래처 대표 경로 1개입니다.

1. 서로 다른 App/AppData/temp/download/Sync.DeviceId와 PID로 PC-A/PC-B를 실행합니다.
2. 두 앱이 같은 인증된 loopback 테스트 서버, 사용자, tenant, office, scope로 로그인합니다.
3. PC-A가 거래처를 생성·동기화하고 편집창에 미저장 draft를 유지합니다.
4. PC-B가 같은 거래처에 최신값을 저장·동기화합니다.
5. PC-A가 실제 stale DTO를 서버에 push해 `accepted=0`, `conflicts=1`, `Expected revision mismatch`를 확인합니다.
6. PC-A의 자동저장은 로컬 최신 revision guard에서 거부되고 PC-B 서버값과 PC-A draft·선택이 함께 보존되는지 확인합니다.
7. PC-B 삭제, PC-A 삭제 관측, 서버 purge 후 양쪽 local row 없음·dirty 0·outbox pending/failed 0을 확인합니다.
8. 앱·서버·loopback listener, 임시 `.gp-stage/.gp-validate` 백업, 서버 DB 변경, 원본 appsettings가 모두 정리·복원됐는지 확인합니다.

## BLOCKED (미실행) 실제 업무 경로

다음 8개 경로는 `Contract` xUnit은 있으나 v55 실제 두 DesktopE2E로 실행하지 않았습니다.

1. 품목 stale 저장/삭제
2. 렌탈 청구 stale 저장/삭제
3. 렌탈 자산 stale 저장/삭제
4. 재고이동 stale 저장/삭제
5. 렌탈 청구 시작 stale 충돌
6. 재고이동 수령확정 stale 충돌
7. 품목 자동저장 stale 충돌
8. 재고이동 자동저장 stale 충돌

## 실제 DesktopE2E 기대 결과

- PC-B가 먼저 최신 내용을 저장한 뒤 PC-A의 오래된 저장/삭제/상태변경 요청은 충돌로 차단되어야 합니다.
- 자동저장 충돌 시 DB에는 최신값(PC-B)이 유지되어야 합니다.
- 자동저장 충돌 시 편집창 값(PC-A 임시 입력)은 유지되어야 합니다.
- 선택된 행도 유지되어야 하며, 상태 메시지로 충돌 사실이 표시되어야 합니다.
- Contract 리포트는 `D:\거래플랜\테스트 시행\기록\multi-pc-conflict-*`, 실제 E2E 리포트는 지정한 새 증거 폴더에 JSON/Markdown으로 남아야 합니다.

## 수동 점검 시나리오
### 1. 동시 로그인 및 기본 조회
- PC-A, PC-B 모두 로그인합니다.
- 같은 계정으로 거래처/품목/렌탈/재고 화면을 각각 열어 조회합니다.
- 초기 로딩 오류, 깨진 캐시, 자동 동기화 오류가 없는지 확인합니다.

### 2. PC-A 저장 후 PC-B 새로고침
- PC-A에서 거래처나 품목을 수정하고 저장합니다.
- PC-B에서 같은 화면을 재조회합니다.
- 최신 내용이 반영되는지 확인합니다.

### 3. dirty 데이터가 있는 상태에서 업데이트 차단
- PC-A에서 저장하지 않은 편집 상태를 만듭니다.
- 환경설정 > 업데이트에서 업데이트를 시도합니다.
- dirty 데이터가 남아 있으면 업데이트가 차단되는지 확인합니다.

### 4. 강제 종료 후 복구
- PC-B에서 수정 직후 프로세스를 강제 종료합니다.
- 다시 실행한 뒤 미저장 변경, 동기화 필요 상태, 복구 안내가 정상적으로 보이는지 확인합니다.

### 5. 범위 변경 / 전체 동기화
- 다른 계정으로 로그인하거나 담당지점 범위를 바꿉니다.
- 범위 변경 후 전체 동기화를 실행합니다.
- 조회 범위와 캐시가 새 정책으로 정리되는지 확인합니다.

### 6. 종료 전 동기화
- 변경사항이 남은 상태에서 종료를 시도합니다.
- 필요한 경고가 표시되고, 즉시 종료로 데이터가 사라지지 않는지 확인합니다.

## 확인 포인트
- 두 클라이언트가 서로 다른 `GEORAEPLAN_APP_ROOT` 를 사용해야 합니다.
- 같은 서버 기준으로 stale 요청이 정확히 차단되어야 합니다.
- 자동저장 충돌 시 최신 DB 값과 편집창 임시값이 함께 보호되어야 합니다.
- 현재 실제 자동 증거는 같은 Windows PC 안의 두 프로세스와 SQLite fallback 서버입니다. 물리적으로 다른 PC와 운영 PostgreSQL 증거가 아닙니다.
- live 반영 전에는 `Contract`와 필요한 실제 업무별 DesktopE2E를 각각 실행해야 하며, 대표 거래처 경로 1개 PASS만으로 미실행 8개 경로를 대체하지 않습니다.

## 2026-07-31 DesktopE2E v5 권위 갱신

위의 `BLOCKED (미실행) 실제 업무 경로` 목록 중 다음 두 품목 항목은 더 이상 미실행이 아닙니다.

1. 품목 stale 저장·삭제
2. 품목 자동저장 stale 충돌

### 실제 수행 결과

- 증거 폴더:
  `D:\거래플랜\테스트 시행\기록\20260731-item-multipc-e2e-v5`
- 결과: PASS, 18/18 단계, nonce-bound coordination 20/20
- 저장된 격리 테스트 계정으로 PC-A·PC-B의 공식 in-app 자동 로그인을 수행했습니다.
- 거래처와 품목 모두 생성, 상대 PC 조회, PC-B 최신 저장, PC-A stale 저장·자동저장 충돌, PC-A draft·선택 보존, PC-B 서버값 유지, 삭제 관측, 서버 purge, 양쪽 local row·dirty·outbox 잔여 0을 확인했습니다.
- 두 Desktop은 서로 다른 PID, install/AppData/temp/download/device identity를 사용했습니다.
- 종료 뒤 Desktop/server process와 loopback listener, 임시 `.gp-stage/.gp-validate` 파일은 0이고 server DB file-set digest와 source appsettings는 exact rollback됐습니다.

### 계속 미실행인 실제 업무별 경로

1. 렌탈 청구 stale 저장·삭제
2. 렌탈 자산 stale 저장·삭제
3. 재고이동 stale 저장·삭제
4. 렌탈 청구 시작 stale 충돌
5. 재고이동 수령확정 stale 충돌
6. 재고이동 자동저장 stale 충돌

이번 결과는 같은 Windows PC 안의 격리된 두 Desktop 프로세스와 loopback 테스트 서버 증거입니다. 물리적으로 다른 두 PC와 운영 PostgreSQL 증거로 확대 해석하지 않습니다.
