# 거래플랜 Codex 작업 규칙

## 세션 공통 사전점검 규칙
- 모든 비단순 작업은 수정 전에 반드시 **사전 영향도 점검**부터 한다.
- 사전점검 항목은 최소 아래를 포함한다.
  - 영향 화면/메뉴
  - 데이터 범위(scope): 자산 / 품목 / 청구 / 거래처 / 동기화
  - 업체/지점 구분: ITWORLD / USENET / YEONSU / 공용 처리 방식
  - 저장 가능 범위와 조회 가능 범위가 서로 다르지 않은지 여부
  - live 반영 시 기존 사용자 데이터와 dirty 동기화에 미치는 영향
- 점검 결과 **오류 가능성, 꼬일 가능성, 설계 충돌, 확인되지 않은 가정**이 하나라도 있으면 먼저 사용자에게 브리핑하고, 정리 없이 바로 수정/배포하지 않는다.
- 사전점검 결과가 명확히 `진행 가능`일 때만 구현을 진행한다.
- scope 관련 변경은 아래 5가지를 각각 분리해 확인한다.
  - 자산 조회
  - 품목관리
  - 청구/전표
  - 거래처 선택/조회
  - 동기화/dirty/권한 저장
- `절대 문제 없음`을 추정으로 말하지 않는다. 항상 근거(코드 경로, 데이터 범위, 검증 결과)로 설명한다.

## 공통 테스트/반영 규칙
- 거래플랜의 데스크톱/서버 수정은 먼저 `D:\거래플랜\테스트 시행` 기준으로 **내 PC 로컬 테스트 서버 선검증**을 진행한다.
- 기본 순서는 아래와 같다.
  - `D:\거래플랜\테스트 시행\테스트-환경-준비.ps1` 실행
  - 현재 로컬 앱 데이터 스냅샷을 `D:\거래플랜\테스트 시행\실행환경\AppData` 로 복사하고, 분리된 테스트 서버 DB를 다시 시드
  - `D:\거래플랜\테스트 시행\최근 수정 파일.md` 확인
  - `D:\거래플랜\테스트 시행\검증 체크리스트.md` 기준 검증
  - `D:\거래플랜\테스트 시행\실행환경\Run-All.cmd` 로 테스트 서버+앱 확인
- 테스트 확인이 끝나기 전에는 Linux PC live 반영과 Git 반영을 진행하지 않는다.
- 사용자가 **"문제 없다"**, **"완벽하다"**, **"올려도 된다"** 같은 의미로 확인한 뒤에만 Linux PC live 반영과 Git 반영을 진행한다.
- Linux PC에는 테스트 서버/테스트 앱을 올리지 않고, **현재 소스에서 다시 생성한 메인(live) stable 배포본만** 반영한다.
- 데스크톱 앱 수정이 포함된 live 반영일 때는 반드시 `D:\거래플랜\Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj` 의 `Version` / `FileVersion` 을 현재 stable 배포본보다 높게 올린 뒤 빌드/배포해, 기존 설치 PC에 업데이트 알림이 뜨도록 유지한다.
- 반영 시에는 `D:\거래플랜\테스트 시행\검증완료-반영.cmd` 또는 `D:\거래플랜\테스트 시행\검증완료-반영.ps1` 를 사용한다.
- 반영 스크립트는 `검증 체크리스트.md` 의 `문제 없음 → Linux PC 반영 가능`, `문제 없음 → Git 반영 가능` 체크가 없으면 실행되지 않도록 유지한다.

## 모바일 수정/배포 규칙
- 모바일 기능을 수정하면 먼저 에뮬레이터 또는 실기기 기준으로 아래 핵심 흐름을 점검한다.
  - 품목탭
  - 거래처탭 → 전표작성
  - 동기화
- 점검 결과를 사용자에게 보고하고, 사용자가 **"이상 없다"** 또는 같은 의미로 확인하기 전에는 모바일 버전을 올리지 않는다.
- 사용자의 확인을 받은 뒤에만 아래 순서로 진행한다.
  - `ApplicationDisplayVersion` / `ApplicationVersion` 증가
  - APK 재빌드
  - 업데이트 자산 재생성
  - Linux PC 배포
- 모바일 배포본은 항상 `D:\거래플랜\배포` 경로에 최신 APK를 둔다.

## Linux PC 운영 안전 규칙

- 앞으로 거래플랜, 워크플랜, itw 홈페이지는 한 번에 하나의 서비스만 작업한다.
- 거래플랜 작업 중에는 워크플랜/itw 홈페이지 배포, 재시작, 정리 작업을 함께 진행하지 않는다.
- 현재 거래플랜 서버 본체는 Linux PC `itw@192.168.0.199:2222`의 `/srv/georaeplan` 기준으로 운영한다.
- Docker 전체 재시작/정리 명령은 금지한다.
  - 금지: `docker compose down`, `docker system prune`, `docker container prune`, `docker image prune`, `docker volume prune`, `docker stop $(docker ps -q)`, `docker restart $(docker ps -q)`, `sudo reboot`, `sudo systemctl restart docker`.
  - 허용: Linux PC의 `/srv/georaeplan` 거래플랜 compose project 안에서 명시 서비스만 대상으로 하는 `compose up -d postgres`, `compose up -d --force-recreate api`.
- Linux PC의 Docker daemon, systemd 전체 서비스, nginx/Reverse Proxy 전체 재시작, PostgreSQL 전체 재시작은 다른 서비스까지 영향을 줄 수 있으므로 먼저 보고하고 승인 후 진행한다.
- live 반영 전에는 `trade.2884.kr` 상태와 Linux PC의 거래플랜 API/DB/로그 상태를 확인한다.
- 공통 인프라 영향 가능성이 있으면 `work.2884.kr`, `itw.2884.kr` 상태도 함께 확인한다.
- live 반영 후에도 `trade.2884.kr`와 Linux PC 로그에서 502, timeout, Docker daemon, PostgreSQL 연결 오류 여부를 확인한다.
- 장애가 발생하면 추가 배포를 중단하고 서비스별 원인 분리 결과를 먼저 보고한다.
- 새 운영 작업은 `tools\\linux`와 Linux PC 기준 절차만 사용한다.
