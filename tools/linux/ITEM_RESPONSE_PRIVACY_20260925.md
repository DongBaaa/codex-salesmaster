# 서버 품목·등급 가격 응답과 호환성 — 2026-09-25

품목 목록·개별·상세·생성·수정·정확 재전송의 응답, Sync Pull의 품목/등급, Sync 충돌의 client/server snapshot에 매입·매출 권한별 null을 적용한다. 응답 DTO 및 snapshot 문자열 복사본만 바꾸며 업무 금액·Revision·processed mutation·영속 감사 원문은 유지한다. 미지의 형식/깨진 제한 snapshot은 비워서 노출을 차단한다. 중첩/case 변형 금액과 파생 hidden 플래그도 처리한다.

자산/품목/전표/거래처 업체·지점 조회 쿼리와 일반 비금액 저장 권한은 유지한다. 서버 품목 저장 정책은 기존 것을 사용한다. 직접 revision/processed mutation 충돌은 금액 없는 메타데이터 응답임을 확인했다. ItemDto/ItemPriceGradeDto를 응답하는 Controller는 Items/Sync 두 곳이다.

제한 계정의 /items 및 /sync에는 nullable 품목 계약 protocol 3이 필요하다. 기존 /invoices·/payments·거래처 detail은 protocol 2를 유지한다. 전액 조회 권한 사용자와 비대상/인증 전 경로는 기존 흐름을 유지한다. 업그레이드 안내는 426 및 no-store이다. 공유 CurrentProtocolVersion을 3으로 올려 PC/Android의 실제 identity provider가 이 값을 전송하도록 재빌드한다.

수정 전 28건 중 15건 실패, 수정 후 최초 집중 28건 통과. 중첩/대소문자/등급/깨진 JSON 5건과 기존 검사를 포함한 서버 최초 329건 통과. 이후 누락된 과거 가격을 explicit null로 보완하는 3건의 실패를 추가 재현하고 최종 서버 332건 통과. 실제 HTTP 제한 계정으로 구버전 품목·Pull 차단, protocol 2 전표 수량/비고 저장과 서버 계산, protocol 3 품목 비고 저장 및 null 응답, 관리자 원값/비고 보존을 확인했다. PC 관련 회귀 1,805건, 보존 검사 27건과 선언 형식 1건 통과. Android Debug APK SDK 8.0.419 빌드 경고/오류 0. 공식 앱과 파일 해시 증거는 verification.json에 기록한다.

공식 Run-All.cmd 실행으로 오른쪽 모니터의 admin/USENET 업무 화면과 01:36:50 동기화 완료를 확인했다. 업무 데이터는 편집하지 않았고 정상 종료(exit 0), 서버 quick_check=ok·sidecar_count=0을 확인했다. 제한 계정의 전체 실제 화면 검증은 아직 남아 있다. 실행 파일/계약/APK 동일성, 종료 백업 CRC, 원본 DB 및 Git 보존은 `D:\DevCaches\tradeplan-item-response-privacy-20260925\verification.json`에 기록한다.

운영 배포·Git 반영·설치파일 갱신은 아직 수행하지 않는다. Transaction Pull, Rental 금액/구조화 JSON/DepositText, 제한 계정 전체 실제 업무·Android 실기기·출력·서명 업그레이드 연속성과 원래 거래플랜·워크플랜 전체 Goal은 미완료다.
