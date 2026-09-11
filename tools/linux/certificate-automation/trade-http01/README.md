# 거래플랜 인증서 자동 갱신 운영 코드

2026-09-11 실제 NAS 설치·시험 발급·운영 인증서 적용·예약 연결을 검증한 코드다. NAS는 인증서와 외부 프록시를 담당하며, 거래플랜 앱과 DB는 Linux PC `/srv/georaeplan`에서 운영한다.

## 현재 운영 상태

|항목|설정|
|---|---|
|도메인|`trade.2884.kr`|
|인증서 ID|`arrkjO`|
|서비스|`ReverseProxy/24c4ba80-609c-434a-ad0c-862c966d81e2`|
|갱신 방식|HTTP-01, 만료 45일 이내에 발급·적용|
|DSM 작업|`tradeplan-certificate-auto-renew`, root·활성·매일 06:35, 같은 날 반복 없음|
|실행 명령|`/bin/sh /volume1/workplan-certificate-automation/trade-http01/bin/run-scheduled.sh`|
|실패 알림|DSM 관리자 이메일, 비정상 종료 때만 전송|
|런타임|`/volume1/workplan-certificate-automation/trade-http01`|
|트랜잭션·백업|`/volume1/workplan-certificate-automation/trade-native-staging`|
|HTTP 설정|`/usr/local/etc/nginx/sites-enabled/tradeplan-http01.conf`|
|webroot|`/volume1/web/tradeplan-acme-http01-4drjksx1`|
|현재 인증서 만료|2026-12-10 12:54:02 KST|
|현재 인증서 SHA-256|`c7457800d79ca99b0cb2fe429665c80444516d7a5cbe876177b862bb52553f7f`|
|HTTP 설정 SHA-256|`728d47b944b9a1116b7c0e29954d040f1873831e68b10148c9c379a7f34f9c2e`|

워크플랜의 `work-http01` 및 `native-staging`과 파일·상태·잠금을 분리했다. 공통 옛 DNS-01 실행 파일과 과거 검증 마커는 보존하지만 정규 Trade 예약에서는 사용하지 않는다.

## 검증 근거

- 설치: 2026-09-11 13:38:26~36 정상0, Nginx재적용1회. 새 HTTP파일만 추가하고 기존 설정·공개 인증서를 보존했다.
- 시험 인증서: 13:49:24~46 정상0. 시험 발급기관·SAN·키 일치·유효기간 검사 및 4개 도메인 공개 인증서 보존.
- 운영 발급·기본 API 적용: 13:52:20~47 정상0. 실제 새 인증서 지문이 공개 TLS와 일치, 다른 도메인 보존.
- 정규 예약 수동검증: 13:57:22 정상0. 같은 시각 `not_due` 상태, 새 인증서 유지.
- 실제 트랜잭션 `run-e25af70f2cd34e289cbdaf67f07ff892`는 `deployed`/API acknowledged. 기존 인증서 2세트 8파일과 INFO 백업을 보존한다. 파일 내용 해시는 API 호출 전에 대조했고, 사후 읽기진단에서 8파일 root·0600·링크 없음 확인.
- 런타임·설치기 80개 격리 검사와 셸문법2개, 1회 검증 실행기7검사 통과. 코드 복사 후 SHA 일치를 확인했다. 격리 검사에서는 NAS API/root와 Nginx 효과를 모의 처리하며, 위 실제 운영 결과와 구분한다.
- 실제 예정 시각에 의한 실행, 메일 실제 수신, 인증서 백업의 실제 원복은 아직 별도 미확인이다. 예약 수동 실행이나 백업 파일 존재만으로 이를 완료 처리하지 않는다.

## 점검 및 장애 대응

NAS에서 `trade-http01/state/last-status-trade.2884.kr.txt`의 시각과 상태, DSM 최근 실행 결과를 함께 확인한다. 만료까지 45일보다 많이 남았다면 `not_due`는 정상이며 새 발급을 뜻하지 않는다. 현재 인증서는 위 날짜와 지문으로 확인할 수 있고, 이후 정상 갱신 시 값이 달라진다.

실패하면 해당 실행의 비공개 로그와 `trade-native-staging/run-*/journal.json`을 확인한다. `importing`, `verifying`, `recovery-required` 등 미완료 트랜잭션이 있으면 API 요청을 반복하지 않는다. 진행 중 프로세스·공개 TLS·현재 인증서와 서비스 매핑을 먼저 대조한다. 인증서 원복은 기존 요청 종료, 현재 상태 관측값 및 백업 무결성 확인을 거치는 `native_deploy.py`의 명시적 절차를 사용한다. 개인키를 터미널이나 보고서에 출력하지 않는다.

정규 운영은 위 예약 진입점만 사용한다. `install_trade.py --install`, 저장된 `dsm-command.sh`, `verification/run-once.py`는 당시 설치·시험용이며 다시 실행하지 않는다. 설치기가 기존 런타임/HTTP파일/트랜잭션을 발견하면 덮어쓰지 않고 거부하도록 되어 있다. 일회 검증 보호 폴더와 과거 트랜잭션을 제거해 재실행을 강제하지 않는다.

기존 HTTP 설정 파일이 없는 상태에서의 새 파일 설치·조건부 제거를 구현한 것이므로 업데이트/재설치 도구로 사용하지 않는다. 향후 변경은 실제 경로·지문·기존 상태를 새로 확인한 뒤 별도 패키지와 원복 범위를 검증한다.

## 개발 검증

Windows에서 `python tools/linux/certificate-automation/trade-http01/verify.py`는 지정된 Linux PC의 일반 계정으로 임시 폴더를 만들어 격리 검증한 뒤 정리한다. NAS 설치나 실제 인증서 발급은 수행하지 않는다. SSH 호스트·키 경로는 실행 PC 환경을 확인한 뒤 사용한다.

`installed-package-manifest.json`은 2026-09-11 설치 당시 패키지의 고정 해시 기록이다. 저장소 보존본은 줄바꿈과 보조 모듈의 마지막 빈 줄을 정리했으며, `package-manifest.json`은 이 보존본으로 재생성한 값이다. 운영 런타임4개의 바이트는 NAS 설치본과 일치한다. `payload-hashes.json`의 값도 동일하지만 JSON의 줄바꿈을 LF로 통일했다. `install_support.py`는 설치 보조 함수이고 `install_trade.py`가 Trade 설치 진입점이다. 신규 변경 시 기록된 해시와 시험 결과를 현재 결과로 오인하지 않는다.
