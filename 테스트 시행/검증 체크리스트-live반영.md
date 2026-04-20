# 거래플랜 live 반영 체크리스트

- 용도: NAS live/stable 반영 전 최종 확인
- 작성 원칙: 체크 후 반영, 문제 발견 시 즉시 반영 보류

## 1. 기본 준비
- [x] `최근 수정 파일.md` 내용을 확인했다.
- [x] 이번 반영이 테스트 버전이 아니라 현재 소스 기준 live/stable 배포본임을 확인했다.
- [x] `D:\거래플랜\테스트 시행\검증 체크리스트.md` 기준 테스트를 먼저 완료했다.

## 2. 버전 / 업데이트 정책
- [x] 데스크톱 버전이 현재 live manifest 보다 높다.
- [ ] 필수 업데이트로 배포할 경우 `minimumSupportedVersion` 정책을 함께 정했다.
- [x] `Invoke-LiveReleaseReadinessCheck.ps1 -Mode Pre` 를 통과했다.

## 3. 패키지 구성 확인
- [x] 설치 패키지에 `Updater\거래플랜.Updater.exe` 가 포함된다.
- [x] 설치 패키지에 `appsettings.json` 이 포함된다.
- [x] 설치 패키지에 `Install-GeoraePlan.ps1` 이 포함된다.
- [x] `Invoke-LiveReleaseReadinessCheck.ps1 -Mode Post` 를 통과했다.

## 4. 다중 PC / 동기화 확인
- [ ] 필요 시 `준비-다중PC-검증.ps1` 로 PC-A / PC-B 분리 캐시 검증을 수행했다.
- [x] 필요 시 `Invoke-MultiPcReadinessCheck.ps1` 로 다중 PC 실행 구성 자체를 점검했다.
- [x] dirty 데이터가 남아 있으면 업데이트가 차단되는지 확인했다.
- [x] 시작 시 무결성 점검 / 전체 재동기화 안내가 비정상 반복되지 않는지 확인했다.
- [ ] 재고/렌탈 연동 수정이 포함되면 무결성 리포트에 재고 스냅샷 불일치가 남지 않는지 확인했다.

## 5. 반영 결정
- [x] 문제 없음 → NAS 반영 가능
- [x] 문제 없음 → Git 반영 가능
- [ ] 이슈 있음 → NAS/Git 반영 보류
- [x] 반영 직후 `Invoke-LiveObservationCheck.ps1` 로 live 서버 healthz/manifest/package 응답을 관찰했다.

## 메모
- 
