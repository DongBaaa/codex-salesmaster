# live 관찰 점검 리포트

- 실행시각: 2026-07-12 23:25:53
- 결과: **PASS**
- BaseUrl: https://trade.2884.kr
- 채널: stable
- 샘플 수: 2
- 샘플 간격(초): 5
- package probe 모드: anonymous-only
- manifest probe skip: False
- package probe skip: False
- Android APK signing 점검: ACCEPTED - legacy debug signing update chain, DN=CN=Android Debug, O=Android, C=US, SHA256=dfc2e3680116ebe4291c466ba7da9491a2ecdf8502323ffafefc155e0c45dc28
- Android legacy debug signing 경고 수용: True
- 로컬 캐시 필수 점검: False
- 로컬 캐시 Warning 실패 처리: False
- 로컬 캐시 점검: SKIP - LocalCacheAppDataRoot가 지정되지 않아 로컬 캐시 검증을 건너뜀

| 회차 | 시각 | healthz | manifest | desktop 버전 | android 버전 | desktop package | android package | 거래처/거래내역 | desktop packageUrl | android packageUrl |
| ---: | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 2026-07-12 23:24:59 | OK (200) | OK (200) | 1.1.677 | 0.2.81 | OK (200; anonymous-head) | OK (200; anonymous-head) | SKIP | https://trade.2884.kr/updates/download/desktop/tradeplan-pc-installer-v1.1.677.zip | https://trade.2884.kr/updates/download/android/tradeplan-android-v0.2.81.apk |
| 2 | 2026-07-12 23:25:05 | OK (200) | OK (200) | 1.1.677 | 0.2.81 | OK (200; anonymous-head) | OK (200; anonymous-head) | SKIP | https://trade.2884.kr/updates/download/desktop/tradeplan-pc-installer-v1.1.677.zip | https://trade.2884.kr/updates/download/android/tradeplan-android-v0.2.81.apk |

## 로컬 캐시 점검

- SKIP: LocalCacheAppDataRoot가 지정되지 않아 로컬 캐시 검증을 건너뜀

## 판정

- healthz, manifest, desktop/android package 다운로드 경로가 모두 정상 응답했습니다.
- 인증 정보를 제공한 경우 거래처/거래내역 조회도 0건이 아닌지 함께 확인했습니다.
- 로컬 캐시 점검을 요청한 경우 서버 데이터와 PC 로컬 캐시 핵심 목록도 함께 확인했습니다.
- live 반영 직후 최소한의 관찰 기준은 충족했습니다.
