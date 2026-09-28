# 거래플랜 NAS 별도 호스트 백업 사본 검증 — 2026-09-23

## 결과와 범위

Linux PC `itwserver`의 거래플랜 백업을 별도 NAS `ITW-DS`에 1회 복사했다. NAS에서 데이터 파일 7개(827,256,787바이트)의 SHA-256, PostgreSQL 덤프 3개의 목록과 압축파일 2개를 읽어 검증했다. **별도 호스트 사본 검증 PASS이며, 별도 호스트 DB 실제 복원이나 정기 자동 복제 완료는 아니다.**

- 원본: `/srv/georaeplan/backups/automatic/sets/backup_20260922T173357Z-1421767.complete`
- NAS: `/volume1/georaeplan-offhost-backups/sets/backup_20260922T173357Z-1421767.complete`
- 매니페스트 SHA-256: `4b93631bd48f2f087a7c466d49db7a5daecf5e8ffe47e5df015af278a73a70cf`
- 대상: 중앙 DB, ITWORLD DB, USENET DB, 첨부파일, 보호키, 백업 메타데이터. 업무 화면·회사/지점 권한·동기화 로직 변경 없음.

## 보존 및 전송 검증

원본 백업의 공유 잠금을 유지하고, 읽기 전용 컨테이너에서 전송 전후 해시를 확인했다. Windows 디스크에 백업 중간 사본을 만들지 않고 SSH 스트림으로 전송했다. NAS의 임시 디렉터리에서 두 차례 검증한 뒤 원자적 이름 변경으로 확정했다. 전송은 174.59초, 스트림 크기는 827,269,120바이트였다.

NAS 경로는 공유 폴더가 아닌 root 전용 디렉터리이며 디렉터리 0700, 파일 0600이다. 데이터 7개와 SHA256SUMS·COMPLETE를 합한 정확한 파일 9개, 소유자·권한·링크 여부를 확인했다. 경로 이탈/절대 경로/알 수 없는 파일/중복/심볼릭 링크/하드 링크/디렉터리/파일 및 합계 용량 초과 차단을 포함한 모의 검사 10개를 통과했다.

원본과 NAS의 호스트명, machine-id 해시, 부팅 ID 해시를 기록해 별도 호스트임을 확인했다. 기존 Linux 거래플랜 API·DB와 NAS 기존 DB의 컨테이너 ID·시작 시각·재시작 횟수·상태는 전송 전후 동일했다. 검증 후 NAS DB도 running/healthy였다. 임시 전송·검증 컨테이너는 남지 않았다.

## 최초 실패와 재검증

첫 NAS 목록 검증은 Docker 시작 단계에서 exit125로 실패했다. NAS 커널의 CPU CFS quota 미지원으로 `--cpus=0.25`를 적용할 수 없었다. 원본 시도 기록을 보존하고 데이터 마운트 없는 `/bin/true` 실행으로 같은 오류를 확인했다.

새 이름의 재검증은 CPU shares 128과 nice 19를 사용했다. 이는 낮은 우선순위이며 CPU 25% 상한을 보장하지 않는다. 네트워크 차단, 읽기 전용 루트/백업, 모든 capability 제거, no-new-privileges, 메모리/스왑 합계 128MiB, PID 32 제한은 유지했다. 고정된 NAS PostgreSQL 16 이미지의 pg_restore --list로 덤프 3개를 검사했으며 PostgreSQL 서버는 시작하지 않았다. 파일 7개 해시 및 압축파일 2개 읽기 검증 후 독립적으로 게시 경로를 다시 해시 검증했다.

## 남은 작업

NAS의 전체 RAM은 약 1.7GiB로, 실제 복원 절차의 가용 RAM 3GiB 조건을 충족하지 않는다. PC C·D도 이번 점검 시 합계 여유가 약 6.5GiB였다. 실제 별도 호스트 DB 복원과 앱 복구 검증은 추가 시험 공간/환경 확보 후 진행해야 한다. 동일 Linux PC에서 수행한 실제 DB 복원 증거는 `CURRENT_BACKUP_RESTORE_20260923.md`와 구분한다.

이번 사본은 1회 복사다. 정기 복제·보존 기간·실패 알림은 설정하지 않았다. NAS 관리자가 읽을 수 있는 원래 백업 형식이며 저장 시 별도 암호화나 immutable 보관을 구성한 것은 아니다. 원본 서버 또는 NAS 전체 장애에 대비한 실제 복구 훈련과 정기 백업은 남아 있다.

제품·버전·패키지·운영 DB 내용 변경, 서비스 재시작, Git stage/commit/push는 없다. 전체 Goal은 미완료다.

## 증거

`C:\Users\beene\Documents\Codex\tradeplan-offhost-backup-20260923`

- source-current.json, before.json, after.json, result.json
- member-guard-tests.json, stage-verification.json, published.json
- catalog-started.json, catalog-attempt1-failure.json
- catalog-retry-started.json, nas-catalog-output.txt, independent-postcheck.json
- nas_receiver.py, transfer.py, postcheck.py, postcheck-retry.py

위 스크립트는 해당 백업·해시에 고정된 감사 실행본이다. 정기 백업용 일반 절차로 임의 재사용하지 않는다.
