# 거래플랜 live 반영 체크리스트

- 용도: Linux PC live/stable 반영 전 최종 확인
- 작성 원칙: 체크 후 반영, 문제 발견 시 즉시 반영 보류

## 1. 기본 준비
- [x] `최근 수정 파일.md` 내용을 확인했다.
- [x] 이번 반영이 테스트 버전이 아니라 현재 소스 기준 live/stable 배포본임을 확인했다.
- [x] `D:\거래플랜\테스트 시행\검증 체크리스트.md` 기준 테스트를 먼저 완료했다.

## 2. 버전 / 업데이트 정책
- [x] 반영 전 데스크톱 버전이 기존 live manifest보다 높았고, 반영 후 `1.1.696`으로 일치한다.
- [ ] 필수 업데이트로 배포할 경우 `minimumSupportedVersion` 정책을 함께 정했다.
- [x] `Invoke-LiveReleaseReadinessCheck.ps1 -Mode Pre` 를 통과했다.

## 3. 패키지 구성 확인
- [x] 설치 패키지에 `Updater\거래플랜.Updater.exe` 가 포함된다.
- [x] 설치 패키지에 `appsettings.json` 이 포함된다.
- [x] 설치 패키지에 `Install-GeoraePlan.ps1` 이 포함된다.
- [x] `Invoke-LiveReleaseReadinessCheck.ps1 -Mode Post` 를 통과했다.

## 4. 다중 PC / 동기화 확인
- [ ] 필요 시 `준비-다중PC-검증.ps1` 로 PC-A / PC-B 분리 캐시 검증을 수행했다.
- [ ] 필요 시 `Invoke-MultiPcReadinessCheck.ps1` 로 다중 PC 실행 구성 자체를 점검했다.
- [x] dirty 데이터가 남아 있으면 업데이트가 차단되는지 확인했다.
- [x] 시작 시 무결성 점검 / 전체 재동기화 안내가 비정상 반복되지 않는지 확인했다.
- [ ] 재고/렌탈 연동 수정이 포함되면 무결성 리포트에 재고 스냅샷 불일치가 남지 않는지 확인했다.

## 5. 반영 결정
- [x] 문제 없음 → Linux PC 반영 가능
- [x] 문제 없음 → Git 반영 가능
- [ ] 이슈 있음 → Linux PC/Git 반영 보류
- [x] 반영 직후 `Invoke-LiveObservationCheck.ps1` 로 live 서버 healthz/manifest/package 응답을 관찰했다.

## 메모
- release id: `desktop-1.1.696-20260826-2200-r2`
- 반영 desktop.version: `1.1.696` / 반영 전 public stable: `1.1.695`
- public healthz/readyz 확인 시각: `2026-08-26 22:08 KST` / 응답 `ok` / `ready`
- 이번 배포는 mandatory 업데이트 아님 상태를 유지해 `minimumSupportedVersion` 은 빈 값으로 남겼습니다.
- Android는 이번 변경 범위가 아니므로 public stable `0.2.82` 자산을 보존합니다.
- 기존 운영 데이터 경고 `rental_profile_customer_unlinked(1)`, `rental_asset_template_monthly_mismatch(1)`만 명시 허용하고 신규 경고는 차단합니다.
- public ZIP SHA-256: `919A3759139AB4F3EF10183E14F8AC9B17E33877E5530CA4D792FC3AFEBAF591`; 로컬·manifest·Linux live 일치.
- Windows Authenticode 인증서 환경변수가 없어 EXE/MSI는 `NotSigned`; 버전·내용·SHA와 업데이트 경로는 검증 완료했습니다.

## 2026-09-02 Desktop 1.1.703 / USENET DB 분리·RT 이관

- [x] 테스트 실행환경을 재구성하고 `Run-All.cmd`에서 자동 로그인·대시보드 109건·2회 동기화·ITWORLD/USENET 캐시 갱신·오류 0을 확인했다.
- [x] RT 원본 768대가 USENET 219대·ITWORLD 549대로 최종 수렴했고 양쪽 최종 계획이 각각 0건임을 확인했다.
- [x] USENET/ITWORLD 활성 자산의 관리번호 중복 0건, 고아 청구프로필 연결 0건을 확인했다.
- [x] 서버 전체 테스트 1,496건 통과, 실패 0, PostgreSQL 환경 의존 20건 건너뜀을 확인했다.
- [x] 데스크톱 회귀 3,652건 중 3,649건 통과 후 문서 버전 3건과 고정 기대값을 수정하고 문서 검사 클래스 전체 27/27 통과를 확인했다.
- [x] 전체 솔루션 Release 빌드 경고 0, 오류 0을 확인했다.
- [x] 데스크톱 버전과 stable manifest가 `1.1.703`으로 일치하고 Android `0.2.82` 자산이 보존됨을 확인했다.
- [x] ZIP·EXE·MSI를 재생성했고 Live 응답 HTTP 200 및 로컬·Live 크기/해시 일치를 확인했다.
- [x] 거래플랜 API·PostgreSQL 컨테이너 정상, `trade.2884.kr/healthz` HTTP 200을 확인했다.
- [x] 공통 인프라 영향 확인으로 `work.2884.kr`·`itw.2884.kr` HTTP 200을 확인했다.
- [x] 분리 후 중앙·ITWORLD·USENET 3개 DB 신규 백업, 외장 복제, 격리 복구훈련 성공을 확인했다.
- [ ] Windows EXE/MSI Authenticode 서명 환경을 구성한다. 현재 산출물은 `NotSigned`이다.

### 1.1.703 릴리스 근거

- 서버 릴리스: `/srv/georaeplan/releases/20260902-1703-rt-usenet`
- 백업 run ID: `20260902T075826Z-1076603`
- 외장 복제 manifest SHA-256: `1373c2e0c236bdb79a67baee9437fde597895803b040879f3b24050c692de1ce`
- ZIP SHA-256: `6E3F214E8544D99C41884154DA187F38F207AAF98F70EF1D76871B3A7ADEBBDA`
- EXE SHA-256: `3928B9D171220B515667C1827714A46FDAC0A5F924B48A6060BC0F332E7B6808`
- MSI SHA-256: `BC6CAC02583149906379E8B29C918D4F18B46412E519ACA20F1B07CA9CF5D7BB`
- 필수 업데이트 정책은 변경하지 않았고 Android는 기존 `0.2.82`를 유지했습니다.
