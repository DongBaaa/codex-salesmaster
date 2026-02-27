# 레거시 판매관리 (SalesMaster)

한국어 오프라인 우선 판매 관리 데스크톱 ERP 시스템

---

## 기술 스택

| 계층 | 기술 |
|------|------|
| 데스크톱 | .NET 8 WPF + CommunityToolkit.Mvvm + SQLite (EF Core 8) |
| 서버 | ASP.NET Core 8 Web API + EF Core 8 (PostgreSQL/SQLite fallback) |
| 인프라 | Docker Compose (postgres:16-alpine) |
| PDF | QuestPDF (거래명세서 A4 1페이지 2부) |

**금지사항**: Node.js, TypeScript, Express, NestJS, Prisma 사용 불가

---

## 빠른 시작

### 전제조건

- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Docker Desktop

---

## Gate A — 인프라 (PostgreSQL + API 컨테이너)

```powershell
# 1. 인프라 디렉터리로 이동
cd "d:\새 폴더\클로드 레거시 판매관리\infra"

# 2. Docker Compose 실행 (PostgreSQL + API 빌드/시작)
docker compose up -d --build

# 3. 상태 확인 (postgres healthy, api running)
docker compose ps

# 4. 서버 헬스 확인 (JSON 응답 확인)
Invoke-RestMethod http://localhost:8080/swagger/index.html
```

**확인 기준**: `docker compose ps`에서 postgres `healthy`, api `running`

---

## Gate B — 서버 단독 실행 (SQLite fallback)

```powershell
# 서버 프로젝트 디렉터리
cd "d:\새 폴더\클로드 레거시 판매관리\Server\SalesMaster.Server.Api"

# 빌드 및 실행 (PostgreSQL 없이도 SQLite fallback 자동 적용)
dotnet run

# 다른 터미널에서 로그인 API 테스트
$body = '{"username":"admin","password":"CHANGE_THIS_ADMIN_PASSWORD"}'
Invoke-RestMethod -Uri http://localhost:8080/auth/login `
  -Method POST -ContentType "application/json" -Body $body
```

**확인 기준**: JSON 응답에 `token` 필드가 있어야 함

---

## Gate C — 데스크톱 앱 실행

```powershell
# 데스크톱 프로젝트 디렉터리
cd "d:\새 폴더\클로드 레거시 판매관리\Desktop\SalesMaster.Desktop.App"

# 빌드 및 실행
dotnet run

# UI에서 확인:
# 1. 로그인 창: admin / CHANGE_THIS_ADMIN_PASSWORD
# 2. 전표 작성 탭: 거래처 검색 → 품목 입력 → 저장
# 3. 수금 입력 탭: 전표 선택 → 수금 행 추가 → 저장
# 4. 거래명세서 탭 or F9: PDF 저장 다이얼로그 → 저장 확인
```

**확인 기준**: 3개 탭 동작 + PDF 파일 저장 성공

---

## 전체 빌드 (솔루션)

```powershell
cd "d:\새 폴더\클로드 레거시 판매관리"
dotnet build 레거시 판매관리.sln
```

---

## 계정 정보 (초기 시드)

| 계정 | 비밀번호 | 역할 |
|------|----------|------|
| admin | CHANGE_THIS_ADMIN_PASSWORD | Admin (모든 권한) |
| user | CHANGE_THIS_USER_PASSWORD | User (기본 권한) |

---

## 디렉터리 구조

```
클로드 레거시 판매관리/
├── 레거시 판매관리.sln
├── Shared/
│   └── SalesMaster.Shared.Contracts/   # DTOs, 열거형, 동기화 계약
├── Server/
│   └── SalesMaster.Server.Api/         # ASP.NET Core 8 API
│       ├── Controllers/                # Auth, Sync, CRUD endpoints
│       ├── Domain/                     # EF Core 엔티티
│       ├── Data/                       # DbContext, DbInitializer
│       └── Services/                   # JWT, InvoiceNumber, RevisionClock
├── Desktop/
│   └── SalesMaster.Desktop.App/        # WPF MVVM 앱
│       ├── Data/                       # LocalDbContext, 로컬 모델
│       ├── Services/                   # Sync, Print, Backup
│       ├── ViewModels/                 # MVVM ViewModels
│       └── Views/                      # XAML Views
└── infra/
    ├── docker-compose.yml
    └── migrate.ps1
```

---

## 비즈니스 규칙

- **VAT**: 공급가 = ROUND(합계 / 1.1), 부가세 = 합계 - 공급가
- **전표번호**: YYYYMM-0001 형식, 거래처별 월초 리셋 (서버 채번)
- **오프라인 우선**: SQLite 로컬 저장 → 3분마다 서버 동기화
- **충돌 해결**: 서버 우선 (서버 버전이 최신이면 로컬 무시 + ConflictLog 기록)
- **소프트 삭제**: IsDeleted 플래그, 원본 텍스트 보존
- **거래명세서**: A4 1페이지 2부 PDF (QuestPDF), 13행, 직인 이미지 포함

---

## 자체 검증 (Self-check)

```powershell
# .ts/.js 파일이 없어야 함
Get-ChildItem -Recurse -Include "*.ts","*.js" "d:\새 폴더\클로드 레거시 판매관리\Server","d:\새 폴더\클로드 레거시 판매관리\Desktop" | Measure-Object

# .xaml 파일 확인 (최소 4개)
Get-ChildItem -Recurse -Include "*.xaml" "d:\새 폴더\클로드 레거시 판매관리\Desktop" | Select-Object Name

# 모든 .csproj 빌드 성공 확인
dotnet build "d:\새 폴더\클로드 레거시 판매관리\레거시 판매관리.sln"
```

---

## Phase 2 계획

[TODO_PHASE2.md](TODO_PHASE2.md) 참조
