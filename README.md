# 코덱스 레거시 판매관리 (SalesMaster)

- 문서 기준시점: 2026-03-06
- 반영 범위: 커밋 이력 + 현재 워킹트리 변경사항
- 상태 태그: `[완료]`, `[작업중]`, `[검증필요]`, `[보류]`

## 프로젝트 개요
- 오프라인 우선 Windows ERP
- 기술 스택: .NET 8 WPF(MVVM), SQLite, ASP.NET Core API, ClosedXML
- 목표: 전표/거래처/인쇄/집계 업무를 레거시 흐름과 호환되게 안정 운영

## 현재 버전 상태
### 릴리즈 반영 완료
- `[완료]` 판매/거래처/수금 기본 업무 흐름
- `[완료]` 거래명세서/세금계산서/견적서/대금청구서 미리보기 + 인쇄
- `[완료]` 로그인 아이디/비밀번호 저장 옵션
- `[완료]` 자료 기간별 집계(4종 원장) 엑셀 생성/저장/자동 열기
- `[완료]` 시작/종료 동기화 및 자동 저장 기본 흐름

### 개발 중
- `[작업중]` 지점 운영(유즈넷/연수구) 권한 정책의 화면/도메인 연결
- `[작업중]` 연수구 납품내역 전용 화면 및 집계 연동
- `[작업중]` 환경설정(거래처 담당지점 등) 관리 화면 고도화

### 검증 필요/보류
- `[검증필요]` 프린터별 여백 오차/직인 이미지 예외 케이스
- `[검증필요]` 자료기간별 집계의 실데이터 정합성(누적/중복 제거)
- `[완료]` WPF 기본 인쇄 경로로 단일화

## 실행 방법
### 권장 실행
```powershell
cd "D:\새 폴더\클로드 레거시 판매관리"
cmd /c "배포\전체실행.cmd"
```

### 개발 모드 실행
서버:
```powershell
cd "D:\새 폴더\클로드 레거시 판매관리\Server\SalesMaster.Server.Api"
dotnet run
```

데스크톱:
```powershell
cd "D:\새 폴더\클로드 레거시 판매관리\Desktop\SalesMaster.Desktop.App"
dotnet run
```

## 빌드/테스트
```powershell
cd "D:\새 폴더\클로드 레거시 판매관리"
dotnet build "레거시 판매관리.sln" -c Release
```

```powershell
cd "D:\새 폴더\클로드 레거시 판매관리"
dotnet test "레거시 판매관리.sln" -c Release --no-build
```

## 인쇄 기본 동작
- `[완료]` 판매(매출) 창에서 `출력물 편집` 후 데이터 저장
- `[완료]` `인쇄하기(F9)` 클릭 시 미리보기 창 우선 표시
- `[완료]` 미리보기에서 인쇄 클릭 시 Windows PrintDialog 표시
- `[완료]` 외부 PDF 자동 오픈 없이 앱 내부 미리보기 중심 동작

## 자료 기간별 집계(엑셀)
- `[완료]` 지원 유형: 판매+구매, 판매/매출, 구매/매입, 수금/지불
- `[완료]` 저장 경로: `내문서\SalesDoctor\Exports` (또는 설정 경로)
- `[완료]` 파일명: `{From}~{To} 의 {원장종류} 거래원장_{yyyyMMdd_HHmmss}.xlsx`
- `[검증필요]` 일부 실운영 데이터셋에서 산식 검증 필요

## 변경 근거
### 최근 커밋
- `[완료]` `dc47549` docs update
- `[완료]` `ead6e68` period ledger aggregation/export
- `[완료]` `3698ef6` UI/인쇄/동기화/첨부서류 개선

### 워킹트리 작업중(미커밋)
- `[작업중]` `EnvironmentSettingsWindow*`, `EnvironmentSettingsViewModel`
- `[작업중]` `YeonsuDeliveryWindow*`, `YeonsuDeliveryViewModel`
- `[작업중]` `PeriodLedger*`, `LocalDb*`, `MainWindow*`, `SalesViewModel` 보강

## 관련 문서
- 통합 진행 문서: `D:\새 폴더\클로드 레거시 판매관리\기획.md`
- 레거시 비교 문서: `D:\새 폴더\클로드 레거시 판매관리\외부 레거시.md`
