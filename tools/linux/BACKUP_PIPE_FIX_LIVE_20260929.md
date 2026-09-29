# 거래플랜 백업 파이프 수정 운영 결과 — 2026-09-29

## 변경 및 영향

환경 설정 출력에서 첫 DB 이름을 찾자마자 awk가 종료하면 생산자가 SIGPIPE(141)로 실패할 수 있었다. 첫 값은 유지하면서 출력 끝까지 소비하도록 백업 스크립트 두 줄을 수정했다. Linux 재현에서 기존 방식은 6검사 중 5실패, 수정본은 6검사 모두 통과했다. 생산자 자체의 실패는 계속 차단한다.

수정 파일: `tools/linux/assets/georaeplan-backup/georaeplan-backup.sh`, `tools/linux/test_backup_environment_pipe.py`, 이 보고서. 업무 화면·업체/지점 범위·청구·전표·동기화·데스크톱 버전에는 변경이 없다. 로컬 복구 도구 `tools/linux/recovery-readonly/backup-once.py`의 승인 해시도 검증된 설치본으로 갱신했다.

## 운영 반영 및 검증

- 19:12~19:14 KST에 `/usr/local/sbin/georaeplan-backup.sh` 한 파일을 원자적으로 교체하고 새 백업 1회를 완료했다.
- 원본 보관: `/srv/georaeplan/ops/state/script-fixes/backup-pipe-20260929T101227Z`.
- 원본 SHA-256: `a78fe17098e2675a17bdb60dd84575c71c4841f3325d65533f4ede5b80bbbad3`.
- 적용 SHA-256: `91e3c898a1f6cf88d1bcccddb7c1b0c04ddebf9e36e1c98812329b023c4a8960`.
- 새 백업: `/srv/georaeplan/backups/automatic/sets/backup_20260929T101227Z-3023384.complete`. 기존 SSD 마운트 경로를 사용했다.
- 중앙·아이티월드·유즈넷 DB 3개와 첨부파일·보호키·목록·메타데이터, 총 7파일의 SHA-256이 모두 일치했다.
- manifest SHA-256: `cc460ae595bd96fc94aaf78b3635bbd55cf5cf8a2e448058a3a658258aa466b4`.
- 기존 완료 백업 18세트 보존. 이번 수동 실행만 보관 기간을 365000일로 지정했고, 예약 service/timer 및 기존 보관 정책은 변경하지 않았다.
- 임시 단위 `georaeplan-backup-once-3c592bf94ae9`는 종료 코드 0으로 완료됐다. 19:18 독립 SSH 조회에서 단위 제거·inactive를 확인했다.
- API·PostgreSQL·relay의 컨테이너 ID/이미지/시작 시각이 전후 일치하고 재시작 횟수 0이다. 독립 조회에서 거래플랜·워크플랜 HTTPS health 모두 200이다.

## 근거 및 원복

로컬 근거는 `D:\거래플랜\.work\backup-pipe-live-20260929`의 `linux-tests.json`, `procedure-check.json`, `deployment-result.json`, `independent-postcheck.json`이다. 기존 백업 스케줄 모의 검사도 통과했다. 운영 실패 시에는 적용 파일 해시가 이번 수정본과 일치할 때만 보관 원본으로 되돌리는 절차를 준비했으며, 이번 실행은 성공하여 원복하지 않았다.

## 제한 및 후속

새 백업의 실제 DB 복원은 이번 작업에 포함하지 않았다. 과거 exit 255와 이번 SIGPIPE 결함이 같은 원인이라는 증거도 없다. 전체 재해복구, 최신 데스크톱 패키지·실제 화면·모바일 검증은 별도 미완료다. 기존 1.1.748 후보의 배포 보류를 유지한다. 이번 일회성 적용 스크립트는 재실행하지 않는다.
