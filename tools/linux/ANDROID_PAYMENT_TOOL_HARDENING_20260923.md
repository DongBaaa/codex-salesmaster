# Android 결제 시험 도구 수정 및 PDF 실제 열기 검증 — 2026-09-23

진행 가능: 거래플랜 격리 시험과 검증 도구만 변경한다. 제품 코드·APK·운영·원본 업무 DB·계정/권한·공식 시험 입력은 변경하지 않는다. 워크플랜 및 rt.2884.kr 작업은 포함하지 않는다.

## 변경

- tools/mobile/Invoke-GeoraePlanAndroidPaymentE2E.ps1: 실제 하단 탭 문구로 이동, hint 없는 Android 검색창의 placeholder/입력값·포커스·유일성 확인, 선택한 PDF 및 결제 이력 스크롤을 적용했다. 파일명과 첨부 개수 검사는 유지한다.
- 시험 PDF의 객체 위치·xref 위치·stream 바이트 길이를 실제 생성 바이트로 계산한다. 이전 생성기는 xref 410/실제 432, stream 길이 55/실제 54로 잘못 선언했다. 이전 업로드 바이트 일치 결과와 유효한 PDF 문서 여부를 구분한다.
- 외부 화면 판정은 실제 현재 창의 package/activity를 요구한다. 빈 값·조회 오류·오래된 focused app·자체 앱·홈·권한/시스템 창을 열기 성공으로 간주하지 않는다.
- tools/verification/Test-GeoraePlanAndroidPaymentSelectors.ps1: 선택기·스크롤·PDF 구조·외부 화면 판정 회귀 21개. 필요한 함수만 AST로 로드하며 E2E 진입점이나 계정/서버 작업은 실행하지 않는다.

## 검증 결과

- 수정 전 선택기 12개 중 5통과/7실패, PDF 구조 포함 13개 중 5통과/8실패를 보존했다.
- 최종 원본 도구로 PowerShell 5.1 및 7 각각 21/21 통과. 기존 Release 테스트 DLL의 관련 MobileReleaseConfigurationTests 2/2 통과(no-build/no-restore); 전체 재빌드 결과는 아니다.
- 새 PDF 596바이트: pypdf strict 파싱·1페이지·본문 확인, PS5/7 바이트 동일, Poppler 렌더 PNG 육안 확인. Poppler의 Symbol 표시 폰트 경고 1건은 보존했으며 Helvetica 본문은 정상 렌더됐다.
- 첫 PDF 열기 시험은 이력 화면 스크롤 1회를 보조한 PASS다. 이 증거를 완전 자동 성공으로 계산하지 않는다. 이후 원본에 이력 스크롤을 반영했다.
- 최종 첨부 목록 실패 시험은 최종 소스 해시를 기록한 뒤 수동 조작 없이 PASS. 실제 목록 조회 1회 실패→화면의 명시적 네트워크 오류→기존 상세 정보의 첨부 1건 표시→Google PdfViewerActivity에서 본문 표시를 UI XML·스크린샷·현재 창으로 확인했다.
- 두 시험 각각 전표/결제/거래/첨부 1건, 11,000원, 업로드/다운로드 596바이트 동일. 시험 생성 자료 삭제 상태와 시험 재고 0을 독립 DB 조회로 확인했다.
- 삭제 전 활성 첨부로 읽기 권한 8개 확인: usenet 목록/본문 200 및 정확한 파일, itworld·yeonsu 각각 404, 비로그인 각각 401. 거부 응답에 첨부 ID/PDF 없음. 업로드/삭제의 모든 권한 조합까지 검증한 결과는 아니다.

## 보존과 종료

모든 저장 소유자의 전송 대기 0, 현재 usenet/USENET_GROUP/USENET의 동기화 오류 없음·DB integrity ok. 부팅 이후 ANR 없음. 기존 업무 행은 보존됐으며 재생성 원장 138행은 Id를 제외한 모든 열과 중복 개수가 동일하다. 사용자와 권한은 그대로다.

원본 PC DB·공식 시험 DB/API DLL·Git index 해시가 유지됐다. 기존 WriteE2E 변경도 그대로 보존했다. 최종 실행 후 수정한 두 도구의 바이트가 실행 전 해시와 일치한다. 소유 API/에뮬레이터를 정상 종료했고, 해당 포트/ADB 기기/AVD 프로세스가 남지 않았다. AVD는 후속용으로 보존했다.

## 증거와 후속

감사 폴더: C:\Users\beene\Documents\Codex\tradeplan-mobile-e2e-selectors-20260923

주요 증거: final-ps5.json, final-ps7.json, dotnet-results/selectors.trx, pdf-parser-check.json, fixture-render.png, independent-e2e-results.json, fallback-independent.json, fallback-viewer.png, attachment-permission-probe.json, mobile-final-summary.json, source-preservation.json, verification.json.

카메라·첨부 오프라인·업로드/삭제 거부·전송 중 계정 전환, 실제 API 중단·기기 OS 재부팅, 실기기·Release 서명/제자리 업데이트·장시간 시험은 남아 있다. 금액 조회 권한의 정책 의미도 미확정이다. 공식 사용자 수락·운영/Git 반영·원본 충돌 복구·별도 호스트 복원·워크플랜 잔여 검증을 포함해 전체 Goal은 미완료다. 이번 도구 수정에는 제품 버전 증가나 패키지 재생성이 필요하지 않으며 운영/commit/push는 실행하지 않았다.
