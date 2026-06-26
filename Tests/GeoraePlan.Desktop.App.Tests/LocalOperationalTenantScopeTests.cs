using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalOperationalTenantScopeTests
{
    [Fact]
    public async Task InvoiceAndTransactionQueries_RequireCurrentTenantEvenWhenOfficeMatches()
    {
        PrepareAppRoot("georaeplan-operational-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomerId = Guid.NewGuid();
            var itworldCustomerId = Guid.NewGuid();
            var usenetInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: usenetCustomerId,
                invoiceNumber: "USENET-INV");
            var itworldInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: itworldCustomerId,
                invoiceNumber: "ITWORLD-MISMATCH-INV");
            usenetInvoice.IsDirty = true;
            itworldInvoice.IsDirty = true;
            var usenetTransaction = CreateTransaction(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: usenetCustomerId,
                note: "USENET transaction");
            var itworldTransaction = CreateTransaction(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: itworldCustomerId,
                note: "ITWORLD mismatch transaction");
            usenetTransaction.IsDirty = true;
            itworldTransaction.IsDirty = true;
            var usenetPayment = new LocalPayment
            {
                Id = Guid.NewGuid(),
                InvoiceId = usenetInvoice.Id,
                PaymentDate = new DateOnly(2026, 6, 15),
                Amount = 1000m,
                IsDirty = true
            };
            var itworldPayment = new LocalPayment
            {
                Id = Guid.NewGuid(),
                InvoiceId = itworldInvoice.Id,
                PaymentDate = new DateOnly(2026, 6, 15),
                Amount = 1000m,
                IsDirty = true
            };
            var usenetAttachment = new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = usenetTransaction.Id,
                FileName = "usenet.pdf",
                StoredFileName = "usenet.pdf",
                StoredPath = "test/usenet.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                IsDirty = true
            };
            var itworldAttachment = new LocalTransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = itworldTransaction.Id,
                FileName = "itworld.pdf",
                StoredFileName = "itworld.pdf",
                StoredPath = "test/itworld.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                IsDirty = true
            };
            db.Invoices.AddRange(usenetInvoice, itworldInvoice);
            db.Transactions.AddRange(usenetTransaction, itworldTransaction);
            db.Payments.AddRange(usenetPayment, itworldPayment);
            db.TransactionAttachments.AddRange(usenetAttachment, itworldAttachment);
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.InvoiceEdit,
                AppPermissionNames.PaymentEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var invoices = await service.GetInvoicesAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                session);
            var transactions = await service.GetTransactionsAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                session);
            var ledgerInvoices = await service.GetSalesPurchaseLedgerInvoicesAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                warehouseCode: null,
                responsibleOfficeCode: OfficeCodeCatalog.Usenet,
                session);
            var report = await service.BuildIntegrityReportAsync(session);

            var invoice = Assert.Single(invoices);
            Assert.Equal("USENET-INV", invoice.InvoiceNumber);
            var ledgerInvoice = Assert.Single(ledgerInvoices);
            Assert.Equal("USENET-INV", ledgerInvoice.InvoiceNumber);
            var transaction = Assert.Single(transactions);
            Assert.Equal("USENET transaction", transaction.Note);

            var hiddenInvoice = await service.GetInvoiceAsync(itworldInvoice.Id, session);
            var hiddenLatestInvoice = await service.GetLatestInvoiceVersionAsync(itworldInvoice.Id, session);
            var hiddenInvoiceVersions = await service.GetInvoiceVersionsAsync(itworldInvoice.Id, session);
            var hiddenSettlement = await service.GetInvoiceSettlementSummaryAsync(itworldInvoice.Id, session);
            var hiddenAttachments = await service.GetTransactionAttachmentsAsync(itworldTransaction.Id, session);
            var dirtyInvoices = await service.GetDirtyInvoicesForSyncAsync(session);
            var dirtyTransactions = await service.GetDirtyTransactionsForSyncAsync(session);
            var dirtyPayments = await service.GetDirtyPaymentsForSyncAsync(session);
            var dirtyAttachments = await service.GetDirtyTransactionAttachmentsForSyncAsync(session);

            Assert.Null(hiddenInvoice);
            Assert.Null(hiddenLatestInvoice);
            Assert.Empty(hiddenInvoiceVersions);
            Assert.Equal(0m, hiddenSettlement.InvoiceTotal);
            Assert.Empty(hiddenAttachments);
            Assert.Contains(dirtyInvoices, invoice => invoice.Id == usenetInvoice.Id);
            Assert.DoesNotContain(dirtyInvoices, invoice => invoice.Id == itworldInvoice.Id);
            Assert.Contains(dirtyTransactions, current => current.Id == usenetTransaction.Id);
            Assert.DoesNotContain(dirtyTransactions, current => current.Id == itworldTransaction.Id);
            Assert.Contains(dirtyPayments, payment => payment.Id == usenetPayment.Id);
            Assert.DoesNotContain(dirtyPayments, payment => payment.Id == itworldPayment.Id);
            Assert.Contains(dirtyAttachments, attachment => attachment.Id == usenetAttachment.Id);
            Assert.DoesNotContain(dirtyAttachments, attachment => attachment.Id == itworldAttachment.Id);

            var invoiceScopeIssue = Assert.Single(report.Issues, issue => issue.Code == "out_of_scope_invoices");
            Assert.Equal(1, invoiceScopeIssue.Count);
            var transactionScopeIssue = Assert.Single(report.Issues, issue => issue.Code == "out_of_scope_transactions");
            Assert.Equal(1, transactionScopeIssue.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeliveryViewAllInvoiceQueries_StayWithinCurrentTenant()
    {
        PrepareAppRoot("georaeplan-delivery-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerId: Guid.NewGuid(),
                invoiceNumber: "USENET-DELIVERY-INV");
            var yeonsuInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                customerId: Guid.NewGuid(),
                invoiceNumber: "YEONSU-DELIVERY-INV");
            var itworldInvoice = CreateInvoice(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerId: Guid.NewGuid(),
                invoiceNumber: "ITWORLD-DELIVERY-INV");
            foreach (var invoice in new[] { usenetInvoice, yeonsuInvoice, itworldInvoice })
                invoice.IsConfirmed = true;
            db.Invoices.AddRange(usenetInvoice, yeonsuInvoice, itworldInvoice);
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.DeliveryViewAll);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var invoices = await service.GetYeonsuDeliveryInvoicesAsync(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 30),
                null,
                null,
                null,
                session);

            Assert.Contains(invoices, invoice => invoice.InvoiceNumber == "USENET-DELIVERY-INV");
            Assert.Contains(invoices, invoice => invoice.InvoiceNumber == "YEONSU-DELIVERY-INV");
            Assert.DoesNotContain(invoices, invoice => invoice.InvoiceNumber == "ITWORLD-DELIVERY-INV");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalViewAllProfileDetail_StaysWithinCurrentTenant()
    {
        PrepareAppRoot("georaeplan-rental-profile-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetProfile = CreateRentalBillingProfile(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                customerName: "USENET 렌탈 거래처",
                monthlyAmount: 11_000m);
            var yeonsuProfile = CreateRentalBillingProfile(
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                customerName: "YEONSU 렌탈 거래처",
                monthlyAmount: 22_000m);
            var itworldProfile = CreateRentalBillingProfile(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerName: "ITWORLD 렌탈 거래처",
                monthlyAmount: 33_000m);
            foreach (var profile in new[] { usenetProfile, yeonsuProfile, itworldProfile })
                profile.IsDirty = true;
            db.RentalBillingProfiles.AddRange(usenetProfile, yeonsuProfile, itworldProfile);
            await db.SaveChangesAsync();

            var viewAllSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalViewAll);
            var editAllSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalEditAll);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), viewAllSession);

            var visibleUsenetProfile = await service.GetRentalBillingProfileAsync(usenetProfile.Id, viewAllSession);
            var visibleYeonsuProfile = await service.GetRentalBillingProfileAsync(yeonsuProfile.Id, viewAllSession);
            var hiddenItworldProfile = await service.GetRentalBillingProfileAsync(itworldProfile.Id, viewAllSession);
            var hiddenItworldSettlement = await service.GetRentalSettlementSummaryAsync(itworldProfile.Id, viewAllSession);
            var dirtyProfilesForEditAll = await service.GetDirtyRentalBillingProfilesForSyncAsync(editAllSession);

            Assert.NotNull(visibleUsenetProfile);
            Assert.NotNull(visibleYeonsuProfile);
            Assert.Null(hiddenItworldProfile);
            Assert.Equal(0m, hiddenItworldSettlement.BilledAmount);
            Assert.Contains(dirtyProfilesForEditAll, profile => profile.Id == usenetProfile.Id);
            Assert.Contains(dirtyProfilesForEditAll, profile => profile.Id == yeonsuProfile.Id);
            Assert.DoesNotContain(dirtyProfilesForEditAll, profile => profile.Id == itworldProfile.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalEditAllMutations_StayWithinCurrentTenant()
    {
        PrepareAppRoot("georaeplan-rental-mutation-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itworldProfile = CreateRentalBillingProfile(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerName: "ITWORLD 렌탈 거래처",
                monthlyAmount: 33_000m);
            var itworldAsset = CreateRentalAsset(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerName: "ITWORLD 렌탈 거래처");
            db.RentalBillingProfiles.Add(itworldProfile);
            db.RentalAssets.Add(itworldAsset);
            await db.SaveChangesAsync();

            var editAllSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalEditAll);
            var service = new RentalStateService(db);

            var profileUpdate = CreateRentalBillingProfile(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerName: "ITWORLD 렌탈 거래처 수정",
                monthlyAmount: 44_000m);
            profileUpdate.Id = itworldProfile.Id;
            profileUpdate.ProfileKey = itworldProfile.ProfileKey;
            var profileResult = await service.SaveBillingProfileAsync(profileUpdate, editAllSession);

            var assetUpdate = CreateRentalAsset(
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Itworld,
                customerName: "ITWORLD 렌탈 거래처 수정");
            assetUpdate.Id = itworldAsset.Id;
            assetUpdate.AssetKey = itworldAsset.AssetKey;
            assetUpdate.ManagementId = itworldAsset.ManagementId;
            assetUpdate.ManagementNumber = itworldAsset.ManagementNumber;
            var assetResult = await service.SaveAssetAsync(assetUpdate, editAllSession);

            Assert.False(profileResult.Success, profileResult.Message);
            Assert.False(assetResult.Success, assetResult.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalEditAllOperationalMutations_StayWithinCurrentTenant()
    {
        PrepareAppRoot("georaeplan-rental-operational-mutation-tenant-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var referenceDate = new DateOnly(2026, 6, 15);
            var service = new RentalStateService(db);
            var editAllSession = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalEditAll);

            var deleteProfile = CreateRentalBillingProfile(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "삭제 대상 ITWORLD 프로필", 10_000m);
            var holdProfile = CreateRentalBillingProfile(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "보류 대상 ITWORLD 프로필", 20_000m);
            var settlementProfile = CreateRentalBillingProfile(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "입금 대상 ITWORLD 프로필", 30_000m);
            var completedProfile = CreateRentalBillingProfile(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "완납 대상 ITWORLD 프로필", 0m);
            var historyProfile = CreateRentalBillingProfile(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "삭제 이력 ITWORLD 프로필", 40_000m);
            var deleteAsset = CreateRentalAsset(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "삭제 대상 ITWORLD 자산");
            var excludeAsset = CreateRentalAsset(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "청구목록 제외 ITWORLD 자산");
            var historySaveAsset = CreateRentalAsset(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "이력 저장 ITWORLD 자산");
            var historyDeleteAsset = CreateRentalAsset(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "이력 삭제 ITWORLD 자산");
            var replacementCandidate = CreateRentalAsset(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld, "교체 후보 ITWORLD 자산");
            replacementCandidate.CustomerId = null;
            replacementCandidate.BillingProfileId = null;
            replacementCandidate.AssetStatus = "창고";
            var existingHistory = CreateRentalAssignmentHistory(historyDeleteAsset);

            db.RentalBillingProfiles.AddRange(deleteProfile, holdProfile, settlementProfile, completedProfile, historyProfile);
            db.RentalAssets.AddRange(deleteAsset, excludeAsset, historySaveAsset, historyDeleteAsset, replacementCandidate);
            db.RentalAssetAssignmentHistories.Add(existingHistory);
            await db.SaveChangesAsync();

            var historyRun = service.GetOrCreateBillingRun(historyProfile, referenceDate, persistChanges: true);
            Assert.NotNull(historyRun);
            await db.SaveChangesAsync();

            var deleteProfileResult = await service.DeleteBillingProfileAsync(deleteProfile.Id, editAllSession);
            var holdResult = await service.HoldBillingAsync(holdProfile.Id, referenceDate, "타 tenant 보류 시도", editAllSession);
            var settlementResult = await service.RegisterBillingSettlementAsync(settlementProfile.Id, referenceDate, 0m, "타 tenant 입금 시도", editAllSession);
            var completeResult = await service.MarkBillingCompletedAsync(completedProfile.Id, referenceDate, "완료", "타 tenant 완납 시도", editAllSession);
            var deleteHistoryResult = await service.DeleteBillingHistoryAsync(historyProfile.Id, historyRun!.RunId, editAllSession);
            var excludeAssetResult = await service.ExcludeUnlinkedBillingAssetFromBillingListAsync(excludeAsset.Id, editAllSession);
            var saveHistoryResult = await service.SaveAssetAssignmentHistoryAsync(
                new RentalAssetAssignmentHistoryEditRequest
                {
                    AssetId = historySaveAsset.Id,
                    IsCurrent = true,
                    LinkedAtLocal = new DateTime(2026, 6, 1),
                    CustomerName = "타 tenant 이력 저장",
                    InstallLocation = "타 tenant 설치처",
                    BillingProfileDisplay = "타 tenant 프로필",
                    ItemName = historySaveAsset.ItemName,
                    ManagementNumber = historySaveAsset.ManagementNumber,
                    MonthlyFee = 10_000m,
                    ChangeReason = "권한 범위 테스트"
                },
                editAllSession);
            var deleteHistoryEntryResult = await service.DeleteAssetAssignmentHistoryAsync(existingHistory.Id, editAllSession);
            var deleteAssetResult = await service.DeleteAssetAsync(deleteAsset.Id, editAllSession);
            var replacementCandidates = await service.GetRentalEquipmentReplacementCandidatesAsync(historySaveAsset.Id, editAllSession);

            Assert.False(deleteProfileResult.Success, deleteProfileResult.Message);
            Assert.False(holdResult.Success, holdResult.Message);
            Assert.False(settlementResult.Success, settlementResult.Message);
            Assert.False(completeResult.Success, completeResult.Message);
            Assert.False(deleteHistoryResult.Success, deleteHistoryResult.Message);
            Assert.False(excludeAssetResult.Success, excludeAssetResult.Message);
            Assert.False(saveHistoryResult.Success, saveHistoryResult.Message);
            Assert.False(deleteHistoryEntryResult.Success, deleteHistoryEntryResult.Message);
            Assert.False(deleteAssetResult.Success, deleteAssetResult.Message);
            Assert.DoesNotContain(replacementCandidates, asset => asset.Id == replacementCandidate.Id);

            Assert.False((await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == deleteProfile.Id)).IsDeleted);
            Assert.NotEqual(PaymentFlowConstants.BillingStatusOnHold, (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == holdProfile.Id)).BillingStatus);
            Assert.Empty(await db.RentalBillingLogs.IgnoreQueryFilters().Where(log => log.BillingProfileId == settlementProfile.Id || log.BillingProfileId == completedProfile.Id).ToListAsync());
            Assert.Contains(historyRun.RunId.ToString("D"), (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == historyProfile.Id)).BillingRunsJson);
            Assert.NotEqual("청구제외", (await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == excludeAsset.Id)).BillingEligibilityStatus);
            Assert.Empty(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().Where(history => history.AssetId == historySaveAsset.Id && history.Id != existingHistory.Id).ToListAsync());
            Assert.False((await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(history => history.Id == existingHistory.Id)).IsDeleted);
            Assert.False((await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == deleteAsset.Id)).IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalInvoice CreateInvoice(
        string tenantCode,
        string officeCode,
        Guid customerId,
        string invoiceNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = customerId,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 15),
            VersionGroupId = Guid.NewGuid(),
            IsLatestVersion = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalBillingProfile CreateRentalBillingProfile(
        string tenantCode,
        string officeCode,
        string customerName,
        decimal monthlyAmount)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            ProfileKey = $"TEST-{tenantCode}-{officeCode}-{Guid.NewGuid():N}",
            CustomerName = customerName,
            InstallSiteName = customerName,
            ItemName = "렌탈 장비",
            MonthlyAmount = monthlyAmount,
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingMethod = "현금",
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            BillingStatus = "청구중",
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsActive = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalAsset CreateRentalAsset(
        string tenantCode,
        string officeCode,
        string customerName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            AssetKey = $"TEST-{tenantCode}-{officeCode}-{Guid.NewGuid():N}",
            ManagementId = $"MID-{Guid.NewGuid():N}",
            ManagementNumber = $"MN-{Guid.NewGuid():N}",
            CurrentLocation = customerName,
            CurrentCustomerName = customerName,
            CustomerName = customerName,
            InstallSiteName = customerName,
            InstallLocation = customerName,
            ItemName = "렌탈 장비",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "미확인",
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalAssetAssignmentHistory CreateRentalAssignmentHistory(LocalRentalAsset asset)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            TenantCode = asset.TenantCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerName = asset.CustomerName,
            InstallLocation = asset.InstallLocation,
            BillingProfileDisplay = asset.CustomerName,
            ItemName = asset.ItemName,
            ManagementNumber = asset.ManagementNumber,
            MonthlyFee = asset.MonthlyFee,
            IsCurrent = true,
            LinkedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalTransaction CreateTransaction(
        string tenantCode,
        string officeCode,
        Guid customerId,
        string note)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            CustomerId = customerId,
            TransactionDate = new DateOnly(2026, 6, 15),
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            SettlementAmount = 1000m,
            Note = note,
            IsDeleted = false,
            IsDirty = false
        };

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
