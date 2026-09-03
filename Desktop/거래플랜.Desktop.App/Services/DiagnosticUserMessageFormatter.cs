namespace 거래플랜.Desktop.App.Services;

public static class DiagnosticUserMessageFormatter
{
    public static string DescribeIntegrityIssue(string? code, string? title)
        => Normalize(code) switch
        {
            DataIntegrityIssueCodes.RentalBillingTemplateInvalid =>
                "청구서에 표시할 품목 정보가 손상되어 월 청구금액을 정상적으로 계산할 수 없습니다.",
            DataIntegrityIssueCodes.RentalProfileTemplateEmpty =>
                "렌탈 청구 설정은 있지만 청구서에 보여 줄 품목이 하나도 등록되지 않았습니다.",
            DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch =>
                "저장된 월 청구금액과 청구 품목의 수량×단가 합계가 다릅니다. 그대로 청구하면 금액이 틀릴 수 있습니다.",
            DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset =>
                "청구서의 한 품목에 실제 렌탈 장비가 연결되지 않았습니다.",
            DataIntegrityIssueCodes.RentalTemplateMissingAsset =>
                "청구 품목이 이미 삭제되었거나 찾을 수 없는 렌탈 장비를 가리키고 있습니다.",
            DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch =>
                "연결된 장비의 월요금 합계와 청구서 품목 금액이 다릅니다. 어느 금액을 실제 계약금액으로 쓸지 확인이 필요합니다.",
            DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch =>
                "장비와 청구 설정의 업체·담당지점이 서로 달라, 다른 지점 자료로 처리될 위험이 있습니다.",
            DataIntegrityIssueCodes.RentalAssetMissingProfileTemplateReference =>
                "장비는 청구 설정에 연결되어 있지만 청구서 표시 품목에서 빠져 있어 청구가 누락될 수 있습니다.",
            DataIntegrityIssueCodes.RentalOperationalScopeMismatch =>
                "렌탈 청구 설정과 장비에 저장된 업체·담당지점 정보가 서로 맞지 않습니다.",
            DataIntegrityIssueCodes.RentalCustomerNameMismatch =>
                "렌탈 화면에 저장된 거래처명과 현재 거래처 목록의 이름이 다릅니다.",
            DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates =>
                "하나의 장비가 여러 거래처의 청구 설정에 동시에 포함되어 중복 청구될 수 있습니다.",
            DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets =>
                "청구 설정에 연결된 렌탈 장비가 하나도 없습니다.",
            DataIntegrityIssueCodes.RentalProfileLinkedAssetsOutsideCurrentScope =>
                "현재 계정에서는 보이지 않는 다른 지점의 장비가 이 청구 설정을 사용 중입니다. 임의로 삭제하면 안 됩니다.",
            DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee =>
                "청구해야 하는 장비인데 월요금이 0원으로 저장되어 있습니다.",
            DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed =>
                "이 장비를 매월 청구해야 하는지, 청구에서 빼야 하는지 선택되지 않았습니다.",
            DataIntegrityIssueCodes.RentalAssetMissingBillingProfile =>
                "장비가 연결한 청구 설정을 현재 자료에서 찾을 수 없습니다.",
            DataIntegrityIssueCodes.RentalAssignmentMissingReference =>
                "현재 임대 이력이 이미 삭제되었거나 찾을 수 없는 장비·거래처·청구 설정을 가리키고 있습니다.",
            DataIntegrityIssueCodes.RentalAssignmentHistoricalStaleReference =>
                "과거 임대 이력의 원본 장비나 거래처가 현재는 없지만, 당시 이름과 내용은 보존되어 있습니다. 현재 청구에는 영향이 없는 참고 항목입니다.",
            DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments =>
                "하나의 장비에 '현재 임대 중'인 이력이 두 건 이상 있습니다.",
            DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch =>
                "청구 이력에 저장된 수금액과 실제 수금·거래내역 합계가 다릅니다. 미수금이 틀리게 보일 수 있습니다.",
            DataIntegrityIssueCodes.RentalBillingRunMissingRunId =>
                "과거 청구 이력 중 일부에 내부 식별값이 없습니다. 현재 청구 내용과 대조가 필요한 참고 항목입니다.",
            DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds =>
                "같은 청구 이력에 서로 다른 내부 식별값이 연결되어, 어느 전표·수금을 기준으로 할지 자동으로 판단할 수 없습니다.",
            DataIntegrityIssueCodes.RentalBillingRunsJsonMalformed =>
                "과거 청구 이력의 내부 저장 내용이 손상되어 이력을 정상적으로 읽을 수 없습니다.",
            DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch =>
                "청구 설정에 표시된 수금·미수금과 실제 전표·수금 내역이 다릅니다.",
            DataIntegrityIssueCodes.CustomerDuplicateCandidate =>
                "같은 지점에 이름이 완전히 같은 거래처가 두 건 이상 있습니다. 실제로 같은 거래처인지 확인이 필요합니다.",
            DataIntegrityIssueCodes.ItemDuplicateCandidate =>
                "품명과 규격이 같은 품목이 두 건 이상 있습니다. 재고·판매·구매 내역을 비교해 실제 중복인지 확인해야 합니다.",
            DataIntegrityIssueCodes.WarehouseDuplicateCandidate =>
                "같은 지점에 코드나 이름이 같은 창고가 두 건 이상 있습니다.",
            DataIntegrityIssueCodes.CustomerContractMissingCustomerReference =>
                "계약서나 첨부파일이 현재 없는 거래처에 연결되어 있습니다.",
            DataIntegrityIssueCodes.InvoiceAmountMismatch =>
                "전표에 저장된 합계금액이 품목·공급가·부가세를 다시 계산한 결과와 다릅니다.",
            DataIntegrityIssueCodes.InvoiceOverSettled =>
                "입력된 수금 또는 지급 합계가 전표 금액보다 큽니다. 중복 입력이나 금액 오류가 있을 수 있습니다.",
            DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference =>
                "전표 품목 행이 현재 없는 전표에 연결되어 있습니다.",
            DataIntegrityIssueCodes.PaymentMissingInvoiceReference =>
                "수금·지급 내역이 현재 없는 전표에 연결되어 있습니다.",
            DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch =>
                "하나의 전표에 연결된 거래내역과 수금·지급 내역의 금액 또는 상태가 서로 다릅니다.",
            DataIntegrityIssueCodes.TransactionOperationalScopeMismatch =>
                "수금·지급 내역의 업체·담당지점이 연결된 거래처나 전표와 다릅니다.",
            DataIntegrityIssueCodes.RentalDeletedInvoiceActivePayment =>
                "삭제된 렌탈 전표에 사용 중인 수금·지급 내역이 남아 있어 미수금이 틀리게 계산될 수 있습니다.",
            DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction =>
                "복원된 렌탈 전표의 수금·거래내역 연결이 완전히 복원되지 않았습니다.",
            DataIntegrityIssueCodes.TransactionAttachmentMissingTransactionReference =>
                "첨부파일 기록이 현재 없는 거래내역에 연결되어 있습니다.",
            DataIntegrityIssueCodes.MissingAttachmentFiles =>
                "첨부파일 목록은 있지만 이 PC에 실제 파일이 없어 파일을 열 수 없습니다.",
            DataIntegrityIssueCodes.RentalBillingLogMissingProfileReference =>
                "과거 청구 기록이 현재 없는 청구 설정에 연결되어 있습니다.",
            DataIntegrityIssueCodes.InventoryTransferLineMissingTransferReference =>
                "재고이동 품목 행이 현재 없는 재고이동 문서에 연결되어 있습니다.",
            DataIntegrityIssueCodes.InventoryDeletedItemStockResidue =>
                "삭제된 품목에 재고 수량이 남아 있어 재고 합계가 틀릴 수 있습니다.",
            DataIntegrityIssueCodes.InventoryStockSnapshotMismatch =>
                "품목의 전체 현재고와 창고별 재고를 합한 값이 다릅니다.",
            DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing =>
                "재고나 재고이동 내역이 이미 삭제되었거나 사용 중지된 창고를 가리키고 있습니다.",
            _ => $"'{Display(title, "데이터 연결 확인")}' 항목에서 저장된 값끼리 맞지 않거나 필요한 연결 정보가 없습니다."
        };

    public static string BuildIntegrityActionSteps(string? suggestedAction, bool hasDirectAction, bool isInformational)
    {
        var action = HumanizeTerms(Display(suggestedAction, "원본 화면에서 현재 상태를 확인해 주세요."));
        if (isInformational)
            return $"① 현재 업무에 영향이 없는 참고 항목인지 확인합니다.\n② 현재 자료가 정상이라면 수정하지 않아도 됩니다.\n\n확인할 내용\n{action}";

        return hasDirectAction
            ? $"① 아래 '해당 화면에서 수정' 버튼을 누릅니다.\n② '확인할 내용'을 보고 실제 업무 자료와 맞게 고친 뒤 저장합니다.\n③ 이 창을 새로고침해 해결됐는지 확인합니다.\n\n확인할 내용\n{action}"
            : $"① '확인할 정보'를 보고 어느 자료인지 확인합니다.\n② 아래 안내를 따라 원본 화면에서 확인합니다.\n③ 임의로 삭제하지 말고, 수정했다면 운영 점검을 다시 실행합니다.\n\n확인할 내용\n{action}";
    }

    public static string BuildIntegrityImpact(string? severity)
        => severity?.Trim().ToUpperInvariant() switch
        {
            "ERROR" => "업무 영향: 금액·연결·재고 계산이 틀릴 수 있으므로 관련 업무를 계속하기 전에 확인해야 합니다.",
            "INFO" => "업무 영향: 현재 업무를 막지 않는 참고 정보입니다. 임의로 삭제하지 마세요.",
            _ => "업무 영향: 즉시 중단할 정도는 아니지만, 다음 청구·저장 전에 확인하는 것이 안전합니다."
        };

    public static string DescribeServerIntegrityIssue(string? code, string? fallbackMessage)
        => Normalize(code) switch
        {
            "rental_asset_template_monthly_mismatch" => "장비에 저장된 월요금 합계와 청구서 품목 금액이 다릅니다. 실제 계약 금액을 기준으로 두 값을 맞춰야 합니다.",
            "rental_profile_monthly_amount_mismatch" or "rental_profile_asset_monthly_amount_mismatch" => "청구 설정의 월 기준금액과 품목 또는 연결 장비 합계가 다릅니다. 그대로 청구하면 금액이 틀릴 수 있습니다.",
            "rental_billing_manual_stop_status_mismatch" => "청구 설정의 상태와 아직 미수금이 남은 청구 이력의 상태가 서로 충돌합니다. 청구를 계속할지 중단할지 확인이 필요합니다.",
            "rental_profile_customer_unlinked" => "청구 설정에 거래처 이름은 있지만, 현재 거래처 목록의 실제 거래처와 연결되지 않았습니다.",
            "duplicate_item_name_match_keys" => "품명·규격이 같은 품목이 두 건 이상 있습니다. 실제 같은 품목인지 재고와 전표 연결을 비교해야 합니다.",
            "rental_assignment_historical_stale_reference_rows" => "과거 임대 이력의 원본 장비·거래처가 현재는 없지만 당시 표시 내용은 남아 있습니다. 현재 청구에 영향이 없는 참고 항목입니다.",
            _ => HumanizeTerms(Display(fallbackMessage, "저장된 데이터 사이에 맞지 않는 값이나 끊어진 연결이 확인되었습니다."))
        };

    public static string BuildServerIntegrityActionSteps(
        string? suggestedAction,
        bool hasDirectAction,
        bool isInformational)
    {
        var action = HumanizeTerms(Display(suggestedAction, "원본 화면에서 현재 상태를 확인해 주세요."));
        if (isInformational)
        {
            return $"① 아래 대상이 현재 업무에 사용되는지 확인합니다.\n" +
                   "② 과거 기록이고 현재 청구·재고에 영향이 없다면 그대로 둡니다.\n" +
                   $"③ 정리할 근거가 명확할 때만 아래 안내를 따릅니다.\n\n세부 안내\n{action}";
        }

        return hasDirectAction
            ? $"① '해당 화면 열기' 버튼을 누릅니다.\n" +
              "② 실제 계약서·장비·거래처 정보와 비교해 올바른 값으로 고친 뒤 저장합니다.\n" +
              "③ 편집창을 닫으면 자동으로 다시 검사됩니다.\n\n" +
              $"세부 안내\n{action}"
            : $"① 아래 '확인할 대상'에서 거래처·품목·장비 정보를 확인합니다.\n" +
              "② 안전한 수정 대상을 자동으로 특정할 수 없으므로 임의 삭제하지 않습니다.\n" +
              "③ 아래 안내를 따라 원본 자료를 확인한 뒤 '수정 후 다시 검사'를 누릅니다.\n\n" +
              $"세부 안내\n{action}";
    }

    public static string DescribeSyncProblem(SyncDiagnosticListItem item)
        => Normalize(item.Subcategory) switch
        {
            "missing_sync_credential" => "이 PC에 해당 지점의 동기화 계정이 저장되지 않아, 로컬 변경사항을 서버로 보내지 못했습니다.",
            "remaining_dirty" => "이 PC에서 저장한 데이터 중 서버의 최종 확인을 받지 못한 항목이 남아 있습니다.",
            "office_scope" => "현재 로그인 계정은 이 데이터의 담당지점을 수정할 권한이 없어 서버가 저장을 거부했습니다.",
            var value when value.StartsWith("missing_", StringComparison.Ordinal) =>
                $"{EntityDisplay(item.EntityName)} 데이터가 현재 없는 {EntityDisplay(item.ReferenceEntityName)} 데이터를 가리키고 있어 서버가 저장을 완료하지 못했습니다.",
            "db_concurrency" => "이 PC에서 편집하는 동안 다른 PC에서 같은 데이터를 먼저 저장해 버전이 달라졌습니다. 덮어쓰기를 막기 위해 저장이 중단됐습니다.",
            "network_timeout" => "동기화 중 네트워크가 끊기거나 응답 시간을 넘어 서버 확인을 받지 못했습니다.",
            "server_failure" => "서버가 요청을 처리하는 중 오류가 발생했습니다. 사용자 입력보다는 서버 상태나 최근 배포를 확인해야 하는 문제입니다.",
            "startup_recovery" => "앱 시작 중 서버 데이터를 다시 받거나 로컬 상태를 복구하는 작업이 완료되지 못했습니다.",
            _ => "로컬에서 저장한 변경사항이 서버의 확인을 받지 못해 동기화가 완료되지 않았습니다."
        };

    public static string BuildSyncActionSteps(SyncDiagnosticListItem item)
    {
        var specificAction = HumanizeTerms(Display(item.RecoveryAction, "동기화를 다시 시도해 주세요."));
        return Normalize(item.Subcategory) switch
        {
            "missing_sync_credential" => $"1. 환경설정 > 동기화에서 해당 지점 계정을 저장합니다.\n2. 이 창으로 돌아와 '동기화 재시도'를 누릅니다.\n3. 미해결 건수가 줄었는지 확인합니다.\n안내: {specificAction}",
            "office_scope" => "1. 현재 데이터의 담당지점이 맞는지 확인합니다.\n2. 해당 지점 수정 권한이 있는 계정으로 로그인하거나 관리자에게 권한을 요청합니다.\n3. 데이터를 다시 저장한 후 동기화합니다.",
            "server_failure" => "1. '진단 리포트'를 눌러 현재 오류 정보를 저장합니다.\n2. 관리자에게 리포트와 발생 시각을 전달합니다.\n3. 서버 상태가 정상화된 후 '동기화 재시도'를 누릅니다.",
            _ when item.IsRecoverable => $"1. 이 오류를 선택한 상태에서 상단 '선택 항목 복구'를 누릅니다.\n2. {specificAction}\n3. 미해결 건수와 서버 반영 대기 건수가 줄었는지 확인합니다.",
            _ => $"1. 아래 '기술 상세(지원용)'를 포함해 진단 리포트를 저장합니다.\n2. {specificAction}\n3. 임의로 데이터를 삭제하지 말고 관리자에게 리포트를 전달합니다."
        };
    }

    public static string SyncImpactText(SyncDiagnosticListItem item)
        => item.IsRecoverable
            ? "현재 영향: 데이터는 이 PC에 남아 있지만 서버와 다른 PC에는 아직 보이지 않을 수 있습니다."
            : "현재 영향: 자동 복구로 안전하게 고칠 수 없어 권한·원본 데이터·서버 상태 확인이 필요합니다.";

    public static string DescribeOutboxError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "서버 전송 대기 중입니다.";

        var value = errorMessage.Trim();
        if (value.Contains("revision", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("concurrency", StringComparison.OrdinalIgnoreCase))
        {
            return "다른 PC가 같은 데이터를 먼저 저장해 버전이 달라졌습니다. 최신 데이터를 받은 후 다시 전송해야 합니다.";
        }

        if (value.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            return "현재 계정에 해당 지점 데이터를 저장할 권한이 없거나 담당지점이 맞지 않습니다.";
        }

        if (value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return "이 데이터가 필요로 하는 원본 데이터를 서버에서 찾을 수 없어 전송이 멈춼습니다.";
        }

        if (value.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            return "네트워크 또는 서버 응답 지연으로 전송을 완료하지 못했습니다.";
        }

        return HumanizeTerms(value);
    }

    public static string HumanizeTerms(string value)
        => value
            .Replace("sync outbox", "서버 전송 대기열", StringComparison.OrdinalIgnoreCase)
            .Replace("outbox", "서버 전송 대기열", StringComparison.OrdinalIgnoreCase)
            .Replace("dirty", "서버 반영 대기", StringComparison.OrdinalIgnoreCase)
            .Replace("scope", "업체·지점 범위", StringComparison.OrdinalIgnoreCase)
            .Replace("tenant", "업체", StringComparison.OrdinalIgnoreCase)
            .Replace("canonical", "기준", StringComparison.OrdinalIgnoreCase)
            .Replace("BillingProfileId", "청구 설정 연결", StringComparison.OrdinalIgnoreCase)
            .Replace("IncludedAssetIds", "연결 장비 목록", StringComparison.OrdinalIgnoreCase)
            .Replace("청구상태 미확인", "이 장비를 청구할지 아직 선택하지 않음", StringComparison.OrdinalIgnoreCase)
            .Replace("템플릿 참조 없음", "청구서 품목에 장비 연결 없음", StringComparison.OrdinalIgnoreCase)
            .Replace("revision", "서버 변경 번호", StringComparison.OrdinalIgnoreCase)
            .Replace("JSON", "내부 저장 내용", StringComparison.OrdinalIgnoreCase)
            .Replace("run", "청구 이력", StringComparison.OrdinalIgnoreCase);

    private static string EntityDisplay(string? value)
        => Normalize(value) switch
        {
            "customer" or "customermaster" => "거래처",
            "invoice" => "전표",
            "transaction" => "거래내역",
            "payment" => "수금·지급",
            "item" => "품목",
            "rentalasset" => "렌탈 장비",
            "transactionattachment" => "첨부파일",
            _ => Display(value, "연결된 원본")
        };

    private static string Display(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
