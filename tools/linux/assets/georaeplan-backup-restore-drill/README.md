# 거래플랜 외부 백업 복원 드릴

이 도구는 승인된 외부 replica의 `databases.txt`에 기록된 모든 PostgreSQL custom dump를 운영 DB와 완전히 분리된 일회성 PostgreSQL 컨테이너에 복원한다. 운영 API·PostgreSQL·nginx·거래플랜 compose service를 시작·중지·재시작하거나 수정하지 않는다.

## 격리 계약

- 현재 `backup-status.txt`, `external-replica-status.txt`, replica root marker와 최종 replica set의 run·source manifest·replica manifest가 모두 정확히 일치해야 한다.
- restore 이미지는 설치 시 실행 중인 PostgreSQL service의 content-addressed `sha256:<64 hex>` image ID로 고정한다.
- 컨테이너는 `--network none`, read-only root filesystem, private tmpfs 데이터 디렉터리, replica set read-only bind로만 생성한다.
- manifest의 각 dump를 서로 다른 빈 DB에 `pg_restore --exit-on-error --no-owner --no-privileges`로 복원한다.
- 모든 복원 DB에서 `Users`, `Customers`, `Items`, `Transactions`, `RentalAssets`, `Invoices`, `Payments`를 실제 조회한다. 복원 건수 SHA-256은 `SHA256SUMS`에 결박된 `databases.txt`의 백업 시점 기대값과 각각 exact 일치해야 하며, 전체 집합도 `database_digest_set_sha256`과 같을 때만 `business_count_digest_contract=source_metadata_match`를 기록한다.
- 컨테이너 제거와 replica 재검증이 끝난 뒤에만 `backup-restore-drill-status.txt`를 `restore_drill=ok`로 원자 게시한다.
- 실패는 이전 성공 status를 보존하고 별도 failure status를 기록한다.

## 운영 경계

설치 계획은 읽기 전용이며 `-Apply` 전에는 원격 파일을 만들지 않는다. 설치 후에도 드릴은 자동 실행되지 않는다. 실제 외부 mount와 replica가 준비된 뒤 별도 `-RunAfterInstall` 승인을 제공해야만 실행한다.

계획 실패는 `backup_restore_drill_preflight_failed reason=<bounded_reason>`으로 mount root 부재·reparse·잘못된 mount target·원본과 같은 device·replica marker 부재·백업/replica 상태 부재를 구분한다. plan 모드는 이 진단 과정에서도 원격 mutation을 수행하지 않는다.

실제 상태는 `backup-restore-drill-status.txt`의 현재 source run·manifest 결박과 `restore_drill=ok` 여부로만 판단한다. 설치 또는 과거 실행 기록만으로 복원 가능성을 주장하지 않는다.
