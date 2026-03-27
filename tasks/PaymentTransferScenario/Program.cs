using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
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

        var officeAccess = new OfficeAccessService();
        var adminSession = BuildAdminSession("admin", DomainConstants.OfficeUsenet);
        var local = new LocalStateService(db, officeAccess, new SyncRequestDispatcher(), adminSession);

        var yeonsuSession = BuildUserSession("yeonsu-user", DomainConstants.OfficeYeonsu, TenantScopeCatalog.UsenetGroup);
        var itworldSession = BuildUserSession("itworld-user", DomainConstants.OfficeItworld, TenantScopeCatalog.Itworld, TenantScopeCatalog.ScopeTenantAll);
        var usenetSession = BuildUserSession("usenet-user", DomainConstants.OfficeUsenet, TenantScopeCatalog.UsenetGroup, TenantScopeCatalog.ScopeTenantAll);

        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "시나리오 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            Phone = "010-1234-5678"
        };
        var customerResult = local.UpsertCustomerAsync(customer, adminSession).GetAwaiter().GetResult();

        var itworldCustomer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "아이티월드 시나리오 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeItworld,
            Phone = "[REDACTED_PHONE]"
        };
        var itworldCustomerResult = local.UpsertCustomerAsync(itworldCustomer, itworldSession).GetAwaiter().GetResult();

        var usenetCustomer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "유즈넷 시나리오 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            Phone = "032-555-0000"
        };
        var usenetCustomerResult = local.UpsertCustomerAsync(usenetCustomer, usenetSession).GetAwaiter().GetResult();

        var item = new LocalItem
        {
            Id = Guid.NewGuid(),
            NameOriginal = "시나리오 품목",
            SpecificationOriginal = "규격-A",
            Unit = "EA"
        };
        local.UpsertItemAsync(item).GetAwaiter().GetResult();

        var purchaseInvoice = new LocalInvoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            VoucherType = VoucherType.Purchase,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
            Lines = new List<LocalInvoiceLine>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    SpecificationOriginal = item.SpecificationOriginal,
                    Unit = item.Unit,
                    Quantity = 10m,
                    UnitPrice = 1000m,
                    LineAmount = 10000m
                }
            }
        };
        var invoiceResult = local.SaveInvoiceAsync(
            purchaseInvoice,
            new InvoiceSaveContext
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                OfficeCode = DomainConstants.OfficeUsenet
            },
            adminSession).GetAwaiter().GetResult();

        var initialUsenetStock = GetStock(local, item.Id, DomainConstants.WarehouseUsenetMain);
        var initialYeonsuStock = GetStock(local, item.Id, DomainConstants.WarehouseYeonsuMain);

        var advanceDepositTransaction = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TransactionDate = DateOnly.FromDateTime(DateTime.Today),
            TransactionKind = PaymentFlowConstants.TransactionKindAdvanceDeposit,
            CashReceipt = 100000m,
            ReceiptTotal = 100000m,
            Note = "시나리오 선수금 입금",
            Memo = "증빙 테스트"
        };
        var advanceDepositResult = local.SaveTransactionAsync(advanceDepositTransaction, adminSession).GetAwaiter().GetResult();
        var advanceBalanceAfterDeposit = GetAdvanceBalanceAsync(local, customer.Id, adminSession);

        var sampleFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "payment-proof-sample.pdf");
        File.WriteAllText(sampleFile, "sample proof file");

        var attachmentSave = local.SaveTransactionAttachmentAsync(advanceDepositTransaction.Id, sampleFile, "입금확인증", "입금 확인 메모", adminSession).GetAwaiter().GetResult();
        var attachmentList = local.GetTransactionAttachmentsAsync(advanceDepositTransaction.Id, adminSession).GetAwaiter().GetResult();
        var attachment = attachmentList.First();
        var verifyResult = local.UpdateTransactionAttachmentVerificationAsync(attachment.Id, "확인완료", "관리자 확인", adminSession).GetAwaiter().GetResult();
        var attachmentAfterVerify = local.GetTransactionAttachmentsAsync(advanceDepositTransaction.Id, adminSession).GetAwaiter().GetResult().First();

        var invoiceSettlementSummaryBefore = GetInvoiceSettlementSummaryAsync(local, purchaseInvoice.Id, adminSession);
        var invoiceSettlementTransaction = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TransactionDate = DateOnly.FromDateTime(DateTime.Today),
            TransactionKind = PaymentFlowConstants.TransactionKindInvoicePayment,
            LinkedInvoiceId = purchaseInvoice.Id,
            PaymentTotal = 8000m,
            SettlementAmount = 8000m,
            Note = "전표 지급",
            Memo = "전표 연동 테스트"
        };
        var invoiceSettlementResult = local.SaveTransactionAsync(invoiceSettlementTransaction, adminSession).GetAwaiter().GetResult();
        var invoiceSettlementSummaryAfter = GetInvoiceSettlementSummaryAsync(local, purchaseInvoice.Id, adminSession);
        var advanceBalanceAfterSettlement = GetAdvanceBalanceAsync(local, customer.Id, adminSession);

        var itworldGeneralPayment = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = itworldCustomer.Id,
            TransactionDate = DateOnly.FromDateTime(DateTime.Today),
            TransactionKind = PaymentFlowConstants.TransactionKindPayment,
            CashPayment = 55000m,
            PaymentTotal = 55000m,
            Note = "아이티월드 일반지급",
            Memo = "오피스 범위 검증"
        };
        var itworldGeneralPaymentResult = local.SaveTransactionAsync(itworldGeneralPayment, itworldSession).GetAwaiter().GetResult();
        var itworldGeneralPaymentSaved = local.GetTransactionsAsync(itworldCustomer.Id, itworldSession).GetAwaiter().GetResult()
            .FirstOrDefault(transaction => transaction.Id == itworldGeneralPayment.Id);

        var usenetGeneralPayment = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = usenetCustomer.Id,
            TransactionDate = DateOnly.FromDateTime(DateTime.Today),
            TransactionKind = PaymentFlowConstants.TransactionKindPayment,
            CashPayment = 33000m,
            PaymentTotal = 33000m,
            Note = "유즈넷 일반지급",
            Memo = "오피스 범위 검증"
        };
        var usenetGeneralPaymentResult = local.SaveTransactionAsync(usenetGeneralPayment, usenetSession).GetAwaiter().GetResult();
        var usenetGeneralPaymentSaved = local.GetTransactionsAsync(usenetCustomer.Id, usenetSession).GetAwaiter().GetResult()
            .FirstOrDefault(transaction => transaction.Id == usenetGeneralPayment.Id);

        var transfer = new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
            ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
            Memo = "수령확정 테스트",
            Lines = new List<LocalInventoryTransferLine>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    SpecificationOriginal = item.SpecificationOriginal,
                    Unit = item.Unit,
                    Quantity = 5m,
                    ReceivedQuantity = 5m,
                    Remark = "요청"
                }
            }
        };
        var transferSave = local.SaveInventoryTransferAsync(transfer, adminSession).GetAwaiter().GetResult();
        var pendingUsenetStock = GetStock(local, item.Id, DomainConstants.WarehouseUsenetMain);
        var pendingYeonsuStock = GetStock(local, item.Id, DomainConstants.WarehouseYeonsuMain);
        var savedTransfer = local.GetInventoryTransferAsync(transfer.Id).GetAwaiter().GetResult()!;
        savedTransfer.Lines.First().ReceivedQuantity = 4m;
        savedTransfer.Lines.First().ReceiptRemark = "1개 누락";
        var transferConfirm = local.ConfirmInventoryTransferReceiptAsync(savedTransfer.Id, savedTransfer.Lines.ToList(), "실물 확인 완료", yeonsuSession).GetAwaiter().GetResult();
        var transferAfterConfirm = local.GetInventoryTransferAsync(savedTransfer.Id).GetAwaiter().GetResult()!;
        var confirmedUsenetStock = GetStock(local, item.Id, DomainConstants.WarehouseUsenetMain);
        var confirmedYeonsuStock = GetStock(local, item.Id, DomainConstants.WarehouseYeonsuMain);

        var rejectTransfer = new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
            ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
            Memo = "반려 테스트",
            Lines = new List<LocalInventoryTransferLine>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    SpecificationOriginal = item.SpecificationOriginal,
                    Unit = item.Unit,
                    Quantity = 2m,
                    ReceivedQuantity = 2m,
                    Remark = "반려 요청"
                }
            }
        };
        var rejectSave = local.SaveInventoryTransferAsync(rejectTransfer, adminSession).GetAwaiter().GetResult();
        var rejectPendingUsenetStock = GetStock(local, item.Id, DomainConstants.WarehouseUsenetMain);
        var rejectPendingYeonsuStock = GetStock(local, item.Id, DomainConstants.WarehouseYeonsuMain);
        var rejectResult = local.RejectInventoryTransferAsync(rejectTransfer.Id, "포장 훼손", yeonsuSession).GetAwaiter().GetResult();
        var transferAfterReject = local.GetInventoryTransferAsync(rejectTransfer.Id).GetAwaiter().GetResult()!;
        var rejectedUsenetStock = GetStock(local, item.Id, DomainConstants.WarehouseUsenetMain);
        var rejectedYeonsuStock = GetStock(local, item.Id, DomainConstants.WarehouseYeonsuMain);

        var auditLogCount = db.AuditLogs.IgnoreQueryFilters().Count();

        var report = new
        {
            CustomerSave = customerResult.Success,
            ItworldCustomerSave = itworldCustomerResult.Success,
            UsenetCustomerSave = usenetCustomerResult.Success,
            PurchaseInvoiceSave = invoiceResult.Success,
            InitialStock = new
            {
                Usenet = initialUsenetStock,
                Yeonsu = initialYeonsuStock
            },
            TransactionSave = advanceDepositResult.Success,
            AdvanceDeposit = new
            {
                Save = advanceDepositResult.Success,
                Kind = advanceDepositTransaction.TransactionKind,
                AdvanceDelta = advanceDepositTransaction.AdvanceDelta,
                AdvanceBalance = advanceBalanceAfterDeposit
            },
            Attachment = new
            {
                Save = attachmentSave.Success,
                Count = attachmentList.Count,
                Type = attachment.AttachmentType,
                FileExists = File.Exists(attachment.StoredPath),
                HashLength = attachment.FileHash.Length,
                Verify = verifyResult.Success,
                Status = attachmentAfterVerify.VerificationStatus,
                VerifiedBy = attachmentAfterVerify.VerifiedByUsername
            },
            InvoiceSettlement = new
            {
                Before = new
                {
                    invoiceSettlementSummaryBefore.InvoiceTotal,
                    invoiceSettlementSummaryBefore.SettledAmount,
                    invoiceSettlementSummaryBefore.RemainingAmount
                },
                Save = invoiceSettlementResult.Success,
                Kind = invoiceSettlementTransaction.TransactionKind,
                LinkedInvoiceNumber = invoiceSettlementTransaction.LinkedInvoiceNumber,
                SettlementAmount = invoiceSettlementTransaction.SettlementAmount,
                After = new
                {
                    invoiceSettlementSummaryAfter.InvoiceTotal,
                    invoiceSettlementSummaryAfter.SettledAmount,
                    invoiceSettlementSummaryAfter.RemainingAmount
                },
                AdvanceBalance = advanceBalanceAfterSettlement
            },
            GeneralPaymentScopes = new
            {
                Itworld = new
                {
                    Save = itworldGeneralPaymentResult.Success,
                    Customer = itworldCustomer.NameOriginal,
                    Office = itworldSession.OfficeCode,
                    Kind = itworldGeneralPaymentSaved?.TransactionKind,
                    PaymentTotal = itworldGeneralPaymentSaved?.PaymentTotal ?? 0m
                },
                Usenet = new
                {
                    Save = usenetGeneralPaymentResult.Success,
                    Customer = usenetCustomer.NameOriginal,
                    Office = usenetSession.OfficeCode,
                    Kind = usenetGeneralPaymentSaved?.TransactionKind,
                    PaymentTotal = usenetGeneralPaymentSaved?.PaymentTotal ?? 0m
                }
            },
            TransferConfirm = new
            {
                Save = transferSave.Success,
                PendingUsenetStock = pendingUsenetStock,
                PendingYeonsuStock = pendingYeonsuStock,
                Confirm = transferConfirm.Success,
                Status = transferAfterConfirm.TransferStatus,
                ReceivedBy = transferAfterConfirm.ReceivedByUsername,
                ReceiveMemo = transferAfterConfirm.ReceiveMemo,
                RequestedQty = transferAfterConfirm.Lines.First().Quantity,
                ReceivedQty = transferAfterConfirm.Lines.First().ReceivedQuantity,
                Difference = transferAfterConfirm.Lines.First().QuantityDifference,
                ReceiptRemark = transferAfterConfirm.Lines.First().ReceiptRemark,
                ConfirmedUsenetStock = confirmedUsenetStock,
                ConfirmedYeonsuStock = confirmedYeonsuStock
            },
            TransferReject = new
            {
                Save = rejectSave.Success,
                PendingUsenetStock = rejectPendingUsenetStock,
                PendingYeonsuStock = rejectPendingYeonsuStock,
                Reject = rejectResult.Success,
                Status = transferAfterReject.TransferStatus,
                RejectReason = transferAfterReject.RejectReason,
                RejectedBy = transferAfterReject.RejectedByUsername,
                RejectedUsenetStock = rejectedUsenetStock,
                RejectedYeonsuStock = rejectedYeonsuStock
            },
            AuditLogCount = auditLogCount
        };

        Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "isolated-payment-transfer-scenario");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var georaePlanRoot = Path.Combine(runtimeRoot, "거래플랜");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", georaePlanRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        if (Directory.Exists(georaePlanRoot))
            Directory.Delete(georaePlanRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(georaePlanRoot);
    }

    private static SessionState BuildAdminSession(string username, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = new List<string>()
        });
        return session;
    }

    private static SessionState BuildUserSession(
        string username,
        string officeCode,
        string tenantCode,
        string? scopeType = null)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType ?? TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new List<string>()
        });
        return session;
    }

    private static decimal GetStock(LocalStateService local, Guid itemId, string warehouseCode)
    {
        return local.GetItemWarehouseStocksAsync().GetAwaiter().GetResult()
            .Where(stock => stock.ItemId == itemId && string.Equals(stock.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase))
            .Select(stock => stock.Quantity)
            .DefaultIfEmpty(0m)
            .First();
    }

    private static decimal GetAdvanceBalanceAsync(LocalStateService local, Guid customerId, SessionState session)
    {
        var transactions = local.GetTransactionsAsync(customerId, session).GetAwaiter().GetResult();
        return transactions.Where(transaction => !transaction.IsDeleted).Sum(transaction => transaction.AdvanceDelta);
    }

    private static InvoiceSettlementSummary GetInvoiceSettlementSummaryAsync(LocalStateService local, Guid invoiceId, SessionState session)
    {
        var invoice = local.GetInvoiceAsync(invoiceId, session).GetAwaiter().GetResult();
        if (invoice is null)
            return new InvoiceSettlementSummary();

        var settledAmount = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        return new InvoiceSettlementSummary
        {
            InvoiceTotal = invoice.TotalAmount,
            SettledAmount = settledAmount,
            RemainingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount)
        };
    }
}
