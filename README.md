# 거래플랜

- 문서 기준시점: 2026-03-12
- 반영 범위: 커밋 이력 + HEAD(`42a0d21`) 기준 구현 상태
- 상태 태그: `[완료]`, `[작업중]`, `[검증필요]`, `[보류]`

## 프로젝트 개요
- 오프라인 우선 Windows ERP
- 기술 스택: .NET 8 WPF(MVVM), SQLite, ASP.NET Core API, ClosedXML
- 목표: 전표/거래처/인쇄/집계 업무를 레거시 흐름과 호환되게 안정 운영

## 현재 버전 상태
### 릴리즈 반영 완료
- `[완료]` 판매/거래처/수금 기본 업무 흐름
- `[완료]` 거래명세서/세금계산서/견적서/대금청구서 미리보기 + 인쇄
- `[완료]` 로그인 아이디/비밀번호 저장 옵션 + 오프라인 로그인 fallback
- `[완료]` 자료 기간별 집계(5종: 판매+구매, 판매/매출, 구매/매입, 수금/지불, 연수구 납품내역) 엑셀 생성/저장/자동 열기
- `[완료]` 환경설정 운영 화면(회사정보, 선택값 관리, 담당지점 관리, 사용자 관리)
- `[완료]` 지점별 재고 조회 + 내부 재고이동 기본 흐름
- `[완료]` 시작/종료 동기화 및 자동 저장 기본 흐름
- `[완료]` WPF 기본 인쇄 경로로 단일화

### 개발 중
- `[작업중]` 지점/권한 정책을 전표 저장/조회/수정 전 구간에 더 정교하게 적용
- `[작업중]` 로컬 확장 도메인(담당지점/창고/선택값/재고이동/원가계층)의 서버 동기화 범위 확장
- `[작업중]` FIFO 원가/시리얼/부분납품 운영 UX 고도화

### 검증 필요/보류
- `[검증필요]` 프린터별 여백 오차/직인 이미지 예외 케이스
- `[검증필요]` 자료기간별 집계 및 연수구 납품내역의 실데이터 정합성(누적/중복 제거)
- `[검증필요]` 장시간 운영 시 동기화/백업 알림 노이즈

## 실행 방법
### 권장 실행(로컬 빠른 확인)
```powershell
cd "D:\거래플랜"
cmd /c "배포\전체실행.cmd"
```

- 2026-03-20 실제 검증 결과:
  - `배포\전체실행.cmd` 는 로컬 테스트용으로 `Development` 환경 + SQLite fallback 기준에서 실행되도록 조정함
  - 서버는 `http://127.0.0.1:19080` 계열이 아니라, 스크립트가 잡은 실제 포트로 기동
  - 외부 NAS 실서버 확인은 `https://api.example.invalid` 기준으로 별도 검증

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

- 위 실행본은 `https://api.example.invalid` 를 바라보는 포터블 배포본이다.
- 로컬 서버 없이 외부 NAS API에 붙여서 UI/로그인 테스트할 때 가장 단순하다.

## 빌드/테스트
```powershell
cd "D:\거래플랜"
dotnet build "거래플랜.sln" -c Release
```

```powershell
cd "D:\거래플랜"
dotnet test "거래플랜.sln" -c Release --no-build
```

- 참고: 현재 솔루션에 별도 테스트 프로젝트는 없어 `dotnet test` 는 빌드/구성 검증 성격이 강합니다.

## NAS 자동 배포(권장)
PC 설치파일, Android APK, 업데이트 자산 생성 후 NAS에 **파일 복사 + `apply-release.sh` 자동 실행 + 컨테이너 재기동**까지 한 번에 처리하려면 아래 명령을 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\거래플랜\tools\release\Publish-GeoraePlanFullRelease.ps1" -ProjectRoot "D:\거래플랜" -SigningConfigPath "D:\거래플랜\Mobile\GeoraePlan.Mobile.App\android-signing.local.json" -DeployToNas
```

사전 조건:
- `D:\거래플랜\infra\nas\.env.example` 또는 운영용 `.env`에 `NAS_SSH_USER`를 포함한 SSH 정보가 채워져 있어야 합니다.
- `NAS_SSH_KEY_PATH`는 **Windows 배포 PC 기준 경로**입니다.
- 권장 동작 순서:
  1. SSH 설정이 있으면 배포 PC가 NAS의 `apply-release.sh`를 바로 실행
  2. SSH 설정이 없고 `NAS_SCHEDULED_APPLY_ENABLED=true`이면 NAS 작업 스케줄러의 `auto-apply-release.sh`가 pending release를 감지해 로컬에서 `apply-release.sh`를 실행
- 정말 필요한 경우에만 `-AllowLegacyLiveMirror`를 명시해 예전 방식의 직접 미러링을 허용할 수 있습니다.

## 인쇄 기본 동작
- `[완료]` 판매(매출) 창에서 `출력물 편집` 후 데이터 저장
- `[완료]` `인쇄하기(F9)` 클릭 시 미리보기 창 우선 표시
- `[완료]` 미리보기에서 인쇄 클릭 시 Windows PrintDialog 표시
- `[완료]` 외부 PDF 자동 오픈 없이 앱 내부 미리보기 중심 동작

## 자료 기간별 집계(엑셀)
- `[완료]` 지원 유형: 판매+구매, 판매/매출, 구매/매입, 수금/지불, 연수구 납품내역
- `[완료]` 저장 경로: `내문서\거래플랜\Exports` (또는 설정 경로)
- `[완료]` 파일명: `{From}~{To} 의 {원장종류} 거래원장_{yyyyMMdd_HHmmss}.xlsx`
- `[검증필요]` 일부 실운영 데이터셋에서 산식 검증 필요

## 변경 근거
### 최근 커밋
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
- 레거시 비교 문서: `D:\거래플랜\외부 레거시.md`
- NAS 운영 런북: `D:\거래플랜\infra\NAS-운영-런북.md`
- NAS 실운영 최소 체크리스트: `D:\거래플랜\infra\NAS-실운영-최소체크리스트_2026-03-19.md`
- NAS 실행 명령 요약: `D:\거래플랜\infra\NAS-실행-명령요약_2026-03-19.md`
- NAS 보안 체크리스트: `D:\거래플랜\infra\NAS-보안-체크리스트_2026-03-19.md`
- NAS 포트/폴더/도메인 배치안: `D:\거래플랜\tasks\NAS_포트_폴더_도메인_배치안_2026-03-19.md`
- 안드로이드 MVP 기능명세: `D:\거래플랜\tasks\안드로이드_MVP_기능명세_2026-03-19.md`
- 안드로이드 MAUI 스캐폴드: `D:\거래플랜\Mobile\GeoraePlan.Mobile.App\README.md`
- 안드로이드 빌드/서명/직접설치 가이드: `D:\거래플랜\Mobile\안드로이드_빌드_서명_설치_가이드_2026-03-19.md`
- 안드로이드 빌드환경 부트스트랩 스크립트: `D:\거래플랜\tools\mobile\Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1`
- 안드로이드 환경 점검 스크립트: `D:\거래플랜\tools\mobile\Test-GeoraePlanAndroidEnvironment.ps1`
- 안드로이드 keystore 생성 스크립트: `D:\거래플랜\tools\mobile\New-GeoraePlanAndroidKeystore.ps1`
- 안드로이드 서명 APK 빌드 스크립트: `D:\거래플랜\tools\mobile\Build-GeoraePlanAndroidApk.ps1`
- 안드로이드 실사용 APK: `D:\거래플랜\배포\거래플랜-안드로이드-v0.2.4-signed.apk`
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
