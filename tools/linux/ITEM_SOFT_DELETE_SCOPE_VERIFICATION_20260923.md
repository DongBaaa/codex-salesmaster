# 품목 삭제 조회와 계정별 API 권한 검증

## 사전 영향도와 결정

격리 API에서 ITWORLD 품목을 삭제한 뒤 같은 계정으로 조회하면 HTTP 200이 반환됐다. DB에는 IsDeleted=true가 저장돼 있었다. 내부 이동 수령 품목을 조회 범위에 포함하는 하위 쿼리의 IgnoreQueryFilters가 합성된 바깥 품목 쿼리의 삭제 필터까지 해제하는 원인이다.

일반 품목 목록·상세·재고 조회에는 삭제 필터를 적용하고, 동기화와 휴지통에서 명시적으로 요청하는 삭제 기록은 유지해야 한다. 소유회사·담당지점·업체 DB 분리 및 쓰기 권한은 바꾸지 않는다. 서버 쿼리의 필터 선택권을 호출자에 유지하는 최소 수정으로 로컬 검증 진행 가능하다. 운영 데이터·원본 PC의 dirty/Outbox를 복구하는 작업과는 별개다.

## 수정 파일

- Server/거래플랜.Server.Api/Services/OfficeScopeService.cs: 수령 품목 하위 쿼리에서 IgnoreQueryFilters 3개를 제거했다. 활성 이동/행/품목 및 tenant/지점/창고 조건은 유지했다.
- Tests/GeoraePlan.Server.Api.Tests/OfficeScopeAndPagingTests.cs: ITWORLD·USENET·YEONSU·전체관리자의 일반 삭제 제외 및 명시적 동기화 삭제 기록 보존 4개 사례를 추가했다.
- Tests/GeoraePlan.Server.Api.Tests/InventoryTransferScopeGuardTests.ReceivedItemAccess.cs: 삭제된 원본 품목은 수령 이력으로 조회 권한을 얻지 않는 사례를 추가했다.
- Tests/GeoraePlan.Server.Api.Tests/SyncPushCoverageTests.cs: Git worktree의 .git 파일도 저장소 표식으로 인정한다. 제품 동작 변경은 없다.

## 검증 결과

- 수정 전 새 회귀 검사: 4개 중 3개 실패로 재현(전체관리자 1개 통과).
- 수정 후 관련 검사: 46/46 통과. 수령 재고 조회·판매와 원본 품목 수정 제한을 포함한다.
- 최초 전체 검사: 1,691 통과·7 실패·24 생략. 실패 7개는 worktree .git 파일을 인식하지 못한 검사 경로 탐색 문제였고, 24개는 PostgreSQL 연결 미설정으로 생략됐다.
- 경로 탐색 수정 후 임시 PostgreSQL 17을 사용하는 전체 서버 검사: **1,722/1,722 통과, 실패·생략 0**. 임시 서버 정상 종료와 소유 클러스터 정리까지 성공했다.
- 실제 로컬 HTTP API 및 세 계정의 합성 거래처·품목 6행 시험: **105/105 통과**. 목록·상세, 타 업체/지점의 PUT/DELETE 거부, 요청 본문 scope 위조 거부, 미인증 거부, 자기 자료 수정·재조회·삭제 후 404, 이름별 계정 검사 PASS, SQLite 무결성을 확인했다.
- 금지 요청마다 전체 테이블 해시를 대조해 변경이 없음을 확인했다. 허용 요청 이후 합성 6행을 제외한 변경 테이블은 AuditLogs와 SyncRevisionStates뿐이었다.
- 기존 실제 자료의 ITWORLD 거래처가 0개였던 한계는 합성 양성 사례로 보완했다. 이를 실제 ITWORLD 업무 자료가 검증됐다는 뜻으로 확대하지 않는다.

## 시험 중 실패와 범위

첫 실제 API 시험은 삭제 품목 재조회 오류로 중단됐다. 수정본 첫 두 시도는 HTTP 응답 읽기 중 연결 재설정으로 완료하지 못했다. 이후 읽기 GET에만 1회 재시도와 제한된 준비 대기를 적용했다. 변경 요청은 재시도하지 않았다. 최종 105개 시험에서도 GET 연결 재설정 1회 후 정상 응답을 받았으며 원인은 확정하지 않았다. 반복 실패 로그를 보존했다.

중간 시험의 YEONSU 거래처 저장 HTTP 200을 실패로 판정한 것은 시험의 기대값 오류였다. 거래처 소유회사는 USENET, 담당지점은 YEONSU로 저장하는 기존 규칙을 확인했다. 합성 거래처와 기대값을 이 규칙에 맞췄으며 제품 권한 규칙은 변경하지 않았다. 품목의 지점 소유권 규칙과 거래처 규칙을 혼합하지 않았다.

## 증거와 보호

증거 루트: C:\Users\beene\Documents\Codex\tradeplan-api-scope-matrix-20260923

- tests/soft-delete-before.trx, tests/scope-after.trx, tests/server-full.trx
- tests-full-pg/server-full-postgres.trx, server-full-pg.log
- fixed4/result.json, checks.json, preservation.json, table-digests.json, account-scope-positive.md
- 이전 run 및 fixed/fixed2/fixed3 실패 증거도 유지

수정본 격리 API DLL SHA256: b20c739695a7f971afdbdf3831f210b67fd6fdd0958fc3804bda4e59c7ff2e07

실제 API 시험 전후 원본 PC DB, 기존 공식 시험 DB/API DLL, 체크리스트와 원본 Git index 해시가 같고 시험 서버는 종료됐다. 임시 계정 비밀번호는 메모리에서만 생성·사용했다. 운영 DB 쓰기, 서비스 재시작, live 배포, commit/push는 하지 않았다.

## 공식 환경 후속 상태

공식 준비 첫 시도는 계정 스냅샷 유효기간 초과로 안전하게 중단했다. 운영에서 읽기 전용으로 4계정·62권한·4scope를 새로 조회했고, 정규화 권한 해시는 이전과 동일한 4BFC303B275A62DCCE9DFD499FDCDA3BFD061F8132AA0005837FB5D49D45EAA3이다. 새 스냅샷으로 공식 준비를 재실행해 종료 0으로 완료했다. 기존 격리 앱 자료를 보존하는 SkipDataCopy를 사용하며 서버 시드와 인증은 생략하지 않는다.

공식 실행 확인과 사용자 수락은 별도로 완료해야 한다. 운영 반영 완료로 해석하지 않는다.


## 공식 준비와 Run-All 최종 결과

공식 기록은 테스트 시행/기록/20260923-161027이다. 전체 Release 빌드는 경고·오류 0, 시드는 기존 3회 절차로 최종 sync_ok=True/dirty_count=0/non_acknowledged_outbox_count=0을 확인했다. 인증 ID b5a9cf4f87674dc9ac573bf76b578d3a, 공식 API DLL SHA256 7f2299ca15c2be09ded83c8a13547f22b75534112b07009de1f8c7bac6eb069a이며 원본 Release 출력과 동일하다. 이 DLL은 검토 worktree 산출물과 파일 해시가 다르지만 서버 소스 100개 원본/검토본 바이트 일치, 기존 데스크톱·패키징 입력 374개 보존을 별도 확인했다.

Run-All.cmd 실행으로 실제 admin/USENET 메인 업무 화면과 품목 목록 2,080건을 확인했다. 처음 왼쪽에 열린 창을 오른쪽 모니터로 옮긴 뒤 품목 화면을 확인했으며 입력은 조회와 정상 종료만 했다. 업무 데이터 추가/수정/삭제는 하지 않았다. 앱 exitCode=0, 시험 서버 종료, 서버 SQLite sidecar=0, Run-All 오류 로그 0바이트를 확인했다. UI 전체 회귀를 다시 수행했다는 뜻은 아니다. 기존 업무 경고 181건 및 별도 사업 자료 확인 과제는 유지한다.

시험 manifest는 설치 가능한 desktop 업데이트가 없으므로 desktop 항목을 광고하지 않았다. 이미 검증한 별도 1.1.743 stable 설치패키지가 실패했다는 의미가 아니다. 공식 시험 실행과 배포 패키지 검증을 구분한다.

최종 원본 PC DB 674e0022...·Git index 8e01c7f8...·원본 desktop 버전 파일 8f4433c9...는 그대로다. 테스트 체크리스트는 공식 준비기가 새로 생성했으며 사용자 수락 체크는 추가하지 않았다. 사용자 수락, 보호된 운영/Git 반영과 원본 미전송 자료 복구는 미완료다.
