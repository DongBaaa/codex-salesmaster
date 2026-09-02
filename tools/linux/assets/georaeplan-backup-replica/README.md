# 거래플랜 외부 백업 replica

이 자산은 거래플랜의 최신 `backup_*.complete` 세트를 **승인된 전용 네트워크 mount**로 복제한다. 운영 서비스·DB·원본 파일을 시작·중지·재시작하거나 수정하지 않는다.

## 사전 조건

- mount 경로는 정확히 `/mnt/georaeplan-backup-replica`여야 한다.
- filesystem은 `cifs`, `nfs`, `nfs4` 중 하나이고 원본 `/srv/georaeplan/backups/automatic`과 다른 device여야 한다.
- 해당 mount는 거래플랜 백업 전용 공유여야 한다. 현재 `/mnt/itworld-rental-contracts` 계약서 공유에는 설치하지 않는다.
- 전용 root에는 아래 marker와 고정 lock inode를 provisioning 단계에서 한 번만 생성한다.

```text
.georaeplan-replica-root
  schema_version=1
  owner=georaeplan-external-backup-replica
  replica_id=<32 lowercase hex>

.georaeplan-replica.lock
```

`/etc/georaeplan/backup-replica.env`는 root 소유 0600으로 다음 값만 가진다.

```text
GEORAEPLAN_SOURCE_BACKUP_ROOT=/srv/georaeplan/backups/automatic
GEORAEPLAN_BACKUP_STATE_ROOT=/srv/georaeplan/ops/state
GEORAEPLAN_REPLICA_ROOT=/mnt/georaeplan-backup-replica
GEORAEPLAN_REPLICA_ID=<marker와 같은 32 lowercase hex>
```

## 검증·게시 계약

1. source backup lock을 shared로, replica lock을 exclusive로 보유한다.
2. `backup-status.txt`의 정확한 현재 run·manifest·DB snapshot consistency를 확인한다.
3. source complete set의 `databases.txt`와 정확한 entry set, 단일-link 일반 파일, `SHA256SUMS`, 두 tar, manifest에 적힌 모든 dump의 `pg_restore -l`을 검증한다.
4. 외부 root의 같은 filesystem `.staging`에 복사하고 다시 동일 검증을 수행한다.
5. `REPLICA` marker에 source run/hash와 replica manifest hash를 결박한 뒤 `sets/replica_<run>.complete`로 원자 이동한다.
6. 최종 세트를 재검증한 뒤에만 로컬 `external-replica-status.txt`를 `replica=ok`로 원자 게시한다.
7. 실패는 마지막 성공 status/set을 보존하고 `external-replica-failure-status.txt`에 별도로 기록한다.

운영 gate는 `replica=ok` 문자열만 믿지 않는다. 전용 validator가 현재 `backup-status.txt`의 run·manifest와 replica status의 source run·manifest가 정확히 일치하는지, archive/catalog 검증과 신선도, 더 최신 실패가 없는지를 확인한다.

## 로컬 검증

```powershell
powershell -NoProfile -File tools/linux/Test-GeoraeplanLinuxPcBackupReplica.ps1
```

이 fixture는 로컬 filesystem 허용 test-only 인자를 사용하지만 systemd service에는 해당 인자가 없다. 운영 service는 네트워크 filesystem과 다른 device를 항상 요구한다.

## 설치 계획과 적용

전용 mount가 준비된 뒤 먼저 `-Apply` 없이 읽기 전용 계획을 실행한다. `ReplicaId`는 해당 전용 root에 한 번 정한 32자리 소문자 16진수 식별자를 계속 재사용한다.

```powershell
powershell -NoProfile -File tools/linux/Install-GeoraeplanLinuxPcBackupReplica.ps1 `
  -ReplicaId '<32 lowercase hex>'
```

계획 출력의 `backup_replica_remote_readonly_preflight=ok`와 자산 SHA-256을 확인한 뒤에만 명시적으로 적용한다.
계획이 실패하면 `backup_replica_preflight_failed reason=<bounded_reason>`으로 mount root 부재·reparse·원본과 같은 device·백업 상태 부재·용량 값 오류·용량 부족을 구분한다. 이 출력은 진단 전용이며 plan 모드에서는 원격 파일이나 서비스 상태를 변경하지 않는다.

```powershell
powershell -NoProfile -File tools/linux/Install-GeoraeplanLinuxPcBackupReplica.ps1 `
  -ReplicaId '<same 32 lowercase hex>' `
  -Apply -PromptForSudoCredential
```

적용 경계는 전용 root의 mount·device·marker·허용 entry를 다시 확인한 뒤 자산을 설치하고 timer만 활성화한다. 운영 거래플랜 컨테이너·DB·nginx를 재시작하지 않는다.

## 아직 수행하지 않는 작업

- 현재 PC/Linux PC에서 확인된 `/mnt/itworld-rental-contracts`에는 백업을 쓰지 않는다.
- 승인된 전용 NAS share·mount·root marker가 제공되기 전에는 service/timer를 설치하거나 활성화하지 않는다.
- replica의 archive/catalog 검증은 실제 DB restore drill과 다르다. 별도의 `georaeplan-backup-restore-drill.sh`가 네트워크 없는 일회성 PostgreSQL에 manifest의 모든 dump를 복원하고 업무 표본 쿼리·컨테이너 제거·현재 replica 재검증까지 통과하기 전에는 `restore_drill=ok`를 기록하지 않는다.
- machine-readable boundary: `restore_drill=not_proven`

복원 드릴 설치기는 기본적으로 읽기 전용 계획만 수행한다. 실제 설치와 실행은 각각 `-Apply`, `-RunAfterInstall`을 명시해야 한다.

```powershell
powershell -NoProfile -File tools/linux/Install-GeoraeplanLinuxPcBackupRestoreDrill.ps1 `
  -ReplicaId '<same 32 lowercase hex>'
```
