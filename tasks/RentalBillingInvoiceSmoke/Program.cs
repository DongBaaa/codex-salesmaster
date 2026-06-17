using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void Main()
    {
        PrepareIsolatedLocalAppData();

        using var db = new LocalDbContext();
        LocalDbInitializer.InitializeAsync(db).GetAwaiter().GetResult();

        var adminSession = BuildAdminSession();
        var userSession = BuildUserSession();
        var officeAccess = new OfficeAccessService();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, officeAccess, dispatcher, adminSession);
        var rental = new RentalStateService(db, local);

        var referenceDate = new DateOnly(2026, 6, 15);

        var groupedCustomerId = SeedCustomer(local, adminSession, "ZZZ-렌탈 묶음 검증 거래처", "999-99-90001");
        var groupedProfileId = Guid.NewGuid();
        SeedGroupedAssets(db, groupedCustomerId, groupedProfileId);
        var groupedSave = SaveBillingProfile(
            rental,
            adminSession,
            groupedProfileId,
            groupedCustomerId,
            customerName: "ZZZ-렌탈 묶음 검증 거래처",
            billingType: "묶음",
            cycleMonths: 3,
            monthlyAmount: 330000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "사무기기 렌탈대금",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 330000m,
                    Amount = 330000m,
                    IncludedAssetIds = db.RentalAssets.IgnoreQueryFilters()
                        .Where(asset => asset.BillingProfileId == groupedProfileId)
                        .Select(asset => asset.Id)
                        .ToList()
                }
            });

        var individualCustomerId = SeedCustomer(local, adminSession, "ZZZ-렌탈 개별 검증 거래처", "999-99-90002");
        var individualProfileId = Guid.NewGuid();
        SeedIndividualAssets(db, individualCustomerId, individualProfileId);
        var individualSave = SaveBillingProfile(
            rental,
            adminSession,
            individualProfileId,
            individualCustomerId,
            customerName: "ZZZ-렌탈 개별 검증 거래처",
            billingType: "개별",
            cycleMonths: 3,
            monthlyAmount: 742000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "사무기기 렌탈대금",
                    BillingLineMode = "개별",
                    Quantity = 1m,
                    UnitPrice = 742000m,
                    Amount = 742000m,
                    IncludedAssetIds = db.RentalAssets.IgnoreQueryFilters()
                        .Where(asset => asset.BillingProfileId == individualProfileId)
                        .Select(asset => asset.Id)
                        .ToList()
                }
            });

        Ensure(groupedSave.Success, groupedSave.Message);
        Ensure(individualSave.Success, individualSave.Message);

        var groupedScenario = VerifyGroupedScenario(db, local, rental, userSession, groupedProfileId, referenceDate);
        var individualScenario = VerifyIndividualScenario(db, local, rental, userSession, individualProfileId, referenceDate);

        var januaryBoundaryScenario = VerifyJanuaryBoundaryScenarios(db, local, rental, adminSession);
        var customerResolutionScenario = VerifyCustomerResolutionFallbackScenario(db, local, rental, adminSession, userSession);
        var singleCandidateAutoLinkScenario = VerifySingleCandidateAutoLinkScenario(db, local, rental, adminSession, userSession);
        var legacyLinkedAssetFallbackScenario = VerifyLegacyLinkedAssetFallbackScenario(db, local, rental, adminSession, userSession);

        var output = new
        {
            Grouped = groupedScenario,
            Individual = individualScenario,
            JanuaryBoundary = januaryBoundaryScenario,
            CustomerResolutionFallback = customerResolutionScenario,
            SingleCandidateAutoLink = singleCandidateAutoLinkScenario,
            LegacyLinkedAssetFallback = legacyLinkedAssetFallbackScenario
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static object VerifyGroupedScenario(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState userSession,
        Guid profileId,
        DateOnly referenceDate)
    {
        var firstStart = rental.StartBillingAsync(profileId, referenceDate, userSession).GetAwaiter().GetResult();
        Ensure(firstStart.Success, firstStart.Message);
        Ensure(firstStart.RelatedEntityId != Guid.Empty, "묶음 청구 전표 ID가 반환되지 않았습니다.");

        var secondStart = rental.StartBillingAsync(profileId, referenceDate, userSession).GetAwaiter().GetResult();
        Ensure(secondStart.Success, secondStart.Message);
        Ensure(secondStart.RelatedEntityId == firstStart.RelatedEntityId, "묶음 청구 전표가 재사용되지 않았습니다.");

        var invoice = local.GetInvoiceAsync(firstStart.RelatedEntityId).GetAwaiter().GetResult();
        Ensure(invoice is not null, "묶음 청구 전표를 다시 불러오지 못했습니다.");

        var profile = db.RentalBillingProfiles.IgnoreQueryFilters().First(current => current.Id == profileId);
        var run = rental.GetOrCreateBillingRun(profile, referenceDate, persistChanges: false);
        Ensure(run is not null, "묶음 청구 run 을 찾지 못했습니다.");

        var activeInvoiceCount = db.Invoices.IgnoreQueryFilters()
            .Count(current => !current.IsDeleted &&
                              current.IsLatestVersion &&
                              current.LinkedRentalBillingRunId == run!.RunId);
        Ensure(activeInvoiceCount == 1, $"묶음 청구 전표가 {activeInvoiceCount}건 생성되었습니다.");

        var lines = invoice!.Lines.OrderBy(line => line.ItemNameOriginal).ToList();
        Ensure(lines.Count == 3, $"묶음 청구 라인 수가 3건이 아닙니다. 실제 {lines.Count}건");

        var expectedMonths = new[] { "사무기기 렌탈대금[6월]", "사무기기 렌탈대금[7월]", "사무기기 렌탈대금[8월]" };
        foreach (var expectedMonth in expectedMonths)
        {
            var line = lines.SingleOrDefault(current => string.Equals(current.ItemNameOriginal, expectedMonth, StringComparison.Ordinal));
            Ensure(line is not null, $"묶음 청구에서 {expectedMonth} 라인을 찾지 못했습니다.");
            Ensure(string.Equals(line!.SpecificationOriginal, "리코 IMC 2000 외", StringComparison.Ordinal), $"묶음 청구 대표 장비명이 잘못되었습니다. 실제: {line.SpecificationOriginal}");
            Ensure(line.Quantity == 1m, $"묶음 청구 수량은 1이어야 합니다. 실제: {line.Quantity}");
            Ensure(line.UnitPrice == 330000m, $"묶음 청구 단가가 330000원이 아닙니다. 실제: {line.UnitPrice}");
            Ensure(line.LineAmount == 330000m, $"묶음 청구 금액이 330000원이 아닙니다. 실제: {line.LineAmount}");
        }

        return new
        {
            FirstStartMessage = firstStart.Message,
            SecondStartMessage = secondStart.Message,
            InvoiceId = firstStart.RelatedEntityId,
            ActiveInvoiceCount = activeInvoiceCount,
            Lines = lines.Select(line => new
            {
                line.ItemNameOriginal,
                line.SpecificationOriginal,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount
            }).ToList()
        };
    }

    private static object VerifyIndividualScenario(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState userSession,
        Guid profileId,
        DateOnly referenceDate)
    {
        var firstStart = rental.StartBillingAsync(profileId, referenceDate, userSession).GetAwaiter().GetResult();
        Ensure(firstStart.Success, firstStart.Message);
        Ensure(firstStart.RelatedEntityId != Guid.Empty, "개별 청구 전표 ID가 반환되지 않았습니다.");

        var secondStart = rental.StartBillingAsync(profileId, referenceDate, userSession).GetAwaiter().GetResult();
        Ensure(secondStart.Success, secondStart.Message);
        Ensure(secondStart.RelatedEntityId == firstStart.RelatedEntityId, "개별 청구 전표가 재사용되지 않았습니다.");

        var invoice = local.GetInvoiceAsync(firstStart.RelatedEntityId).GetAwaiter().GetResult();
        Ensure(invoice is not null, "개별 청구 전표를 다시 불러오지 못했습니다.");

        var profile = db.RentalBillingProfiles.IgnoreQueryFilters().First(current => current.Id == profileId);
        var run = rental.GetOrCreateBillingRun(profile, referenceDate, persistChanges: false);
        Ensure(run is not null, "개별 청구 run 을 찾지 못했습니다.");

        var activeInvoiceCount = db.Invoices.IgnoreQueryFilters()
            .Count(current => !current.IsDeleted &&
                              current.IsLatestVersion &&
                              current.LinkedRentalBillingRunId == run!.RunId);
        Ensure(activeInvoiceCount == 1, $"개별 청구 전표가 {activeInvoiceCount}건 생성되었습니다.");

        var lines = invoice!.Lines.OrderBy(line => line.ItemNameOriginal).ThenBy(line => line.SpecificationOriginal).ToList();
        Ensure(lines.Count == 12, $"개별 청구 라인 수가 12건이 아닙니다. 실제 {lines.Count}건");

        foreach (var month in new[] { 6, 7, 8 })
        {
            var imcLines = lines.Where(current =>
                string.Equals(current.ItemNameOriginal, $"사무기기 렌탈대금[{month}월]", StringComparison.Ordinal) &&
                string.Equals(current.SpecificationOriginal, "리코 IMC 2010", StringComparison.Ordinal))
                .ToList();
            Ensure(imcLines.Count == 3, $"개별 청구 {month}월 IMC 2010 라인 수가 3건이 아닙니다. 실제: {imcLines.Count}");
            Ensure(imcLines.All(line => line.Quantity == 1m), $"개별 청구 {month}월 IMC 2010 수량은 장비별 1이어야 합니다.");
            Ensure(imcLines.All(line => line.UnitPrice == 240000m), $"개별 청구 {month}월 IMC 2010 단가가 240000원이 아닙니다.");
            Ensure(imcLines.Sum(line => line.LineAmount) == 720000m, $"개별 청구 {month}월 IMC 2010 합계가 720000원이 아닙니다. 실제: {imcLines.Sum(line => line.LineAmount)}");

            var samsungLine = lines.SingleOrDefault(current =>
                string.Equals(current.ItemNameOriginal, $"사무기기 렌탈대금[{month}월]", StringComparison.Ordinal) &&
                string.Equals(current.SpecificationOriginal, "SL-M2670FN", StringComparison.Ordinal));
            Ensure(samsungLine is not null, $"개별 청구 {month}월 SL-M2670FN 라인을 찾지 못했습니다.");
            Ensure(samsungLine!.Quantity == 1m, $"개별 청구 {month}월 SL-M2670FN 수량이 1이 아닙니다. 실제: {samsungLine.Quantity}");
            Ensure(samsungLine.UnitPrice == 22000m, $"개별 청구 {month}월 SL-M2670FN 단가가 22000원이 아닙니다. 실제: {samsungLine.UnitPrice}");
            Ensure(samsungLine.LineAmount == 22000m, $"개별 청구 {month}월 SL-M2670FN 금액이 22000원이 아닙니다. 실제: {samsungLine.LineAmount}");
        }

        return new
        {
            FirstStartMessage = firstStart.Message,
            SecondStartMessage = secondStart.Message,
            InvoiceId = firstStart.RelatedEntityId,
            ActiveInvoiceCount = activeInvoiceCount,
            Lines = lines.Select(line => new
            {
                line.ItemNameOriginal,
                line.SpecificationOriginal,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount
            }).ToList()
        };
    }

    private static object VerifyJanuaryBoundaryScenarios(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState adminSession)
    {
        var quarterEndCustomerId = SeedCustomer(local, adminSession, "ZZZ-분기말 후불 검증 거래처", "999-99-91001");
        var quarterEndProfileId = Guid.NewGuid();
        var quarterEndSave = SaveBillingProfile(
            rental,
            adminSession,
            quarterEndProfileId,
            quarterEndCustomerId,
            customerName: "ZZZ-분기말 후불 검증 거래처",
            billingType: "묶음",
            cycleMonths: 3,
            monthlyAmount: 100000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "사무기기 렌탈대금",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 100000m,
                    Amount = 100000m,
                    IncludedAssetIds = new List<Guid>()
                }
            },
            billingAdvanceMode: "후불",
            billingStartDate: new DateOnly(2026, 3, 1),
            billingAnchorDate: new DateOnly(2026, 3, 1),
            billingAnchorMonth: 3,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeEndOfMonth);
        Ensure(quarterEndSave.Success, quarterEndSave.Message);

        var quarterEndProfile = db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefault(current => current.Id == quarterEndProfileId);
        Ensure(quarterEndProfile is not null, "분기말 후불 검증 프로필을 불러오지 못했습니다.");
        var quarterEndRun = rental.GetOrCreateBillingRun(quarterEndProfile!, new DateOnly(2026, 3, 25), persistChanges: false);
        Ensure(quarterEndRun is not null, "분기말 후불 청구 run을 계산하지 못했습니다.");

        var arrearsMonths = BuildMonthLabels(quarterEndRun!);
        Ensure(
            arrearsMonths.SequenceEqual(new[] { 3, 4, 5 }),
            $"시작월 3월인 후불 3개월 청구는 3월, 4월, 5월이어야 합니다. 실제: {string.Join(", ", arrearsMonths)}");

        var explicitAnchorCustomerId = SeedCustomer(local, adminSession, "ZZZ-연도경계 후불 검증 거래처", "999-99-91002");
        var explicitAnchorProfileId = Guid.NewGuid();
        var explicitAnchorSave = SaveBillingProfile(
            rental,
            adminSession,
            explicitAnchorProfileId,
            explicitAnchorCustomerId,
            customerName: "ZZZ-연도경계 후불 검증 거래처",
            billingType: "묶음",
            cycleMonths: 3,
            monthlyAmount: 100000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "사무기기 렌탈대금",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 100000m,
                    Amount = 100000m,
                    IncludedAssetIds = new List<Guid>()
                }
            },
            billingAdvanceMode: "후불",
            billingStartDate: new DateOnly(2026, 1, 1),
            billingAnchorDate: new DateOnly(2026, 1, 1),
            billingAnchorMonth: 1,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeEndOfMonth);
        Ensure(explicitAnchorSave.Success, explicitAnchorSave.Message);

        var explicitAnchorProfile = db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefault(current => current.Id == explicitAnchorProfileId);
        Ensure(explicitAnchorProfile is not null, "연도경계 후불 검증 프로필을 불러오지 못했습니다.");
        var explicitAnchorRun = rental.GetOrCreateBillingRun(explicitAnchorProfile!, new DateOnly(2026, 1, 25), persistChanges: false);
        Ensure(explicitAnchorRun is not null, "연도경계 후불 청구 run을 계산하지 못했습니다.");

        var explicitAnchorMonths = BuildMonthLabels(explicitAnchorRun!);
        Ensure(
            explicitAnchorMonths.SequenceEqual(new[] { 1, 2, 3 }),
            $"시작월 1월인 후불 1월 3개월 청구는 1월, 2월, 3월이어야 합니다. 실제: {string.Join(", ", explicitAnchorMonths)}");

        var advanceCustomerId = SeedCustomer(local, adminSession, "ZZZ-연도경계 선불 검증 거래처", "999-99-91003");
        var advanceProfileId = Guid.NewGuid();
        var advanceSave = SaveBillingProfile(
            rental,
            adminSession,
            advanceProfileId,
            advanceCustomerId,
            customerName: "ZZZ-연도경계 선불 검증 거래처",
            billingType: "묶음",
            cycleMonths: 3,
            monthlyAmount: 100000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "사무기기 렌탈대금",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 100000m,
                    Amount = 100000m,
                    IncludedAssetIds = new List<Guid>()
                }
            },
            billingAdvanceMode: "선불",
            billingStartDate: new DateOnly(2026, 1, 1),
            billingAnchorDate: new DateOnly(2026, 1, 1),
            billingAnchorMonth: 1,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeEndOfMonth);
        Ensure(advanceSave.Success, advanceSave.Message);

        var advanceProfile = db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefault(current => current.Id == advanceProfileId);
        Ensure(advanceProfile is not null, "선불 연도경계 검증 프로필을 불러오지 못했습니다.");
        var advanceRun = rental.GetOrCreateBillingRun(advanceProfile!, new DateOnly(2026, 1, 25), persistChanges: false);
        Ensure(advanceRun is not null, "선불 연도경계 청구 run을 계산하지 못했습니다.");

        var advanceMonths = BuildMonthLabels(advanceRun!);
        Ensure(
            advanceMonths.SequenceEqual(new[] { 1, 2, 3 }),
            $"선불 1월 3개월 청구는 1월, 2월, 3월이어야 합니다. 실제: {string.Join(", ", advanceMonths)}");

        return new
        {
            QuarterEndArrears = new
            {
                ScheduledDate = quarterEndRun!.ScheduledDate,
                PeriodStartDate = quarterEndRun.PeriodStartDate,
                PeriodEndDate = quarterEndRun.PeriodEndDate,
                Months = arrearsMonths
            },
            ExplicitAnchorArrears = new
            {
                ScheduledDate = explicitAnchorRun!.ScheduledDate,
                PeriodStartDate = explicitAnchorRun.PeriodStartDate,
                PeriodEndDate = explicitAnchorRun.PeriodEndDate,
                Months = explicitAnchorMonths
            },
            Advance = new
            {
                ScheduledDate = advanceRun!.ScheduledDate,
                PeriodStartDate = advanceRun.PeriodStartDate,
                PeriodEndDate = advanceRun.PeriodEndDate,
                Months = advanceMonths
            }
        };
    }

    private static object VerifyCustomerResolutionFallbackScenario(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState adminSession,
        SessionState userSession)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "인천보건환경연구원[대기평가과]",
            NameMatchKey = "인천보건환경연구원[대기평가과]".ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-555-9999",
            BusinessNumber = string.Empty,
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Address = "테스트 주소"
        };

        var customerSave = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();
        Ensure(customerSave.Success, customerSave.Message);

        SeedResolutionCandidateAssets(db, customer.Id);

        var profile = new LocalRentalBillingProfile
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CustomerName = "[보건환경연구원]대기평가과",
            BusinessNumber = string.Empty,
            InstallSiteName = "[보건환경연구원]대기평가과",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            BillingMethod = "전자세금계산서",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 30,
            BillingCycleMonths = 1,
            BillingStartDate = new DateOnly(2026, 3, 1),
            BillingAnchorDate = new DateOnly(2026, 3, 1),
            MonthlyAmount = 150000m,
            BillingTemplateJson = rental.SerializeBillingTemplateItems(new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "IMC2010",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 150000m,
                    Amount = 150000m,
                    IncludedAssetIds = new List<Guid>()
                }
            })
        };

        var profileSave = rental.SaveBillingProfileAsync(profile, adminSession).GetAwaiter().GetResult();
        Ensure(profileSave.Success, profileSave.Message);

        var start = rental.StartBillingAsync(profile.Id, new DateOnly(2026, 3, 31), userSession).GetAwaiter().GetResult();
        Ensure(start.Success, $"강한 키(CustomerId) 기반 자동 연결 청구는 성공해야 합니다. 실제 메시지: {start.Message}");
        Ensure(start.RelatedEntityId != Guid.Empty, "강한 키(CustomerId) 기반 자동 연결 전표 ID가 반환되지 않았습니다.");

        var invoice = local.GetInvoiceAsync(start.RelatedEntityId).GetAwaiter().GetResult();
        Ensure(invoice is not null, "강한 키(CustomerId) 기반 자동 연결 전표를 다시 불러오지 못했습니다.");
        Ensure(invoice!.Lines.Count == 1, $"강한 키(CustomerId) 기반 자동 연결 전표 라인 수가 1건이 아닙니다. 실제: {invoice.Lines.Count}건");

        var persistedProfile = db.RentalBillingProfiles.IgnoreQueryFilters().First(current => current.Id == profile.Id);
        Ensure(persistedProfile.CustomerId == customer.Id, "강한 키(CustomerId) 기반 저장 후 CustomerId 가 유지되지 않았습니다.");

        var persistedItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(persistedProfile.BillingTemplateJson ?? "[]", JsonOptions) ?? new List<RentalBillingTemplateItemModel>();
        Ensure(persistedItems.Count == 1, "강한 키(CustomerId) 기반 자동 연결 후 저장된 청구항목 수가 잘못되었습니다.");
        Ensure(persistedItems[0].IncludedAssetIds.Count == 2, $"강한 키(CustomerId) 기반 자동 연결 후 IncludedAssetIds 수가 2가 아닙니다. 실제: {persistedItems[0].IncludedAssetIds.Count}");

        var linkedAssets = db.RentalAssets.IgnoreQueryFilters()
            .Where(current => current.BillingProfileId == profile.Id)
            .OrderBy(current => current.ManagementNumber)
            .ToList();
        Ensure(linkedAssets.Count == 2, $"강한 키(CustomerId) 기반 자동 연결 후 연결 자산 수가 2가 아닙니다. 실제: {linkedAssets.Count}");

        return new
        {
            start.Message,
            start.RelatedEntityId,
            LinkedAssetCount = linkedAssets.Count,
            IncludedAssetIds = persistedItems[0].IncludedAssetIds
        };
    }

    private static object VerifySingleCandidateAutoLinkScenario(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState adminSession,
        SessionState userSession)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "ZZZ-단일후보 자동연결 거래처",
            NameMatchKey = "ZZZ-단일후보 자동연결 거래처".ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-111-2222",
            BusinessNumber = "888-88-88888",
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Address = "테스트 주소"
        };

        var customerSave = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();
        Ensure(customerSave.Success, customerSave.Message);

        var assetId = SeedSingleCandidateAsset(db, customer.Id);

        var profileId = Guid.NewGuid();
        var profileSave = SaveBillingProfile(
            rental,
            adminSession,
            profileId,
            customer.Id,
            customerName: customer.NameOriginal,
            billingType: "묶음",
            cycleMonths: 1,
            monthlyAmount: 150000m,
            templateItems: new[]
            {
                new RentalBillingTemplateItemModel
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "IMC2010",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 150000m,
                    Amount = 150000m,
                    IncludedAssetIds = new List<Guid>()
                }
            },
            billingAdvanceMode: "후불",
            billingStartDate: new DateOnly(2026, 3, 1),
            billingAnchorDate: new DateOnly(2026, 3, 1));

        Ensure(profileSave.Success, profileSave.Message);

        var start = rental.StartBillingAsync(profileId, new DateOnly(2026, 3, 31), userSession).GetAwaiter().GetResult();
        Ensure(start.Success, $"단일 후보 자동 연결 청구는 성공해야 합니다. 실제: {start.Message}");
        Ensure(start.RelatedEntityId != Guid.Empty, "단일 후보 자동 연결 전표 ID가 반환되지 않았습니다.");

        var invoice = local.GetInvoiceAsync(start.RelatedEntityId).GetAwaiter().GetResult();
        Ensure(invoice is not null, "단일 후보 자동 연결 전표를 다시 불러오지 못했습니다.");
        var line = invoice!.Lines.Single();
        Ensure(invoice.Lines.Count == 1, $"단일 후보 자동 연결 전표 라인 수가 1건이 아닙니다. 실제: {invoice.Lines.Count}건");
        Ensure(string.Equals(line.SpecificationOriginal, "IMC2010", StringComparison.Ordinal), $"단일 후보 자동 연결 대표 규격이 잘못되었습니다. 실제: {line.SpecificationOriginal}");

        var persistedProfile = db.RentalBillingProfiles.IgnoreQueryFilters().First(current => current.Id == profileId);
        var persistedItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(persistedProfile.BillingTemplateJson ?? "[]", JsonOptions) ?? new List<RentalBillingTemplateItemModel>();
        Ensure(persistedItems.Count == 1, "단일 후보 자동 연결 후 저장된 청구항목 수가 잘못되었습니다.");
        Ensure(persistedItems[0].IncludedAssetIds.Count == 1, $"단일 후보 자동 연결 후 IncludedAssetIds 수가 1이 아닙니다. 실제: {persistedItems[0].IncludedAssetIds.Count}");
        Ensure(persistedItems[0].IncludedAssetIds[0] == assetId, "단일 후보 자동 연결 후 잘못된 자산이 저장되었습니다.");

        var persistedAsset = db.RentalAssets.IgnoreQueryFilters().First(current => current.Id == assetId);
        Ensure(persistedAsset.BillingProfileId == profileId, "단일 후보 자동 연결 후 자산 BillingProfileId 가 프로필에 연결되지 않았습니다.");

        return new
        {
            start.Message,
            start.RelatedEntityId,
            LinkedAssetId = assetId,
            PersistedIncludedAssetIds = persistedItems[0].IncludedAssetIds
        };
    }

    private static object VerifyLegacyLinkedAssetFallbackScenario(
        LocalDbContext db,
        LocalStateService local,
        RentalStateService rental,
        SessionState adminSession,
        SessionState userSession)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "ZZZ-기존연결 백필 거래처",
            NameMatchKey = "ZZZ-기존연결 백필 거래처".ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-333-4444",
            BusinessNumber = "777-77-77777",
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Address = "테스트 주소"
        };

        var customerSave = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();
        Ensure(customerSave.Success, customerSave.Message);

        var profileId = Guid.NewGuid();
        var assetId = SeedLegacyLinkedAsset(db, profileId, customer.NameOriginal);
        var now = DateTime.UtcNow;
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = profileId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            BusinessNumber = customer.BusinessNumber,
            InstallSiteName = "사무실",
            ItemName = "IMC2010",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            BillingMethod = "전자세금계산서",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            MonthlyAmount = 150000m,
            BillingTemplateJson = "[]",
            ProfileKey = $"legacy|{customer.Id:D}",
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = false
        });
        db.SaveChanges();

        var includedAssets = rental.GetIncludedBillingAssetsAsync(
                profileId,
                Array.Empty<Guid>(),
                customer.Id,
                DomainConstants.OfficeUsenet,
                userSession)
            .GetAwaiter()
            .GetResult();
        Ensure(includedAssets.Count == 1, $"기존 연결 장비 백필 후보는 1건이어야 합니다. 실제: {includedAssets.Count}");
        Ensure(includedAssets[0].Id == assetId, "기존 연결 장비 백필 후보가 잘못 연결되었습니다.");

        var profile = db.RentalBillingProfiles.IgnoreQueryFilters().First(current => current.Id == profileId);
        var templateItems = rental.GetBillingTemplateItems(profile, includedAssets);
        Ensure(templateItems.Count == 1, $"기존 연결 장비 백필 템플릿 수가 1건이어야 합니다. 실제: {templateItems.Count}");
        Ensure(templateItems[0].IncludedAssetIds.Count == 1, $"기존 연결 장비 백필 IncludedAssetIds 수가 1이 아닙니다. 실제: {templateItems[0].IncludedAssetIds.Count}");
        Ensure(templateItems[0].IncludedAssetIds[0] == assetId, "기존 연결 장비 백필 IncludedAssetIds가 잘못되었습니다.");

        var viewModel = new RentalBillingViewModel(rental, local, userSession);
        viewModel.LoadAsync().GetAwaiter().GetResult();
        var row = viewModel.Rows.FirstOrDefault(current => current.Source.Id == profileId);
        Ensure(row is not null, "기존 연결 장비 백필 프로필을 화면 목록에서 찾지 못했습니다.");
        viewModel.SelectedRow = row;
        for (var attempt = 0; attempt < 20 && viewModel.IncludedAssets.Count == 0; attempt++)
            Task.Delay(100).GetAwaiter().GetResult();

        Ensure(viewModel.IncludedAssets.Count == 1, $"기존 연결 장비 백필 후 화면 내부 포함 장비가 1건이어야 합니다. 실제: {viewModel.IncludedAssets.Count}");
        Ensure(viewModel.IncludedAssets[0].AssetId == assetId, "기존 연결 장비 백필 후 화면 내부 포함 장비가 잘못 표시되었습니다.");

        return new
        {
            IncludedAssetId = assetId,
            IncludedAssetCount = viewModel.IncludedAssets.Count,
            TemplateIncludedAssetIds = templateItems[0].IncludedAssetIds
        };
    }

    private static Guid SeedCustomer(LocalStateService local, SessionState adminSession, string name, string businessNumber)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-555-1234",
            BusinessNumber = businessNumber,
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Address = "테스트 주소"
        };

        var result = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();
        Ensure(result.Success, result.Message);
        return result.EntityId;
    }

    private static void SeedResolutionCandidateAssets(LocalDbContext db, Guid customerId)
    {
        var now = DateTime.UtcNow;
        db.RentalAssets.AddRange(
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                BillingProfileId = null,
                AssetKey = "USENET|C-001",
                ManagementCompanyCode = DomainConstants.OfficeUsenet,
                ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "C-001",
                ManagementId = "C-001",
                ItemName = "IMC2010",
                CustomerName = "[보건환경연구원]대기평가과",
                CurrentCustomerName = "[보건환경연구원]대기평가과",
                InstallSiteName = "[보건환경연구원]대기평가과",
                InstallLocation = "[보건환경연구원]대기평가과",
                BillingEligibilityStatus = "청구가능",
                AssetStatus = "임대진행중",
                MonthlyFee = 150000m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                BillingProfileId = null,
                AssetKey = "USENET|C-002",
                ManagementCompanyCode = DomainConstants.OfficeUsenet,
                ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "C-002",
                ManagementId = "C-002",
                ItemName = "IMC2010",
                CustomerName = "[보건환경연구원]대기평가과",
                CurrentCustomerName = "[보건환경연구원]대기평가과",
                InstallSiteName = "[보건환경연구원]대기평가과",
                InstallLocation = "[보건환경연구원]대기평가과",
                BillingEligibilityStatus = "청구가능",
                AssetStatus = "임대진행중",
                MonthlyFee = 150000m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

        db.SaveChanges();
    }

    private static Guid SeedSingleCandidateAsset(LocalDbContext db, Guid customerId)
    {
        var now = DateTime.UtcNow;
        var asset = new LocalRentalAsset
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BillingProfileId = null,
            AssetKey = "USENET|D-001",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            ManagementNumber = "D-001",
            ManagementId = "D-001",
            ItemName = "IMC2010",
            CustomerName = "ZZZ-단일후보 자동연결 거래처",
            CurrentCustomerName = "ZZZ-단일후보 자동연결 거래처",
            InstallSiteName = "단일후보 테스트실",
            InstallLocation = "단일후보 테스트실",
            BillingEligibilityStatus = "청구가능",
            AssetStatus = "임대진행중",
            MonthlyFee = 150000m,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.RentalAssets.Add(asset);
        db.SaveChanges();
        return asset.Id;
    }

    private static Guid SeedLegacyLinkedAsset(LocalDbContext db, Guid billingProfileId, string customerName)
    {
        var now = DateTime.UtcNow;
        var asset = new LocalRentalAsset
        {
            Id = Guid.NewGuid(),
            CustomerId = null,
            BillingProfileId = billingProfileId,
            AssetKey = "USENET|LEGACY-001",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            ManagementNumber = "LEGACY-001",
            ManagementId = "LEGACY-001",
            ItemName = "IMC2010",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            InstallSiteName = "사무실",
            InstallLocation = "사무실",
            BillingEligibilityStatus = "청구가능",
            AssetStatus = "임대진행중",
            MonthlyFee = 150000m,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.RentalAssets.Add(asset);
        db.SaveChanges();
        return asset.Id;
    }

    private static void SeedGroupedAssets(LocalDbContext db, Guid customerId, Guid billingProfileId)
    {
        var now = DateTime.UtcNow;
        db.RentalAssets.AddRange(
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                BillingProfileId = billingProfileId,
                AssetKey = "USENET|A-001",
                ManagementCompanyCode = DomainConstants.OfficeUsenet,
                ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "A-001",
                ManagementId = "A-001",
                ItemName = "리코 IMC 2000",
                CustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                CurrentCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                InstallSiteName = "묶음 테스트실",
                InstallLocation = "묶음 테스트실",
                BillingEligibilityStatus = "청구가능",
                AssetStatus = "임대진행중",
                MonthlyFee = 0m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                BillingProfileId = billingProfileId,
                AssetKey = "USENET|A-002",
                ManagementCompanyCode = DomainConstants.OfficeUsenet,
                ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "A-002",
                ManagementId = "A-002",
                ItemName = "SL-M4070FR",
                CustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                CurrentCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                InstallSiteName = "묶음 테스트실",
                InstallLocation = "묶음 테스트실",
                BillingEligibilityStatus = "청구가능",
                AssetStatus = "임대진행중",
                MonthlyFee = 0m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });

        db.SaveChanges();
    }

    private static void SeedIndividualAssets(LocalDbContext db, Guid customerId, Guid billingProfileId)
    {
        var now = DateTime.UtcNow;
        db.RentalAssets.AddRange(
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-001", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "B-001", ManagementId = "B-001",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-002", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "B-002", ManagementId = "B-002",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-003", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "B-003", ManagementId = "B-003",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-010", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                ManagementNumber = "B-010", ManagementId = "B-010",
                ItemName = "SL-M2670FN", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 22000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });

        db.SaveChanges();
    }

    private static LocalMutationResult SaveBillingProfile(
        RentalStateService rental,
        SessionState adminSession,
        Guid profileId,
        Guid customerId,
        string customerName,
        string billingType,
        int cycleMonths,
        decimal monthlyAmount,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        string billingAdvanceMode = "후불",
        DateOnly? billingStartDate = null,
        DateOnly? billingAnchorDate = null,
        int? billingAnchorMonth = null,
        string? billingDayMode = null,
        string? documentIssueMode = null,
        int documentLeadDays = 0)
    {
        var profile = new LocalRentalBillingProfile
        {
            Id = profileId,
            CustomerId = customerId,
            CustomerName = customerName,
            BusinessNumber = "999-99-99999",
            InstallSiteName = customerName,
            BillingType = billingType,
            BillingAdvanceMode = billingAdvanceMode,
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            BillingMethod = "전자세금계산서",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingDayMode = billingDayMode ?? RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = cycleMonths,
            BillingAnchorMonth = billingAnchorMonth ?? 0,
            BillingStartDate = billingStartDate ?? new DateOnly(2026, 6, 1),
            BillingAnchorDate = billingAnchorDate ?? new DateOnly(2026, 6, 1),
            DocumentIssueMode = documentIssueMode ?? RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate,
            DocumentLeadDays = documentLeadDays,
            MonthlyAmount = monthlyAmount,
            BillingTemplateJson = rental.SerializeBillingTemplateItems(templateItems)
        };

        return rental.SaveBillingProfileAsync(profile, adminSession).GetAwaiter().GetResult();
    }

    private static SessionState BuildAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "billing-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = DomainConstants.OfficeUsenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = new List<string>()
        });
        return session;
    }

    private static SessionState BuildUserSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "billing-user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = DomainConstants.OfficeUsenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new List<string>
            {
                AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.InvoiceEdit
            }
        });
        return session;
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "rental-billing-invoice-smoke");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var appRoot = Path.Combine(runtimeRoot, "거래플랜");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appRoot);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static List<int> BuildMonthLabels(RentalBillingRunModel run)
    {
        var months = new List<int>();
        var current = new DateOnly(run.PeriodStartDate.Year, run.PeriodStartDate.Month, 1);
        var end = new DateOnly(run.PeriodEndDate.Year, run.PeriodEndDate.Month, 1);
        while (current <= end)
        {
            months.Add(current.Month);
            current = current.AddMonths(1);
        }

        return months;
    }
}
