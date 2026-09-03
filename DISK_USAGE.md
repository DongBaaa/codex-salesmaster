# 거래플랜 디스크 사용 및 재발 방지 가이드

## 결론

2026-09-03 점검에서 현재 체크아웃의 Git/Codex checkpoint는 비정상 증가 원인이 아니었다. 프로젝트 전체는 정리 전 462MiB였고 `.git`은 24MiB였다. 실제 반복 증가 원인은 다음 세 가지였다.

1. Windows 수동 전달 작업이 남긴 1.1.704~1.1.706 설치본 중복 사본
2. 빌드·검증 후 남은 .NET `bin/obj` 및 `/tmp` 도구/작업본
3. 활성 다운로드 폴더에 계속 누적된 과거 데스크톱 설치본

호스트 전체에는 거래플랜과 무관한 Node/pnpm Docker 빌드 캐시 9.153GB와 워크플랜 백업 약 9.2GB도 존재한다. 서비스 소유권이 다르므로 이 작업에서는 삭제하지 않았다.

## 원인 분류와 측정값

| 분류 | 점검 결과 | 판정 |
|---|---:|---|
| A. Codex checkpoint / Git objects | `.git` 23.9MiB, objects 23.2MiB, Codex 전용 객체 추정 5.6MiB | 주원인 아님 |
| B. 잘못 추적된 대용량 파일 | 현재 추적 파일 중 최대는 `전역 기능검사 보고서.md` 3.2MiB. 생성물/DB/설치본 추적 없음 | 문제 없음 |
| C. 무제한 로그 | 거래플랜 API Docker 로그 스트림 약 40MB, PostgreSQL 약 1MB. 기존 컨테이너의 `json-file` 제한 없음 | 잠재 증가 원인 |
| D. DB / SQLite / WAL | 프로젝트 내부 0B. 데스크톱 DB는 사용자 LocalAppData, 운영 PostgreSQL은 `/srv/georaeplan/data/postgres` | 프로젝트 외부로 정상 분리 |
| E. Docker bind mount | 운영 DB·첨부파일은 `/srv/georaeplan` 외부 경로. 개발 Compose만 `infra/data/postgres`를 사용하도록 되어 있었음 | 개발 Compose를 named volume으로 수정 |
| F. 빌드/캐시 | 프로젝트 `bin/obj` 397.5MiB, 사용자 `uv` 캐시 3.45GiB. 별도로 다른 서비스의 Docker 빌드 캐시 9.153GB | 안전 범위만 정리 |
| G. 백업/임시파일 | handoff 1.88GiB, 알려진 `/tmp` 작업본 약 878MiB, 미참조 live 다운로드 약 1.89GiB | 중복·임시 사본 정리 |
| H. 기타 | `/srv/georaeplan/backups` 약 3.8GB, releases 약 2.8GB, app rollback backups 약 2.4GB | 운영 복구 자료라 보존 |

`git fsck --unreachable --no-reflogs`에서 53개 객체가 보였지만 loose object 전체가 1.25MiB에 불과하고 최근 작업 복구에 쓰일 수 있다. 용량 이득이 거의 없어 `git gc --prune=now`나 Codex ref 삭제는 수행하지 않았다.

## 이번에 적용한 구조 변경

### Git 제외 규칙

`.gitignore`에 다음 생성물을 추가했다.

- 로컬 DB 및 sidecar: `*.db`, journal/WAL/SHM, dump
- 서버 로컬 파일 저장소: `App_Data/`
- 과거 개발 Compose bind mount: `infra/data/`
- Windows 패키징/스테이징: `release-artifacts/`, `.georaeplan-stage-*/`
- .NET 테스트·패키지 결과: `TestResults/`, TRX, coverage, BenchmarkDotNet, NuGet package

현재 Git이 추적하는 해당 생성물은 0개였으므로 `git rm --cached`는 필요하지 않았다. `.env`는 예제 파일만 추적 중이며 실제 비밀 설정은 건드리지 않았다.

### Runtime 데이터 위치

- Windows 데스크톱 DB·백업: `%LOCALAPPDATA%\거래플랜` 계열
- 운영 PostgreSQL: `/srv/georaeplan/data/postgres`
- 운영 첨부파일: `/srv/georaeplan/storage/files`
- 운영 Data Protection key: `/srv/georaeplan/storage/data-protection-keys`
- 개발 PostgreSQL: Docker named volume `georaeplan-dev-postgres-data`

점검 시 `infra/data/postgres`는 존재하지 않았으므로 이동할 기존 개발 DB는 없었다. 다른 PC에 이 경로의 기존 개발 DB가 있다면 폴더를 삭제하지 말고 먼저 `pg_dump`로 백업한 뒤 named volume에 복원해야 한다. Compose 변경만으로 기존 폴더가 삭제되지는 않는다.

### Docker 로그 제한

개발 및 Linux Compose 원본에 아래 기본 제한을 적용했다.

- driver: `json-file`
- 파일당 최대 크기: `20m`
- 보관 개수: `5`
- 환경변수 조정: `DOCKER_LOG_MAX_SIZE`, `DOCKER_LOG_MAX_FILE`

이미 실행 중인 운영 컨테이너에는 재생성 전까지 새 옵션이 적용되지 않는다. 이번 작업에서는 무중단 원칙 때문에 API/PostgreSQL 컨테이너를 재생성하지 않았다. 다음 거래플랜 단독 점검 시간에 Compose 원본과 운영용 Compose의 차이를 검토한 뒤 해당 컨테이너만 순차 재생성해야 한다. Docker 전체 재시작이나 전체 prune은 금지한다.

## 안전하게 정리한 데이터

- 현재 커밋과 파일 blob이 모두 동일함을 확인한 `/tmp/georaeplan-1.1.707-verify-SUvRNH` 작업본
- Git에서 무시되며 추적 파일이 0개인 .NET `bin/obj`
- live 파일과 크기·SHA-256이 일치한 handoff 설치본 9개
- stable/current 1.1.707과 stable.previous 1.1.706을 제외한 live 1.1.703~1.1.705 설치본 9개
- 열린 파일 handle이 없는 것으로 확인한 거래플랜 검증용 `/tmp` 디렉터리 6개
- `uv cache prune`이 미사용으로 판정한 공유 dependency cache entry

운영 DB, 첨부파일, DB dump, 현재/직전 설치본, Git branch/tag/history, Codex refs, 다른 서비스의 Docker cache/backup은 삭제하지 않았다.

## 변경 전/후

측정은 `du -x -B1`과 `statvfs` 기준이다. 경로별 합계에는 hardlink로 공유된 블록이 중복 계산될 수 있으므로 실제 회수량은 파일시스템 가용 공간 차이를 기준으로 한다.

| 항목 | 변경 전 | 변경 후 |
|---|---:|---:|
| 파일시스템 사용량 | 90,193,358,848B | 83,890,229,248B |
| 파일시스템 가용량 | 9,510,416,384B | 15,813,545,984B |
| 프로젝트 전체 | 484,827,136B | 약 68MB(문서 추가 전 67,821,568B) |
| `.git` | 25,047,040B | 약 25MB |
| handoff 전체 | 2,019,176,448B | 1,355,776B |
| `/tmp` | 949,460,992B | 29,470,720B |
| `uv` 캐시 | 3,709,591,552B | 1,864,851,456B |
| live update 폴더 | 3,407,450,112B | 1,382,010,880B |
| `/srv/georaeplan` | 14,181,892,096B | 12,156,452,864B |

실제 파일시스템 가용 공간은 **6,303,129,600B(약 5.87GiB)** 증가했다.

## 검사 스크립트 사용법

프로젝트만 점검:

```bash
./scripts/check-disk-usage.sh
```

공유 Codex cache, handoff, 운영 경로, Docker 사용량까지 함께 점검:

```bash
./scripts/check-disk-usage.sh --include-host
```

스크립트는 다음 항목을 출력하지만 아무 파일도 변경하지 않는다.

- 파일시스템과 프로젝트·Git·objects·pack·LFS 크기
- `git count-objects -vH`
- Codex refs와 전용 객체 추정치
- 상위 20개 디렉터리와 파일
- 프로젝트 안의 빌드·로그·DB 후보
- 임계값 경고
- 선택적으로 공유 Codex/cache/handoff/live/Docker 사용량

## 재발 시 확인 순서

1. `./scripts/check-disk-usage.sh --include-host` 실행
2. 프로젝트 `bin/obj`, package output, DB/WAL이 Git status에 잡히는지 확인
3. `.git`이 5GiB를 넘으면 Codex refs와 `git fsck --unreachable --no-reflogs`를 먼저 분석
4. handoff 또는 `/tmp`가 늘었으면 현재 배포본과 해시가 일치하는 중복 사본인지 확인
5. live update 폴더는 stable/current와 stable.previous 두 버전을 보존하고 게시 도구의 retention 경로로만 정리
6. Docker build cache는 설명·생성 시각·서비스 소유자를 확인한 뒤 해당 서비스 담당 범위에서만 정리
7. 운영 DB dump와 rollback backup은 별도 보존 정책 승인 없이는 삭제하지 않음

GitHub Actions Windows package artifact는 workflow에서 7일 보관으로 제한되어 있으며, 앞으로 Windows 수동 handoff 폴더에 대형 설치본을 반복 복사하지 않는다.

## 남은 위험

1. 다른 Node/pnpm 서비스가 만든 Docker build cache 9.153GB가 모두 reclaimable로 표시되지만 거래플랜 소유가 아니므로 남겨 두었다.
2. 워크플랜 백업 약 9.2GB 역시 별도 서비스 자료라 건드리지 않았다.
3. 거래플랜 운영 rollback/release/DB backup은 합계가 크다. 보관 기간과 최소 복구 지점을 별도로 정한 후 전용 retention 작업을 만들어야 한다.
4. 실행 중인 거래플랜 컨테이너는 아직 Docker 로그 제한이 비어 있다. 다음 거래플랜 단독 유지보수에서 새 Compose 설정을 적용해야 한다.
5. `전역 기능검사 보고서.md`는 3.2MiB이고 변경 이력이 많지만 Git pack 전체가 약 22MiB로 잘 압축되어 있다. 현재는 감사 기록으로 판단해 추적을 유지했다.
