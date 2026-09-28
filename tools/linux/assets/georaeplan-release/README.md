# 거래플랜 API 배포·앱 원복 스크립트

현재는 **검증된 로컬 후보**다. 운영 `/srv/georaeplan/ops/apply-release.sh`에는 아직 설치하지 않았다. 기존 Publisher가 자동으로 이 파일을 설치하는 기능은 없다.

## 수정 이유와 동작

기존 운영 스크립트는 health 검사 실패에서만 원복했다. 파일 복사 또는 API 재생성 실패는 `set -e`로 먼저 종료됐다. 이 후보는 다음 순서로 동작한다.

1. 릴리스 필수 파일, rsync/flock 사용 가능 여부와 입력을 검사한다.
2. `.apply-release.lock`을 잠그고 앱 디렉터리를 백업한다. 백업 실패 시 live 파일을 변경하지 않는다.
3. 첫 live 복사 전에 원복을 활성화한다. 복사 중 실패, API 재생성 실패, health/readiness 실패, INT/TERM은 공통 원복 경로로 진입한다.
4. 배포와 원복에 `rsync -a --checksum --delete`를 사용한다. 같은 크기·mtime의 다른 내용도 복사하고 새 버전에만 있던 파일은 원복 때 제거한다. rsync가 없으면 사전 중단하며 선삭제 tar 대체 경로를 사용하지 않는다.
5. `georaeplan` 프로젝트의 **api만** 재생성한다. PostgreSQL/공통 Docker/다른 서비스는 재시작하지 않는다.
6. `/healthz`와 `/readyz`가 모두 정확히 HTTP 200일 때 성공한다. 리다이렉트는 성공으로 간주하지 않으며 loopback 요청에 프록시를 사용하지 않는다.

| 종료 코드 | 의미 |
|---|---|
| 0 | 후보 앱 배포 및 health/readiness 성공 |
| 30 | 배포 실패, 기존 앱 파일 원복·API 재생성·health/readiness 성공 |
| 31 | 원복 복사/API 재생성/health 중 하나 실패; 수동 복구 필요 |
| 75 | 같은 버전 스크립트의 다른 실행이 lock 보유 |
| 기타 | 필수 파일/인자/백업 등 사전 오류의 코드 |

원복 실패 위치와 백업 경로를 stderr에 기록한다. 원복에 성공해도 배포 성공 코드로 바꾸지 않는다. 백업과 lock inode는 삭제하지 않는다.

## 범위와 한계

- 앱 파일 원복만 제공한다. DB·첨부파일·보호키를 이전 상태로 자동 복구하지 않는다. 시작 시 DB 정상화와 배포 중 사용자 쓰기는 별도 백업/업무 중단 계획으로 다룬다.
- INT/TERM은 처리하지만 SIGKILL, 전원 차단, 디스크 자체 고장에 대한 자동 복구는 보장하지 않는다.
- rsync 체크섬은 파일 내용을 읽으므로 기존 크기/시간 비교보다 디스크 I/O가 증가한다. 운영 소요시간은 아직 측정하지 않았다.
- `HEALTH_CHECK_TIMEOUT_SECONDS`와 `ROLLBACK_HEALTH_TIMEOUT_SECONDS`는 각 준비 검사 구간의 경과시간 한도다. 직접 실행 기본값은120초이며 원복 값 미지정 시 같은 값을 쓴다. 공식 Publisher/wrapper는 각각900초(15분), 허용 범위1~3600초다. `-ReleaseHealthTimeoutSeconds`, `-RollbackHealthTimeoutSeconds`로 각각 지정할 수 있다.
- HTTP 요청은 최대10초/연결3초를 쓰되 남은 구간 시간보다 길어지지 않는다. healthz와 readyz는 같은 시간 예산을 공유한다. 기한 뒤 HTTP200도 성공이 아니며 원복은 독립된 시간 예산으로 검사한다. 정수 초 계산/프로세스 스케줄링 오차는 있다. 파일 복사·Docker 명령 시간을 포함한 전체 배포 시간 제한은 아니다.
- 옛 `HEALTH_CHECK_RETRIES` 변수가 있으면 앱 변경 전 중단한다. 지원하지 않는 옛 운영 스크립트에서는 Publisher가 업로드 전과 실제 apply 직전에 `--capabilities`를 확인하고 중단한다. 이 검사는 선택적 플랫폼 health 점검을 생략해도 수행한다.
- 이 파일의 lock은 이 버전을 사용하는 실행끼리 보호한다. 기존 버전 실행과의 혼재를 막으려면 스크립트 교체 시 배포가 진행 중이지 않은지 확인해야 한다.
- health/readiness 성공은 로그인/업무/동기화/업체 권한의 수락 검증을 대체하지 않는다.

## 검증

Linux에서 다음과 같이 실행한다. real rsync/cp는 전용 임시 파일만 다루며 Docker/sg/sleep은 모의 명령이다. HTTP 한 시나리오는 실제 curl과 임시 loopback 서버, 나머지는 모의 curl을 사용한다. 운영 DB·HTTP 서버·Docker daemon에 접근하지 않는다.

```sh
python3 tools/verification/Test-GeoraePlanReleaseRollback.py \
  --script tools/linux/assets/georaeplan-release/apply-release.sh \
  --output /tmp/georaeplan-release-verification-result.json
```

2026-09-23 최초17개 검사 이후 시간 제한을 추가한 최종 **27/27 통과**, 시험 폴더 정리 확인. 정상 배포, 부분 복사, API 실패, health/readiness 실패·리다이렉트, INT/TERM, 원복 실패, 사전 실패, lock 충돌, Docker 그룹 대체 경로, 잘못된 시간 설정/옛 환경변수, 실제 지연 HTTP·공유/원복 시간 예산을 검사했다. 같은 크기/mtime의 다른 파일과 이전/새 버전에만 존재하는 파일도 해시로 검증한다.

최신 증거: `C:\Users\beene\Documents\Codex\tradeplan-release-deadline-20260923\rollback.json`. 최초17개 근거는 `tradeplan-release-rollback-20260923\linux-verification.json`에 보존했다.

## 운영 반영 전 필수 순서

1. `SERVER_PROMOTION_PREFLIGHT_20260923.md`의 보호된 DB 3개·첨부·키 백업, 최신 SSD2 사본, 업무 쓰기 처리, release 보존, 사용자 Git 변경 분리, 사용자 수락 조건을 완료한다.
2. 현재 운영 스크립트 SHA-256을 다시 읽고 기준 `75ad0948b701ac32cb1f1396d1e2cb811058e50f9540313f34d5705b9e1390a4`와 비교한다. 다르면 이 후보를 덮어쓰지 말고 변경 내용을 재검토한다.
3. 후보 해시를 최종 검증 보고서와 대조하고 `bash -n`을 통과시킨다. 배포가 진행 중이지 않은 상태에서 기존 스크립트를 별도 보존하고 같은 디렉터리의 임시 파일을 거쳐 원자적으로 교체한다. 기존 소유자/권한을 보존한다. `.env` 내용을 출력하거나 복사하지 않는다.
4. **스크립트 설치 자체는 앱 배포를 실행하지 않는다.** 설치 후 해시/구문 검사로 확인한다. 문제 시 보존한 스크립트 한 파일만 원복한다.
5. 실제 릴리스는 사용자 수락 후 공식 `테스트 시행/검증완료-반영.ps1` 경로를 사용한다. 현재 소스로 stable을 재생성하고 테스트 서버 파일을 운영에 복사하지 않는다. 기존 Publisher의 release 정리 및 전체 tracked Git stage 동작은 별도로 안전성을 확보해야 한다.
6. 배포 후 실제 업무 흐름과 패키지/manifest를 검증한다. 코드 30/31에서는 추가 배포를 중단하고 기록된 백업과 서비스를 점검한다. 새 업무 쓰기가 생긴 DB를 무조건 과거 백업으로 덮어쓰지 않는다.

## 한 파일 교체 도구

`tools/linux/Install-GeoraePlanReleaseGuard.py`는 Python 3/Linux용 설치 보조 도구다. 기본은 읽기 전용이고 `--apply`를 명시해야 교체한다. 대상은 `/srv/georaeplan/ops/apply-release.sh`로 고정되며 앱이나 서비스를 실행하지 않는다.

검토한 전용 원격 폴더에 도구와 후보를 보낸 뒤 먼저 다음 형태로 점검한다. `CANDIDATE_PATH`는 실제 업로드한 후보 한 파일의 절대 경로다.

```sh
python3 Install-GeoraePlanReleaseGuard.py \
  --candidate "$CANDIDATE_PATH" \
  --expected-current 75ad0948b701ac32cb1f1396d1e2cb811058e50f9540313f34d5705b9e1390a4 \
  --expected-candidate fbf0caf15a2efa3a9756642c4b5d602acd5019fcdaaeef867adc9f987f5a52b2
```

반영 조건을 충족한 후 같은 명령에 `--apply`를 추가한다. 결과 JSON의 `backup` 경로를 보존한다. 기존 파일과 후보 해시/구문/단일 링크/추가 메타데이터, 배포 프로세스 부재와 flock 잠금을 검사하고 소유자·그룹·권한을 유지한다. 같은 디렉터리에 기존 파일 백업과 후보를 fsync한 뒤 원자적으로 교체한다. 사후 검증 실패 시 원본을 복원한다. 제3자가 교체한 내용은 덮어쓰지 않고 수동 확인을 요구한다.

의도적 원복은 결과의 정확한 `backup`을 후보로 전달하고 두 해시를 반대로 지정해 같은 도구를 사용한다. 그 과정에서도 교체 직전 파일을 새 백업으로 남긴다. 백업·lock을 자동 삭제하지 않는다. 실행 중인 옛 스크립트는 flock을 사용하지 않으므로 다른 작업자가 점검과 교체 사이에 새 배포를 시작하지 않도록 배포 작업을 단독 수행해야 한다. 전원 차단·SIGKILL 시 자동 원복을 보장하는 도구는 아니다.

2026-09-23 `Test-GeoraePlanReleaseGuard.py` Linux 격리 검사 **20/20 통과**. 운영 경로는 `--apply` 없이 확인했고 스크립트/컨테이너 ID·시작 시각·재시작 횟수·readyz가 전후 동일했다. 자세한 근거는 `RELEASE_GUARD_INSTALL_VERIFICATION_20260923.md`를 참조한다.

시간 제한이 추가된 최신 후보에서도 한 파일 도구20개 검사와 운영 읽기 전용 점검을 다시 통과했다. 최신 해시와 공식 호출자 검증은 `RELEASE_DEADLINE_VERIFICATION_20260923.md`를 따른다. 옛 유즈넷 DB 분리 도구도 새 시간 변수를 전달하고 변경 전 capability를 확인한다. 이미 완료된 DB 분리 작업을 재실행한 것은 아니다.
