# Windows 로컬 HTTP 연결 종료 오류 진단 — 2026-09-23

## 결론과 범위

앞선 첨부 쓰기 시험의 WinError 10054를 다시 재현하고 제품 밖으로 범위를 좁혔다. **거래플랜이 없는 최소 .NET 서버, Python TCP 서버, Python을 쓰지 않는 .NET TCP 클라이언트에서도 같은 Windows PC 안의 동시 요청에 문제가 발생했다.** 거래플랜 권한/DB 코드만의 원인이나 특정 .NET 버전 회귀로 단정할 수 없다. 최종 원인은 미확정이며 문제를 해결했다고 표시하지 않는다.

실행 환경은 Windows 11 Pro 10.0.26200이다. 기존 공식 API DLL의 격리 사본과 업무 기능 없는 최소 서버만 사용했다. 원본/운영 자료·네트워크 설정·보안 서비스·계정·버전을 변경하지 않았다. 거래플랜 공개 HTTPS에서는 익명 상태 조회만 수행했다. 다른 서비스나 rt.2884.kr은 점검하지 않았다.

## 비교 결과

1차 비교는 각 조건에서 순차20·동시2개씩20 요청, 총480회였다. 자동 재시도는 없다.

| 서버 | 요청 | Connection close 없음: 전송 오류/40 | close 있음: 전송 오류/40 |
|---|---|---:|---:|
| 격리 거래플랜 | readyz | 0 | 6 |
| 격리 거래플랜 | healthz | 0 | 5 |
| 격리 거래플랜 | 미등록 경로의 익명 요청 | 0 | 6 |
| 최소 .NET 8.0.31 | 고정 길이 본문 | 0 | 4 |
| 최소 .NET 8.0.31 | 길이 미지정 본문 | 0 | 1 |
| 최소 .NET 8.0.31 | 빈 404 응답 | 0 | 4 |

26개 오류는 모두 응답 헤더를 읽는 단계에서 발생했고, 동일 요청 식별자의 서버 `Request finished` 로그가 26개 모두 존재했다. 서버 완료 로그만으로 클라이언트 수신 성공을 보장하지 않는다는 실제 사례다. 격리 API 로그의 연결 종료 사유에는 send loop의 정상 완료/FIN 기록이 있었다. 패킷을 캡처한 것은 아니므로 그 로그만으로 실제 TCP 패킷의 원인까지 판정하지 않는다.

초기 도구는 미등록 경로를404로 예상했지만 API의 익명 인증 정책상 실제 정상 응답은401이었다. 원본 결과를 보존하고 파생 summary에서 정상401 74건을 HTTP 기대값 분류 오류로 바로잡았다. 이74건을 연결 오류로 계산하지 않는다.

동시2개 연결에서 서로 다른 서버/클라이언트를 추가 비교했다.

| 서버 | Python raw/socket file/http.client 각80회 합계 | .NET 원시 TCP 클라이언트80회 |
|---|---:|---:|
| 최소 .NET 8.0.31 | 오류43/240 | 오류12/80 |
| 최소 .NET 8.0.27 | 오류42/240 | 오류11/80 |
| 단순 Python TCP 서버 | 오류10/240 | 오류5/80 |

최소 서버는 업무 코드·DB·인증·첨부가 없다. .NET 클라이언트는 TcpClient/NetworkStream으로 요청하며 HTTP 자동 재시도를 사용하지 않았다. Python 기준 서버도 송신 종료 후 수신EOF를 기다리는 방식이었다. 위 비교는 오류가 거래플랜/Kestrel/Python 한 가지에만 국한되지 않음을 보이며, Windows OS 자체나 특정 필터의 결함을 확정하는 대조 시험은 아니다.

별도 순차 raw/control 및 헤더/지연 비교는 결과 JSON에 보존했다. 처음의 단순 raw 순차 시험은 대부분 정상이라 동시 대조 시험으로 확장했다. 구형 런타임 첫 실행은 동일 포트를 요청해 시작 실패했고 종료됐다. 이후 별도19082 포트에서 실행했으며 그 준비 실패는 HTTP 오류 횟수에 합산하지 않는다.

## 운영 영향 확인의 한계

같은 PC에서 `https://trade.2884.kr/healthz`를 표준 인증서 검증과 HTTP/1.1로 조회했다. close 헤더20회·미지정20회 모두200이며 재시도0이다. 이40회는 공개 경로에서 같은 현상이 이번 관찰에 나타나지 않았다는 뜻이다. 로그인·업무 쓰기·장시간 부하·모든 사용 PC의 정상 동작을 증명하지 않는다.

## 환경 단서와 후속

읽기 전용 조회에서 AdGuard WFP 드라이버 `adgnetworkwfpdrv`, AhnLab/SafeTransaction 계열 드라이버 등의 실행 상태가 확인됐다. AdGuard 드라이버 파일의 회사는 AdGuard Software Limited, 버전8.0.91.0이다. 설치/실행 사실만으로 원인을 지정할 수 없다. 해당 프로그램, 방화벽, Winsock, TCP 전역 설정을 변경하거나 꺼서 시험하지 않았다.

Microsoft 문서에서10054는 연결 리셋을 뜻하며 종료 방식/소켓 linger도 관련될 수 있다고 설명한다. 이것은 오류 의미의 참고 근거이며 이 PC 원인 판정의 대체 증거가 아니다. [Winsock 오류 코드](https://learn.microsoft.com/en-us/windows/win32/winsock/windows-sockets-error-codes-2), [소켓 종료](https://learn.microsoft.com/en-us/windows/win32/winsock/graceful-shutdown-linger-options-and-socket-closure-2).

후속은 동일 최소 재현기의 다른 Windows PC 대조, 해당 loopback 흐름의 패킷/필터 추적, 필요 시 범위를 한정하고 원복 가능한 필터 비교다. 업무 POST 요청에 무조건 재시도를 추가하거나 제품 연결 정책을 임의 변경하는 방식으로 덮지 않는다. 원인과 운영 영향은 계속 미해결 항목으로 유지한다.

## 보존과 증거

소유 API와 최소 서버 프로세스를 종료했고19080~19083 리스너가 남지 않았다. 원본 PC DB·공식 시험 DB/API DLL·원본 Git index SHA256이 동일하다. 제품 수정·운영 배포·stage/commit/push는 없다.

감사 폴더: `C:\Users\beene\Documents\Codex\tradeplan-http-close-diagnosis-20260923`

- matrix-results.json / matrix-normalized-summary.json:480회 원본·정정 분류
- failed-request-correlation.json:26개 오류와 서버 완료 로그
- concurrent-controls.json / dotnet-raw-client.json:독립 서버/클라이언트 대조
- public-readonly-health.json:공개HTTPS40회
- 최소 서버/클라이언트 소스·빌드로그·원시 로그, raw/control/header 후속 비교
- windows-version.json / network-bindings.json / driver 후보 및 버전:환경 읽기 결과
- source-preservation.json:원본 보존·소유API 종료

전체 Goal은 미완료다. Android 실기기·계정 전환/장시간·정식 서명/업데이트, 기존 업무 충돌, 독립 장치 복원, 사용자 수락/거래플랜 배포, 워크플랜 잔여 검증은 별도로 남아 있다.
