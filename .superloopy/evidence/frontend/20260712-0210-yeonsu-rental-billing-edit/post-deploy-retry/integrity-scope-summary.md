# 계정별 무결성 리포트 요약

- 실행시각: 2026-07-12 20:59:28 +09:00
- 접근 가능 리포트: `3`
- Warning 실패 처리: `True`

| 계정 | 상태 | HTTP | Tenant | Office | Error | Warning | Info | Issues |
| --- | --- | ---: | --- | --- | ---: | ---: | ---: | --- |
| ITWORLD | OK | 200 | ITWORLD | ITWORLD | 0 | 0 | 1 | Info:duplicate_item_name_match_keys=1260 |
| USENET | OK | 200 | USENET_GROUP | USENET | 0 | 0 | 2 | Info:duplicate_item_name_match_keys=1579; Info:rental_assignment_historical_stale_reference_rows=60 |
| YEONSU | SKIP | 403 |  |  |  |  |  | integrity/report permission denied; account is not expected to run settings integrity checks |
| ADMIN | OK | 200 | USENET_GROUP | USENET | 0 | 0 | 2 | Info:duplicate_item_name_match_keys=1579; Info:rental_assignment_historical_stale_reference_rows=60 |
