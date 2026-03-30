using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
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

        var customerId = SeedCustomer(local, adminSession);
        var referenceDate = new DateOnly(2026, 6, 15);

        var groupedProfileId = Guid.NewGuid();
        SeedGroupedAssets(db, customerId, groupedProfileId, userSession.User?.Username ?? string.Empty);
        var groupedSave = SaveBillingProfile(
            rental,
            adminSession,
            groupedProfileId,
            customerId,
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

        var individualProfileId = Guid.NewGuid();
        SeedIndividualAssets(db, customerId, individualProfileId, userSession.User?.Username ?? string.Empty);
        var individualSave = SaveBillingProfile(
            rental,
            adminSession,
            individualProfileId,
            customerId,
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

        var januaryBoundaryScenario = VerifyJanuaryBoundaryScenarios(db, rental, adminSession, customerId);
        var customerResolutionScenario = VerifyCustomerResolutionFallbackScenario(db, local, rental, adminSession, userSession);

        var output = new
        {
            Grouped = groupedScenario,
            Individual = individualScenario,
            JanuaryBoundary = januaryBoundaryScenario,
            CustomerResolutionFallback = customerResolutionScenario
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

        var expectedMonths = new[] { "사무기기 렌탈대금[4월]", "사무기기 렌탈대금[5월]", "사무기기 렌탈대금[6월]" };
        foreach (var expectedMonth in expectedMonths)
        {
            var line = lines.SingleOrDefault(current => string.Equals(current.ItemNameOriginal, expectedMonth, StringComparison.Ordinal));
            Ensure(line is not null, $"묶음 청구에서 {expectedMonth} 라인을 찾지 못했습니다.");
            Ensure(string.Equals(line!.SpecificationOriginal, "리코 IMC 2000", StringComparison.Ordinal), $"묶음 청구 대표 장비명이 잘못되었습니다. 실제: {line.SpecificationOriginal}");
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
        Ensure(lines.Count == 6, $"개별 청구 라인 수가 6건이 아닙니다. 실제 {lines.Count}건");

        foreach (var month in new[] { 4, 5, 6 })
        {
            var imcLine = lines.SingleOrDefault(current =>
                string.Equals(current.ItemNameOriginal, $"사무기기 렌탈대금[{month}월]", StringComparison.Ordinal) &&
                string.Equals(current.SpecificationOriginal, "리코 IMC 2010", StringComparison.Ordinal));
            Ensure(imcLine is not null, $"개별 청구 {month}월 IMC 2010 라인을 찾지 못했습니다.");
            Ensure(imcLine!.Quantity == 3m, $"개별 청구 {month}월 IMC 2010 수량이 3이 아닙니다. 실제: {imcLine.Quantity}");
            Ensure(imcLine.UnitPrice == 240000m, $"개별 청구 {month}월 IMC 2010 단가가 240000원이 아닙니다. 실제: {imcLine.UnitPrice}");
            Ensure(imcLine.LineAmount == 720000m, $"개별 청구 {month}월 IMC 2010 금액이 720000원이 아닙니다. 실제: {imcLine.LineAmount}");

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
        RentalStateService rental,
        SessionState adminSession,
        Guid customerId)
    {
        var arrearsProfileId = Guid.NewGuid();
        var arrearsSave = SaveBillingProfile(
            rental,
            adminSession,
            arrearsProfileId,
            customerId,
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
            billingAnchorDate: new DateOnly(2026, 1, 1));
        Ensure(arrearsSave.Success, arrearsSave.Message);

        var arrearsProfile = db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefault(current => current.Id == arrearsProfileId);
        Ensure(arrearsProfile is not null, "후불 연도경계 검증 프로필을 불러오지 못했습니다.");
        var arrearsRun = rental.GetOrCreateBillingRun(arrearsProfile!, new DateOnly(2026, 1, 25), persistChanges: false);
        Ensure(arrearsRun is not null, "후불 연도경계 청구 run을 계산하지 못했습니다.");

        var arrearsMonths = BuildMonthLabels(arrearsRun!);
        Ensure(
            arrearsMonths.SequenceEqual(new[] { 11, 12, 1 }),
            $"후불 1월 3개월 청구는 11월, 12월, 1월이어야 합니다. 실제: {string.Join(", ", arrearsMonths)}");

        var advanceProfileId = Guid.NewGuid();
        var advanceSave = SaveBillingProfile(
            rental,
            adminSession,
            advanceProfileId,
            customerId,
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
            billingAnchorDate: new DateOnly(2026, 1, 1));
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
            Arrears = new
            {
                ScheduledDate = arrearsRun!.ScheduledDate,
                PeriodStartDate = arrearsRun.PeriodStartDate,
                PeriodEndDate = arrearsRun.PeriodEndDate,
                Months = arrearsMonths
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

        SeedResolutionCandidateAssets(db, customer.Id, userSession.User?.Username ?? string.Empty);

        var profile = new LocalRentalBillingProfile
        {
            Id = Guid.NewGuid(),
            CustomerId = null,
            CustomerName = "[보건환경연구원]대기평가과",
            BusinessNumber = string.Empty,
            RealCustomerName = "[보건환경연구원]대기평가과",
            BillToCustomerName = "[보건환경연구원]대기평가과",
            InstallSiteName = "[보건환경연구원]대기평가과",
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            AssignedUsername = userSession.User?.Username ?? "billing-user",
            BillingMethod = "전자세금계산서",
            PaymentMethod = "계좌이체",
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
        Ensure(!start.Success, "후보 장비 연결 검증은 실패해야 합니다.");
        Ensure(!start.Message.Contains("거래처를 찾을 수 없습니다", StringComparison.Ordinal), $"약칭 거래처 매칭이 실패했습니다. 실제 메시지: {start.Message}");
        Ensure(start.Message.Contains("후보 장비 2대", StringComparison.Ordinal), $"후보 장비 안내 메시지가 누락되었습니다. 실제 메시지: {start.Message}");
        Ensure(start.Message.Contains("선택 장비를 현재 품목에 연결", StringComparison.Ordinal), $"후보 장비 연결 안내 문구가 누락되었습니다. 실제 메시지: {start.Message}");

        return new
        {
            start.Message
        };
    }

    private static Guid SeedCustomer(LocalStateService local, SessionState adminSession)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "ZZZ-렌탈 청구 자동전표 검증 거래처",
            NameMatchKey = "ZZZ-렌탈 청구 자동전표 검증 거래처".ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Phone = "032-555-1234",
            BusinessNumber = "999-99-99999",
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Address = "테스트 주소"
        };

        var result = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();
        Ensure(result.Success, result.Message);
        return result.EntityId;
    }

    private static void SeedResolutionCandidateAssets(LocalDbContext db, Guid customerId, string assignedUsername)
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
                AssignedUsername = assignedUsername,
                ManagementNumber = "C-001",
                ManagementId = "C-001",
                ItemName = "IMC2010",
                CustomerName = "[보건환경연구원]대기평가과",
                CurrentCustomerName = "[보건환경연구원]대기평가과",
                BillToCustomerName = "[보건환경연구원]대기평가과",
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
                AssignedUsername = assignedUsername,
                ManagementNumber = "C-002",
                ManagementId = "C-002",
                ItemName = "IMC2010",
                CustomerName = "[보건환경연구원]대기평가과",
                CurrentCustomerName = "[보건환경연구원]대기평가과",
                BillToCustomerName = "[보건환경연구원]대기평가과",
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

    private static void SeedGroupedAssets(LocalDbContext db, Guid customerId, Guid billingProfileId, string assignedUsername)
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
                AssignedUsername = assignedUsername,
                ManagementNumber = "A-001",
                ManagementId = "A-001",
                ItemName = "리코 IMC 2000",
                CustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                CurrentCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
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
                AssignedUsername = assignedUsername,
                ManagementNumber = "A-002",
                ManagementId = "A-002",
                ItemName = "SL-M4070FR",
                CustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                CurrentCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 묶음 검증 거래처",
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

    private static void SeedIndividualAssets(LocalDbContext db, Guid customerId, Guid billingProfileId, string assignedUsername)
    {
        var now = DateTime.UtcNow;
        db.RentalAssets.AddRange(
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-001", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                AssignedUsername = assignedUsername, ManagementNumber = "B-001", ManagementId = "B-001",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 개별 검증 거래처", InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-002", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                AssignedUsername = assignedUsername, ManagementNumber = "B-002", ManagementId = "B-002",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 개별 검증 거래처", InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-003", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                AssignedUsername = assignedUsername, ManagementNumber = "B-003", ManagementId = "B-003",
                ItemName = "리코 IMC 2010", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 개별 검증 거래처", InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
                BillingEligibilityStatus = "청구가능", AssetStatus = "임대진행중", MonthlyFee = 240000m,
                CreatedAtUtc = now, UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = Guid.NewGuid(), CustomerId = customerId, BillingProfileId = billingProfileId,
                AssetKey = "USENET|B-010", ManagementCompanyCode = DomainConstants.OfficeUsenet, ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
                AssignedUsername = assignedUsername, ManagementNumber = "B-010", ManagementId = "B-010",
                ItemName = "SL-M2670FN", CustomerName = "ZZZ-렌탈 개별 검증 거래처", CurrentCustomerName = "ZZZ-렌탈 개별 검증 거래처",
                BillToCustomerName = "ZZZ-렌탈 개별 검증 거래처", InstallSiteName = "개별 테스트실", InstallLocation = "개별 테스트실",
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
        DateOnly? billingAnchorDate = null)
    {
        var profile = new LocalRentalBillingProfile
        {
            Id = profileId,
            CustomerId = customerId,
            CustomerName = customerName,
            BusinessNumber = "999-99-99999",
            RealCustomerName = customerName,
            BillToCustomerName = customerName,
            InstallSiteName = customerName,
            BillingType = billingType,
            BillingAdvanceMode = billingAdvanceMode,
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            AssignedUsername = "billing-user",
            BillingMethod = "전자세금계산서",
            PaymentMethod = "계좌이체",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            SettlementStatus = PaymentFlowConstants.SettlementStatusPending,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            BillingDay = 25,
            BillingCycleMonths = cycleMonths,
            BillingStartDate = billingStartDate ?? new DateOnly(2026, 6, 1),
            BillingAnchorDate = billingAnchorDate ?? new DateOnly(2026, 6, 1),
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
            Permissions = new List<string>()
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
