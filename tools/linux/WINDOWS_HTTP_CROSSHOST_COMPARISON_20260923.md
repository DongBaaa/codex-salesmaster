# Windows 로컬 HTTP 종료 오류의 다른 호스트 비교 — 2026-09-23

## 결론

업무 코드 없는 같은 Windows Python 소켓 서버를 Windows 내부 연결, Linux의 SSH 경유 내부 연결, Linux의 직접 LAN 연결로 비교했다. **오류는 이번 시험에서 Windows 내부 경로를 거친 Connection: close 요청에 집중됐다.** Windows/Python 클라이언트 하나만의 문제로 제한할 수 없다는 추가 근거다. 특정 OS 구성 요소·드라이버·보안 제품의 책임은 확정하지 않았고 오류 해결 완료도 아니다.

이전 `WINDOWS_HTTP_CLOSE_DIAGNOSIS_20260923.md`의 최소 .NET/Python 서버 대조를 보완한다. 제품 코드에 자동 재시도나 임의 연결 정책을 추가하지 않았다.

## 실제 비교

각 조건은 동시 연결 2개, 80회, 자동 재시도 없음이다. `Connection: close` 유무만 달리한 짝을 구성했다. 원격 비교 전후 Windows 시험을 넣어 일시적인 회복을 정상 대조군으로 오인하지 않도록 했다.

| 클라이언트와 경로 | close 없음 오류/요청 | close 있음 오류/요청 |
| --- | ---: | ---: |
| Windows → Windows loopback, 원격 비교 전 | 0/80 | 10/80 |
| Linux → SSH 역방향 전달 → Windows loopback | 0/80 | 5/80 |
| Linux → Windows LAN 주소 직접 연결 | 0/80 | 0/80 |
| Windows → Windows loopback, 원격 비교 후 | 0/80 | 8/80 |

총 640회 중 23회 실패. Windows의 18회는 ConnectionResetError, Linux SSH 경유 5회는 RemoteDisconnected였다. 전부 응답 헤더 수신 단계에서 발생했으며, 각각 같은 요청 ID의 서버 sendall 완료 기록이 있다. 이 기록은 서버의 송신 호출 성공을 뜻하며 실제 수신이나 패킷 전달을 증명하지 않는다.

SSH 경유 비교는 Linux HTTP 클라이언트를 사용하지만 마지막 연결은 Windows SSH 프로세스에서 loopback으로 열린다. 또한 SSH가 버퍼링·TCP 종료 방식을 바꾸므로, 이 결과만으로 특정 네트워크 필터를 지목할 수 없다. LAN 80회 정상도 모든 운영 요청과 장시간 상황의 정상 보장은 아니다.

## 범위와 보존

- 서버는 `hello` 5바이트만 반환하는 합성 TCP 서버이며 거래플랜 앱·인증·DB를 사용하지 않았다.
- Windows 임시 수신: 127.0.0.1:19094 및 192.168.0.14:19096. 합성 서버는 loopback/자기 주소/지정 Linux IP만 처리했다.
- Linux 임시 loopback 전달: 127.0.0.1:19095. 새 SSH 연결 수명 안에서만 사용했다. Linux 파일·서비스·Docker 설정은 쓰지 않았다.
- 최종 Windows 포트 2개와 원격 전달 포트가 모두 닫혔고 소유 서버 스레드·SSH 프로세스가 종료됐다.
- 원본 PC DB·공식 시험 DB·앱 파일·Git index 등 보호 대상 9개 해시 일치, 공개 거래플랜 health 200.
- 방화벽 규칙, 보안 프로그램, 네트워크 필터, Winsock/TCP 설정은 변경하지 않았다. 다른 서비스 및 rt.2884.kr에는 작업하지 않았다.

원격 포트 해제 확인의 첫 PowerShell 인용 명령은 구문 오류로 실패했다. 이후 Python의 인자 목록과 shlex로 같은 읽기/포트 점유 확인을 수행해 정상 종료를 확인했다. 이를 HTTP 오류 23회에 포함하지 않는다.

## 다음 진단 조건

현재 도구 셸은 관리자 권한이 없다. 필요하면 관리 권한의 패킷/필터 추적을 준비하되, 대상은 이 합성 서버의 주소·포트·짧은 재현 시간으로 제한하고 업무 트래픽을 수집하지 않는 방식을 우선 검토한다. 기존 캡처가 있으면 건드리지 않고, 종료/정리와 설정 보존까지 확인해야 한다. 이번에 패킷 캡처나 보안 프로그램 중지는 수행하지 않았다.

다른 Windows PC와의 동일 최소 재현 비교도 여전히 유효하다. 확인되지 않은 필터를 비활성화하거나 업무 POST에 무조건 재시도를 넣는 변경은 이번 증거로 정당화하지 않는다.

## 증거

`C:\Users\beene\Documents\Codex\tradeplan-crosshost-http-20260923`

- probe.py, started.json, terminal.json
- groups.json, remote-result.json, server-events.json, summary.json
- independent-verification.json, remote.stderr.txt

진단 자료의 독립 검증은 PASS지만 `diagnosticIssueResolved=false`다. 제품 수정·운영 배포·stage/commit/push는 없으며 전체 Goal은 미완료다.
