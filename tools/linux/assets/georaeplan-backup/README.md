# 거래플랜 Linux PC 자동 백업

이 자산은 `/srv/georaeplan` 운영 compose의 `postgres` 서비스만 읽어 중앙 DB와 업체 DB를 custom dump로 만들고, 파일 저장소와 Data Protection key ring을 함께 보관한다. 컨테이너·Docker·PostgreSQL·API를 시작하거나 재시작하지 않는다.

## 완성 백업 세트

백업은 `/srv/georaeplan/backups/automatic/.staging`에서 생성·검증한 뒤 같은 파일시스템의 `sets/backup_*.complete` 디렉터리로 한 번에 이동한다.

- `georaeplan.dump`
- `georaeplan_itworld.dump`
- `files.tar.gz`
- `data-protection-keys.tar.gz`
- `metadata.txt`
- `SHA256SUMS`
- `COMPLETE`

두 dump는 `pg_restore -l`, 두 tar는 `tar -tzf`, 전체 데이터 파일은 `sha256sum -c`를 통과해야 한다. PostgreSQL 16의 cluster-wide `pg_current_snapshot()` 토큰을 첫 dump 직전과 두 번째 dump 직후에 읽어 transaction 가시성 변화가 감지되면 실패한다. 이 토큰 비교는 두 `pg_dump`가 하나의 exported snapshot을 공유했다는 뜻은 아니므로, 중앙 DB와 업체 DB 각각에서 `Users`, `Customers`, `Items`, `Transactions`, `RentalAssets`, `Invoices`, `Payments` 건수 해시를 해당 dump 직전과 직후에 계산해 같은 경우에만 게시한다. 두 기대 해시는 manifest에 포함되는 `metadata.txt`에 기록하며 이후 외부 replica 복구 드릴의 exact 비교 기준이 된다. 검증된 세트를 원자 게시하고 `ops/state/backup-status.txt`가 새 세트를 가리키도록 `backup=ok`로 원자 교체한 뒤에만 만료 세트를 정리한다. 따라서 상태 교체 실패가 마지막 성공 상태가 참조하는 기존 세트를 먼저 지우지 않는다. 실패는 성공 상태를 덮지 않고 `backup-failure-status.txt`와 실행별 로그에 별도로 남는다.

DB dump 시작 전 `storage/files/.georaeplan-backup-delete.lock`에 배타 `flock`을 잡고 파일 tar가 끝날 때까지 유지한다. 설치기는 이 고정 inode를 일반 파일·`0644`로 미리 만들며 API는 생성하거나 쓰지 않고 읽기 전용 FD의 공유 non-blocking lock만 사용한다. 백업 중이면 물리 삭제를 안전하게 건너뛴다. 저장 파일은 임시 파일 완성 후 원자 게시되고 DB commit 전에 경로가 확정되므로, DB snapshot이 참조하는 파일은 tar까지 보존되고 snapshot 뒤 생긴 추가 파일은 복구를 깨뜨리지 않는다. API 컨테이너를 pause/restart하지 않는다.

매 백업 전에 `api`와 `postgres`가 모두 실행 중인지 확인하고, host의 `/srv/georaeplan/storage/files`와 컨테이너의 `/storage/files`에서 lock 파일의 device/inode를 비교한다. API가 중지됐거나 실제 bind mount가 아니거나 inode가 다르면 백업은 실패 상태를 남기고 dump를 시작하지 않으며 어떤 서비스도 시작하지 않는다. 설치 plan도 두 디렉터리의 device/inode 동일성과 `api`/`postgres` 실행 상태를 읽기 전용으로 확인한다.

lock inode는 설치 `-Apply` 단계에서 없을 때만 한 번 생성하고 이후 교체·삭제하지 않는다. oneshot은 systemd의 storage read-only sandbox 안에서 기존 inode를 read-only FD로 열어 `flock`하므로 storage 쓰기 권한을 넓히지 않는다.

동시 실행은 `flock`으로 차단한다. 새 정상 세트를 완성한 뒤 14일이 지난 완료 세트만 삭제하므로 마지막 정상본은 보존된다.

## 안전 경계

- systemd unit은 Docker를 `Requires`/`Wants`하지 않는다. Docker 또는 `postgres` 서비스가 멈춰 있으면 시작하지 않고 실패 상태를 기록한다.
- compose `.env`의 `ITWORLD_POSTGRES_DB`와 컨테이너의 `POSTGRES_DB`를 각각 읽고, 두 DB명이 유효하며 서로 다른지 확인한 뒤 dump한다.
- 두 DB를 순차 dump하는 동안 cluster snapshot 토큰이 달라지면 `database_snapshot_drift`로 실패한다. 이 검사는 서비스를 멈추거나 쓰기를 차단하지 않고, 일관성을 확인할 수 없는 실행을 complete set으로 게시하지 않는 실패 폐쇄 경계다.
- 각 DB의 7개 핵심 테이블 건수 해시가 해당 dump 전후 달라지면 `business_count_digest_drift`로 실패한다. 이 계약은 건수 일치만 증명하며 행 내용 전체의 동일성을 과장하지 않는다.
- 관리 경로는 절대 경로·점 경로(`.`/`..`)·실제 경로 경계를 검사하고, 백업 출력과 파일/key ring 원본이 겹치면 중단한다.
- DB 논리 크기와 파일 원본 크기에 10% 여유를 더한 예상 백업 크기 외에 최소 2 GiB와 1,024 inode가 남는 경우에만 dump를 시작한다.
- compose 파일이나 `.env`가 없거나 용량·DB 식별 검사가 실패해도 systemd 조건으로 조용히 건너뛰지 않고 `backup-failure-status.txt`에 실패를 남긴다.

## replica 계약

현재 `infra/linux/.env.example`은 `EXTERNAL_REPLICA_ENABLED=false`이고 운영 gate는 별도 `external-replica-status.txt`의 `replica=ok`를 요구한다. 이 스케줄러는 외부 replica를 만들지 않으며 성공 상태에도 `replica=disabled`를 기록한다.

따라서 이 백업이 성공해도 replica gate를 충족한 것으로 간주하면 안 된다. 외부 복제 저장소와 검증 절차가 승인·구현되기 전에는 `replica=ok` 파일을 합성하거나 운영 gate를 완화하지 않는다.

## 로컬 검증과 설치 경계

```powershell
powershell -NoProfile -File tools/linux/Test-GeoraeplanLinuxPcBackupSchedule.ps1
powershell -NoProfile -File tools/linux/Install-GeoraeplanLinuxPcBackupSchedule.ps1
```

설치 스크립트의 기본 실행은 로컬 자산 검사와 Linux PC read-only 계획 확인만 수행한다. 실제 업로드, `/usr/local/sbin`·`/etc/systemd/system` 변경, `daemon-reload`, timer 활성화는 사용자가 live 반영을 명시적으로 승인한 뒤 `-Apply`로 실행해야 한다.

설치 후 운영자가 별도로 확인할 항목:

```bash
systemctl list-timers georaeplan-backup.timer
systemctl status georaeplan-backup.timer --no-pager
journalctl -u georaeplan-backup.service -n 100 --no-pager
cat /srv/georaeplan/ops/state/backup-status.txt
```
