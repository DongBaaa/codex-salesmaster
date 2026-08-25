# Visual QA - 메인 거래내역 세금계산서 발행 표시 통일

## 범위
- WPF 데스크톱 메인 화면 거래내역 DataGrid의 세금계산서 컬럼.
- 전표 상세창의 세금계산서 번호 표시 유지.

## 변경 확인
- 메인 목록 행 모델 `InvoiceListRow.TaxInvoiceDisplay`는 `TaxInvoiceIssued=true` 또는 `TaxInvoiceNumber` 존재 시 항상 `발행`을 반환합니다.
- `TAX-20260606-0001` 같은 번호는 메인 거래내역 목록에 직접 노출되지 않습니다.
- 전표 상세 ViewModel의 `TaxInvoiceNumberDisplay`는 기존처럼 실제 번호를 반환하므로, 거래 전표를 열면 번호 확인이 가능합니다.

## 디자인 일관성
- 새 색상, 새 간격, 새 컬럼, 새 장식 요소를 추가하지 않았습니다.
- 기존 DataGrid 컬럼 구조와 다크 ERP 디자인 토큰을 유지했습니다.

## 실행 검증
- `TaxInvoiceIssuedPersistenceTests` 6건 통과.
- 전체 데스크톱 테스트 846건 통과.
- Release 빌드 경고 0개, 오류 0개.

## 제한
- 이 변경은 WPF 표시 모델 변경이라 브라우저 캡처 대상이 아닙니다.
- 정적 코드/회귀 테스트/Release 빌드로 검증했습니다.

## Anti-slop 체크
- 새 시각 요소 없음.
- 목록은 상태 요약, 상세는 관리번호 확인이라는 업무 역할 분리 유지.
- DB 저장/동기화/권한 변경 없음.
