# 품목 가격 변경을 통한 전표 금액 우회 차단

## 발견과 사전 영향도

앞 단계의 서버 전표 계산 뒤 입력 경로를 추적했다. `ItemsController`와 Sync Push는 `Item.Edit`만 확인한 뒤 품목의 매입/판매/소매/A/B/C 단가를 그대로 적용하고 있었다. 따라서 두 금액 권한이 없는 계정도 **한 번의 Sync Push에서 판매 단가를 1원으로 바꾸고 수량 3의 전표를 3원으로 생성**할 수 있었다. 실제 운영 자료는 변경하지 않고 메모리 SQLite와 컨트롤러를 통해 재현했다.

사용자가 확정한 기준은 "품목·수량·비고 저장 허용, 금액은 서버가 계산"이다. 서버 계산의 가격 근거가 무권한 요청으로 바뀌면 이 기준을 지킬 수 없으므로 품목 가격 쓰기를 먼저 보호한다. 이 수정은 금액 숨김 응답/클라이언트 구현을 대체하지 않는다.

사전점검 판정: 로컬 수정·격리 검증 진행 가능. 품목 기본 정보 저장은 유지하고 기존 업체/지점/받은 재고의 읽기·원본 수정 범위와 자산·청구·전표 범위를 확장하지 않는다. 운영 배포는 통합 금액 숨김 및 사용자 수락 뒤 판단한다.

## 변경

- 품목 일반 생성/수정 및 Sync Push에 동일한 `ItemAmountWritePolicy`를 연결했다.
- 매입 금액 권한이 없으면 기존 매입 단가를 보존한다. 매출 금액 권한이 없으면 판매·소매·A/B/C 단가를 보존한다. 품목 메모 등 나머지 허용 필드는 저장할 수 있다.
- 신규 품목은 권한 없는 방향의 가격을 기본값 0으로 둔다. 이는 숨긴 기존 금액을 0으로 덮는 처리가 아니다. 가격 미등록 품목은 앞 단계의 제한 계정 전표 계산에서 저장을 거부하므로, 금액 권한이 있는 담당자의 가격 등록이 필요하다.
- 매출 사용자 단가와 단가 등급 설정의 Sync Push에는 매출 금액 권한을 추가로 요구한다. 매입 금액 권한만으로 매출 가격을 바꿀 수 없다. 권한 거부는 업무 트랜잭션 시작 전에 발생한다.
- DTO 자체의 금액을 바꾸지 않고 저장 엔티티의 보호 가격을 유지한다. 원래 요청의 중복 처리 해시를 보존하여 동일 요청 재전송이 가격·revision을 다시 바꾸지 않도록 했다.
- Admin/God의 기존 권한 우회는 유지한다. 금액 조회 권한이 있어도 품목/설정 자체의 수정 권한과 지점 범위 검사는 여전히 필요하다.
- PC에 남아 있는 권한 없는 가격 변경이 허용된 품목 메모/전표 전송까지 403으로 막지 않도록, 가격등급 옵션 및 품목별 등급단가를 dirty 수집 단계에서 제외한다. 해당 dirty/Outbox를 지우거나 승인 처리하지 않는다. 현재 계정의 전송 가능 건수와 권한 안내도 같은 기준을 사용하며 TenantAll 조회 범위를 금액 권한으로 간주하지 않는다.
- 전체 동기화 완료 진단은 미전송 가격과 Outbox를 계속 표시한다. 허용된 메모 저장만 성공했다고 남은 변경까지 완료로 기록하지 않는다. 가격 관련 Outbox는 일반 전송 확인과 구분하여 권한 안내를 제공한다.

## 수정 파일

- `Server/거래플랜.Server.Api/Services/ItemAmountWritePolicy.cs`
- `Server/거래플랜.Server.Api/Services/OfficeScopeService.cs`
- `Server/거래플랜.Server.Api/Controllers/ItemsController.cs`
- `Server/거래플랜.Server.Api/Controllers/SyncController.cs`
- `Tests/GeoraePlan.Server.Api.Tests/InvoiceScopeAndQuantityIntegrityTests.ItemAmounts.cs`
- `Desktop/거래플랜.Desktop.App/Services/SyncService.cs`, `LocalStateService.cs`, `LocalStateService.ItemPriceGrades.cs`, `LocalStateService.PendingSummary.cs`
- `Tests/GeoraePlan.Desktop.App.Tests/SyncSharedDirtyPermissionGuardTests.cs`, `SyncOutboxPendingStateTests.ItemAmountPermissions.cs`

## 증거

외부 증거 폴더: `D:\DevCaches\tradeplan-item-amounts-20260924`.

- 수정 전 23개: 15실패/8통과. 일반 저장/동기화, 생성/수정, 양쪽 권한 없음/한쪽만/양쪽/Admin 조합에서 보호 실패를 재현했다.
- 수정 후 같은 23개 모두 통과. 같은 요청 내 품목 가격 변경 후 전표 생성도 기존 1,100원·전표 3,300원을 보존한다.
- 허용된 비금액 메모 저장, 기존 가격 보존, 신규 가격 초기화, 정상 재전송의 revision/영수증 보존, 매출 단가 설정의 403과 업무 행 미변경을 검사한다.
- 전체 서버 검사: 1,758통과/실패0/24미실행(PostgreSQL 전용). 23개 검사를 포함한 수치다.
- PC 회귀 4개는 SQLite 및 실제 SyncService/HTTP 직렬화 경로에서 매출 권한 유무 × OfficeOnly/TenantAll을 검사한다. 초기 2실패/2통과 후 수정 및 진단 보완으로 4개 모두 통과했다. 권한 경로 검사 2개를 포함한 최종 집중 검사는 6통과다. 중간 실패 기록은 증거 폴더에 보존한다.
- PC 동기화/대기 기록/지점 범위/가격등급 관련 회귀 414통과/실패0/미실행0. 6개 집중 검사를 포함한다. PC 전체 검사를 이번에 반복한 것은 아니다.
- 최종 전체 서버 검사, 공식 환경 준비/Run-All, 산출물 해시 및 원본 PC/index 보존 결과는 외부 `verification.json`에 기록한다.

## 남은 범위

금액 읽기 응답·PC/모바일 캐시·화면·출력은 아직 숨김 처리가 완성되지 않았다. 수금·거래·렌탈 및 관련 가격 설정의 나머지 쓰기 경로도 별도로 검증해야 한다. 특히 현재 구형 DTO는 미공개 금액과 0원을 구분하지 못하므로 0원 마스킹으로 배포하지 않는다. 기존 743 패키지는 이번 수정도 포함하지 않는다. 운영 데이터/계정 변경·운영 배포·Git commit/push 및 전체 Goal 완료는 수행하지 않는다.
