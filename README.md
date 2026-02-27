# 코덱스 레거시 판매관리 (SalesMaster)

오프라인 우선(Offline-first) 소규모 사업자용 Windows ERP입니다.  
데스크톱(.NET 8 WPF, MVVM) + 서버(ASP.NET Core API) + 로컬 SQLite 구조로 동작합니다.

## 현재 핵심 상태
- 앱 명칭: `코덱스 레거시 판매관리`
- 데스크톱: `.NET 8 WPF + CommunityToolkit.Mvvm + EF Core(SQLite)`
- 서버: `ASP.NET Core 8 Web API`
- 인쇄(거래명세서): `WPF FixedDocument + DocumentViewer + PrintDialog`
- 동작 정책: PDF 외부 자동 오픈 없이, 앱 내부 미리보기 후 프린터 선택 인쇄

## 빠른 실행 (권장)
배포 스크립트 1개로 Desktop/Server publish 후 실행합니다.

```powershell
cd "d:\새 폴더\클로드 레거시 판매관리"
cmd /c "배포\전체실행.cmd"
```

실행 후 확인:
1. 서버 프로세스: `SalesMaster.Server.Api`
2. 앱 프로세스: `SalesMaster.Desktop.App`
3. 로그인 창 표시

## 개발 모드 실행

### 서버
```powershell
cd "d:\새 폴더\클로드 레거시 판매관리\Server\SalesMaster.Server.Api"
dotnet run
```

### 데스크톱
```powershell
cd "d:\새 폴더\클로드 레거시 판매관리\Desktop\SalesMaster.Desktop.App"
dotnet run
```

## 인쇄 흐름 (거래명세서)
현재 판매(매출) 창의 거래명세서 인쇄는 아래 순서로 동작합니다.

1. `출력물 편집` 클릭
2. 편집창에서 내용 수정 후 `저장`
3. 판매창 `인쇄하기[F9]` 클릭
4. 미리보기 창(DocumentViewer) 표시
5. 미리보기에서 `인쇄` 클릭
6. Windows `PrintDialog`에서 프린터 선택 후 인쇄

참고:
- 전표별 출력 데이터는 로컬 DB `Settings`에 `InvoicePrint:{InvoiceId}` 키로 저장됩니다.
- 외부 PDF 뷰어 자동 실행은 거래명세서 기본 경로에서 사용하지 않습니다.

## 초기 계정
| 아이디 | 비밀번호 | 권한 |
|---|---|---|
| `admin` | `CHANGE_THIS_ADMIN_PASSWORD` | 관리자 |
| `user` | `CHANGE_THIS_USER_PASSWORD` | 일반 |

## 빌드
```powershell
cd "d:\새 폴더\클로드 레거시 판매관리"
dotnet build "레거시 판매관리.sln" -c Release
```

## 테스트
```powershell
cd "d:\새 폴더\클로드 레거시 판매관리"
dotnet test "레거시 판매관리.sln" -c Release --no-build
```

## 디렉터리
```text
클로드 레거시 판매관리/
├── Desktop/SalesMaster.Desktop.App        # WPF 앱
├── Server/SalesMaster.Server.Api           # ASP.NET Core API
├── Shared/SalesMaster.Shared.Contracts     # 공유 계약(DTO)
├── 배포/                                   # 실행/배포 스크립트
├── 양식/                                   # 인쇄 양식/변환 스크립트
└── 레거시 판매관리.sln
```

## 참고 문서
- 통합 기획/진행 내역: `기획.md`
- Phase 2 TODO: `TODO_PHASE2.md`
