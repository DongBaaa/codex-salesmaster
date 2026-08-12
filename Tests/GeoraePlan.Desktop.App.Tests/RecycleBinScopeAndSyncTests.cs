using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Data;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Services;
using \uac70\ub798\ud50c\ub79c.Desktop.App.ViewModels;
using \uac70\ub798\ud50c\ub79c.Shared.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RecycleBinScopeAndSyncTests
{
    [Theory]
    [InlineData("bad-gateway")]
    [InlineData("http-exception")]
    [InlineData("timeout")]
    public async Task ErpApiClient_RestoreRecycleBin_AmbiguousDispatch_IsSingleSendAndTypedUnknown(
        string responseMode)
    {
        var entityId = Guid.NewGuid();
        var handler = new RecycleBinRestoreRetryHandler(entityId, responseMode);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() =>
            api.RestoreRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = entityId,
                    Kind = "customer",
                    ExpectedRevision = 7
                }
            ]));

        Assert.Contains("서버 반영 결과를 확정할 수 없습니다", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RestoreRequestCount);
    }

    [Fact]
    public async Task ErpApiClient_RestoreRecycleBin_FirstDefinitiveConflict_RemainsConflict()
    {
        var entityId = Guid.NewGuid();
        var handler = new RecycleBinRestoreRetryHandler(entityId, "conflict");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            api.RestoreRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = entityId,
                    Kind = "customer",
                    ExpectedRevision = 7
                }
            ]));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(1, handler.RestoreRequestCount);
    }

    [Fact]
    public async Task ErpApiClient_RestoreRecycleBin_Http200ItemConflict_RemainsDefinitiveFailure()
    {
        var entityId = Guid.NewGuid();
        var handler = new RecycleBinRestoreRetryHandler(entityId, "item-conflict");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var result = await api.RestoreRecycleBinAsync(
        [
            new RecycleBinMutationTargetDto
            {
                EntityId = entityId,
                Kind = "customer",
                ExpectedRevision = 7
            }
        ]);

        Assert.NotNull(result);
        var itemResult = Assert.Single(result!.Results);
        Assert.False(itemResult.Success);
        Assert.Contains("Expected revision mismatch", itemResult.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RestoreRequestCount);
    }

    [Theory]
    [InlineData("empty-results-full-count")]
    [InlineData("duplicate-key")]
    [InlineData("missing-key")]
    [InlineData("unexpected-key")]
    [InlineData("id-mismatch")]
    [InlineData("kind-mismatch")]
    [InlineData("count-mismatch")]
    [InlineData("succeeded-count-mismatch")]
    [InlineData("null-results")]
    [InlineData("null-result")]
    [InlineData("null-messages")]
    [InlineData("null-item")]
    [InlineData("invalid-json")]
    public async Task ErpApiClient_RestoreRecycleBin_Http200InvalidPerItemReceipt_IsTypedUnknownAndSingleSend(
        string responseMode)
    {
        var entityId = Guid.NewGuid();
        var handler = new RecycleBinRestoreRetryHandler(entityId, responseMode);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() =>
            api.RestoreRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = entityId,
                    Kind = "customer",
                    ExpectedRevision = 7
                },
                new RecycleBinMutationTargetDto
                {
                    EntityId = handler.SecondEntityId,
                    Kind = "item",
                    ExpectedRevision = 11
                }
            ]));

        Assert.Equal(1, handler.RestoreRequestCount);
    }

    [Fact]
    public async Task ErpApiClient_RestoreRecycleBin_Http200ExactPerItemReceipts_RemainDefinitiveSuccess()
    {
        var entityId = Guid.NewGuid();
        var handler = new RecycleBinRestoreRetryHandler(entityId, "valid-two-successes");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var result = await api.RestoreRecycleBinAsync(
        [
            new RecycleBinMutationTargetDto
            {
                EntityId = entityId,
                Kind = "customer",
                ExpectedRevision = 7
            },
            new RecycleBinMutationTargetDto
            {
                EntityId = handler.SecondEntityId,
                Kind = "item",
                ExpectedRevision = 11
            }
        ]);

        Assert.NotNull(result);
        Assert.Equal(2, result!.SucceededCount);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(1, handler.RestoreRequestCount);
    }

    [Theory]
    [InlineData("restore-denied")]
    [InlineData("restore-throws")]
    [InlineData("mark-throws")]
    public async Task ConfirmedServerRestore_LocalApplyFailure_RefreshesExactlyOnceAndIsNotCountedAsSuccess(
        string failureStage)
    {
        var entry = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(),
            Kind = RecycleBinEntityKind.Customer,
            Title = "confirmed-server-restore"
        };
        var restoreCount = 0;
        var markCount = 0;
        var refreshCount = 0;
        var reloadCount = 0;
        var authoritativeEntryVisible = false;

        var result = await EnvironmentSettingsViewModel.ApplyConfirmedServerRestoresLocallyAsync(
            [entry],
            serverRequiresAuthoritativeRefresh: false,
            restoreAsync: _ =>
            {
                restoreCount++;
                return failureStage switch
                {
                    "restore-denied" => Task.FromResult(OfficeMutationResult.Denied("local denied")),
                    "restore-throws" => Task.FromException<OfficeMutationResult>(new InvalidOperationException("local restore failed")),
                    _ => Task.FromResult(OfficeMutationResult.Ok(entry.EntityId))
                };
            },
            markCleanAsync: _ =>
            {
                markCount++;
                return failureStage == "mark-throws"
                    ? Task.FromException(new InvalidOperationException("local clean failed"))
                    : Task.CompletedTask;
            },
            authoritativeRefreshAsync: () =>
            {
                refreshCount++;
                return Task.FromResult(true);
            },
            reloadAsync: () =>
            {
                reloadCount++;
                authoritativeEntryVisible = true;
                return Task.CompletedTask;
            });

        Assert.Equal(1, restoreCount);
        Assert.Equal(failureStage == "mark-throws" ? 1 : 0, markCount);
        Assert.Equal(1, refreshCount);
        Assert.Equal(1, reloadCount);
        Assert.True(authoritativeEntryVisible);
        Assert.Equal(0, result.SucceededCount);
        Assert.True(result.HasLocalApplyFailure);
        Assert.True(result.RequiresAuthoritativeRefresh);
        Assert.True(result.AuthoritativeRefreshSucceeded);
        Assert.Contains(result.Failures, message =>
            message.Contains("서버 복원", StringComparison.Ordinal) &&
            message.Contains("로컬", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmedServerRestore_LocalApplySuccess_CleansBeforeCountingAndDoesNotRefresh()
    {
        var entry = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(),
            Kind = RecycleBinEntityKind.Customer,
            Title = "confirmed-server-restore"
        };
        var marked = false;
        var refreshCount = 0;
        var reloadCount = 0;

        var result = await EnvironmentSettingsViewModel.ApplyConfirmedServerRestoresLocallyAsync(
            [entry],
            serverRequiresAuthoritativeRefresh: false,
            restoreAsync: _ => Task.FromResult(OfficeMutationResult.Ok(entry.EntityId)),
            markCleanAsync: _ =>
            {
                marked = true;
                return Task.CompletedTask;
            },
            authoritativeRefreshAsync: () =>
            {
                refreshCount++;
                return Task.FromResult(true);
            },
            reloadAsync: () =>
            {
                reloadCount++;
                return Task.CompletedTask;
            });

        Assert.True(marked);
        Assert.Equal(1, result.SucceededCount);
        Assert.False(result.HasLocalApplyFailure);
        Assert.False(result.RequiresAuthoritativeRefresh);
        Assert.Equal(0, refreshCount);
        Assert.Equal(1, reloadCount);
    }

    [Fact]
    public async Task UnknownRestoreReceipt_DoesNotApplyLocallyAndRefreshesThenReloadsExactlyOnce()
    {
        var restoreCount = 0;
        var markCount = 0;
        var refreshCount = 0;
        var reloadCount = 0;

        var result = await EnvironmentSettingsViewModel.ApplyConfirmedServerRestoresLocallyAsync(
            [],
            serverRequiresAuthoritativeRefresh: true,
            restoreAsync: _ =>
            {
                restoreCount++;
                return Task.FromResult(OfficeMutationResult.Ok());
            },
            markCleanAsync: _ =>
            {
                markCount++;
                return Task.CompletedTask;
            },
            authoritativeRefreshAsync: () =>
            {
                refreshCount++;
                return Task.FromResult(true);
            },
            reloadAsync: () =>
            {
                reloadCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, restoreCount);
        Assert.Equal(0, markCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, refreshCount);
        Assert.Equal(1, reloadCount);
        Assert.True(result.RequiresAuthoritativeRefresh);
        Assert.True(result.AuthoritativeRefreshSucceeded);
    }

    [Fact]
    public void EnvironmentSettingsRestore_SourceGuard_AppliesLocallyOnlyAfterServerSuccessAndRefreshesUnknownOutcome()
    {
        var repositoryRoot = FindRepositoryRoot();
        var apiSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "ErpApiClient.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.RecycleBin.cs"));

        var restoreOverrideParameter = apiSource.IndexOf(
            "string? businessDatabaseNameOverride",
            apiSource.IndexOf("RestoreRecycleBinAsync", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var restoreApiStart = apiSource.LastIndexOf(
            "public async Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(",
            restoreOverrideParameter,
            StringComparison.Ordinal);
        Assert.True(restoreApiStart >= 0);
        var restoreApiEnd = apiSource.IndexOf("public async Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync", restoreApiStart, StringComparison.Ordinal);
        Assert.True(restoreApiEnd > restoreApiStart);
        Assert.Contains(
            "ExecuteNonIdempotentSingleDispatchAsync",
            apiSource[restoreApiStart..restoreApiEnd],
            StringComparison.Ordinal);

        var restoreWorkflowStart = viewModelSource.IndexOf(
            "private async Task RestoreRecycleBinEntriesCoreAsync",
            StringComparison.Ordinal);
        var restoreWorkflowEnd = viewModelSource.IndexOf(
            "private async Task PermanentlyDeleteRecycleBinEntriesCoreAsync",
            restoreWorkflowStart,
            StringComparison.Ordinal);
        Assert.True(restoreWorkflowStart >= 0 && restoreWorkflowEnd > restoreWorkflowStart);
        var restoreWorkflow = viewModelSource[restoreWorkflowStart..restoreWorkflowEnd];
        Assert.Contains("serverMirror.SucceededEntries", restoreWorkflow, StringComparison.Ordinal);
        Assert.Contains("() => _sync.TryAuthoritativePullOnlyAsync()", restoreWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("() => _sync.TrySyncAsync()", restoreWorkflow, StringComparison.Ordinal);
        Assert.Contains("같은 복원을 반복하지 마세요", restoreWorkflow, StringComparison.Ordinal);
        Assert.Contains("AmbiguousMutationOutcomeException", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("localApply.HasLocalApplyFailure", restoreWorkflow, StringComparison.Ordinal);
        Assert.Contains("ReloadRecycleBinAsync", restoreWorkflow, StringComparison.Ordinal);
        Assert.Contains("서버 복원은 확정됐지만 로컬 반영에 실패했습니다. 같은 복원을 반복하지 마세요.", restoreWorkflow, StringComparison.Ordinal);
        Assert.True(
            restoreWorkflow.IndexOf("serverMirror.SucceededEntries", StringComparison.Ordinal) <
            restoreWorkflow.IndexOf("RestoreRecycleBinEntryAsync", StringComparison.Ordinal));
        Assert.True(
            restoreWorkflow.IndexOf("if (!result.Success)", StringComparison.Ordinal) <
            restoreWorkflow.IndexOf("await markCleanAsync(entry)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalStateService_GetCustomersAsync_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-fallback-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var customers = await service.GetCustomersAsync(session);

            Assert.Contains(customers, customer => customer.Id == customerId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetDirtyCustomerContractsForSyncAsync_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-contract-fallback-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = customerId,
                ContractType = "Fallback contract",
                FileName = "fallback-contract.pdf",
                FileSize = 12,
                FileHash = "fallback-hash",
                FileContent = [1, 2, 3],
                IsDirty = true,
                IsDeleted = false,
                Revision = 3
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                AppPermissionNames.CustomerEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);

            Assert.Contains(dirtyContracts, contract => contract.Id == contractId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-fallback-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = string.Empty,
                InvoiceNumber = "FB-RESTORE-001",
                LocalTempNumber = "L202606-0001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 22),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = true,
                IsDirty = false,
                Revision = 7
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Invoice, invoiceId, session);

            Assert.True(restore.Success, restore.Message);
            Assert.False(await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.IsDeleted)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_LinkedTransactionMismatch_RollsBackWholeCascadeAndPublishesNoReceipt()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-restore-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var modifiedUnitId = Guid.NewGuid();
            var deletedUnitId = Guid.NewGuid();
            var addedUnitId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 8, 9),
                Amount = 1000m,
                IsDeleted = true,
                IsDirty = false,
                Revision = 8
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = paymentId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 8, 9),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                LinkedInvoiceId = Guid.NewGuid(),
                SettlementAmount = 1000m,
                ReceiptTotal = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 9
            });
            db.Units.AddRange(
                new LocalUnit
                {
                    Id = modifiedUnitId,
                    Name = "modified unit before draft",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 10
                },
                new LocalUnit
                {
                    Id = deletedUnitId,
                    Name = "deleted unit draft",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 11
                });
            await db.SaveChangesAsync();

            var modifiedUnit = await db.Units.SingleAsync(current => current.Id == modifiedUnitId);
            modifiedUnit.Name = "modified unit pending value";
            modifiedUnit.IsDirty = true;
            var deletedUnit = await db.Units.SingleAsync(current => current.Id == deletedUnitId);
            db.Units.Remove(deletedUnit);
            var addedUnit = new LocalUnit
            {
                Id = addedUnitId,
                Name = "added unit pending value",
                IsActive = true,
                IsDirty = true,
                Revision = 0
            };
            db.Units.Add(addedUnit);

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId,
                session);

            Assert.False(result.Success);
            Assert.Contains("일치하지 않아", result.Message, StringComparison.Ordinal);
            Assert.Equal(EntityState.Modified, db.Entry(modifiedUnit).State);
            Assert.Equal("modified unit pending value", modifiedUnit.Name);
            Assert.Equal(EntityState.Deleted, db.Entry(deletedUnit).State);
            Assert.Equal(EntityState.Added, db.Entry(addedUnit).State);
            Assert.Equal("added unit pending value", addedUnit.Name);
            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);
            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var customer = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            var invoice = await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            var payment = await verificationDb.Payments.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);
            var transaction = await verificationDb.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == paymentId);

            Assert.True(customer.IsDeleted);
            Assert.False(customer.IsDirty);
            Assert.True(invoice.IsDeleted);
            Assert.False(invoice.IsDirty);
            Assert.True(payment.IsDeleted);
            Assert.False(payment.IsDirty);
            Assert.False(transaction.IsDeleted);
            Assert.False(transaction.IsDirty);
            Assert.NotEqual(invoiceId, transaction.LinkedInvoiceId);
            Assert.Equal("modified unit pending value", await verificationDb.Units
                .Where(current => current.Id == modifiedUnitId)
                .Select(current => current.Name)
                .SingleAsync());
            Assert.False(await verificationDb.Units
                .AnyAsync(current => current.Id == deletedUnitId));
            Assert.Equal("added unit pending value", await verificationDb.Units
                .Where(current => current.Id == addedUnitId)
                .Select(current => current.Name)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_ExceptionAfterFirstSave_RollsBackAndPublishesNoReceipt()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-restore-exception-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var modifiedUnitId = Guid.NewGuid();
            var deletedUnitId = Guid.NewGuid();
            var addedUnitId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Units.AddRange(
                new LocalUnit
                {
                    Id = modifiedUnitId,
                    Name = "exception modified before draft",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 12
                },
                new LocalUnit
                {
                    Id = deletedUnitId,
                    Name = "exception deleted draft",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 13
                });
            await db.SaveChangesAsync();

            var modifiedUnit = await db.Units.SingleAsync(current => current.Id == modifiedUnitId);
            modifiedUnit.Name = "exception modified pending value";
            modifiedUnit.IsDirty = true;
            var deletedUnit = await db.Units.SingleAsync(current => current.Id == deletedUnitId);
            db.Units.Remove(deletedUnit);
            var addedUnit = new LocalUnit
            {
                Id = addedUnitId,
                Name = "exception added pending value",
                IsActive = true,
                IsDirty = true,
                Revision = 0
            };
            db.Units.Add(addedUnit);

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session)
            {
                AfterInvoiceGroupRestoreSavedAsyncForTesting = _ =>
                    Task.FromException(new InvalidOperationException("after first save"))
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RestoreRecycleBinEntryAsync(
                    RecycleBinEntityKind.Invoice,
                    invoiceId,
                    session));
            Assert.Equal(EntityState.Modified, db.Entry(modifiedUnit).State);
            Assert.Equal("exception modified pending value", modifiedUnit.Name);
            Assert.Equal(EntityState.Deleted, db.Entry(deletedUnit).State);
            Assert.Equal(EntityState.Added, db.Entry(addedUnit).State);
            Assert.Equal("exception added pending value", addedUnit.Name);
            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);
            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            Assert.True(await verificationDb.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.False(await verificationDb.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.True(await verificationDb.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == invoiceId)
                .Select(current => current.IsDeleted)
                .SingleAsync());
            Assert.False(await verificationDb.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == invoiceId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.Equal("exception modified pending value", await verificationDb.Units
                .Where(current => current.Id == modifiedUnitId)
                .Select(current => current.Name)
                .SingleAsync());
            Assert.False(await verificationDb.Units
                .AnyAsync(current => current.Id == deletedUnitId));
            Assert.Equal("exception added pending value", await verificationDb.Units
                .Where(current => current.Id == addedUnitId)
                .Select(current => current.Name)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_DeletedCustomerReceiptIsReloadedFromDatabaseAndBothCleaned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-invoice-customer-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId,
                session);
            Assert.True(result.Success, result.Message);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            await using var verificationDb = new LocalDbContext();
            var customer = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            var invoice = await verificationDb.Invoices.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == invoiceId);
            Assert.False(customer.IsDeleted);
            Assert.False(customer.IsDirty);
            Assert.False(invoice.IsDeleted);
            Assert.False(invoice.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_ConcurrentEditAfterCommitDoesNotCleanConcurrentDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-receipt-commit-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session)
            {
                AfterAtomicRecycleBinRestoreCommitAsyncForTesting = async ct =>
                {
                    await using var concurrentDb = new LocalDbContext();
                    var customer = await concurrentDb.Customers.IgnoreQueryFilters()
                        .SingleAsync(current => current.Id == customerId, ct);
                    customer.NameOriginal = "concurrent saved edit";
                    customer.NameMatchKey = "concurrentsavededit";
                    customer.IsDirty = true;
                    customer.Revision += 1;
                    customer.UpdatedAtUtc = customer.UpdatedAtUtc.AddSeconds(1);
                    await concurrentDb.SaveChangesAsync(ct);
                }
            };

            var result = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId,
                session);
            Assert.True(result.Success, result.Message);
            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            await using var verificationDb = new LocalDbContext();
            var customerAfterClean = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            Assert.Equal("concurrent saved edit", customerAfterClean.NameOriginal);
            Assert.True(customerAfterClean.IsDirty);
            Assert.False(await verificationDb.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == invoiceId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreTransaction_NewLinkedPaymentIsIncludedInReceiptAndCleaned()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-added-payment-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: false));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: false));
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 8, 9),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                LinkedInvoiceId = invoiceId,
                LinkedInvoiceNumber = "linked invoice",
                SettlementAmount = 1000m,
                ReceiptTotal = 1000m,
                IsDeleted = true,
                IsDirty = false,
                Revision = 15
            });
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                transactionId,
                session);
            Assert.True(result.Success, result.Message);
            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Transaction,
                transactionId);

            await using var verificationDb = new LocalDbContext();
            var transaction = await verificationDb.Transactions.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == transactionId);
            var payment = await verificationDb.Payments.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == transactionId);
            Assert.False(transaction.IsDeleted);
            Assert.False(transaction.IsDirty);
            Assert.False(payment.IsDeleted);
            Assert.False(payment.IsDirty);
            Assert.Equal(invoiceId, payment.InvoiceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_MarkRestoreClean_PreservesUnrelatedPendingStatesWithoutSavingThem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-clean-pending-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var modifiedUnitId = Guid.NewGuid();
            var deletedUnitId = Guid.NewGuid();
            var addedUnitId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Invoices.Add(CreateInvoice(
                invoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            db.Units.AddRange(
                new LocalUnit
                {
                    Id = modifiedUnitId,
                    Name = "persisted modified source",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 21
                },
                new LocalUnit
                {
                    Id = deletedUnitId,
                    Name = "persisted delete source",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 22
                });
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var restore = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId,
                session);
            Assert.True(restore.Success, restore.Message);

            var modifiedUnit = await db.Units.SingleAsync(current => current.Id == modifiedUnitId);
            modifiedUnit.Name = "pending modified value";
            modifiedUnit.IsDirty = true;
            var deletedUnit = await db.Units.SingleAsync(current => current.Id == deletedUnitId);
            db.Units.Remove(deletedUnit);
            var addedUnit = new LocalUnit
            {
                Id = addedUnitId,
                Name = "pending added value",
                IsActive = true,
                IsDirty = true
            };
            db.Units.Add(addedUnit);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Invoice,
                invoiceId);

            Assert.Equal(EntityState.Modified, db.Entry(modifiedUnit).State);
            Assert.Equal("pending modified value", modifiedUnit.Name);
            Assert.Equal(EntityState.Deleted, db.Entry(deletedUnit).State);
            Assert.Equal(EntityState.Added, db.Entry(addedUnit).State);
            Assert.Equal("pending added value", addedUnit.Name);

            await using var verificationDb = new LocalDbContext();
            Assert.Equal("persisted modified source", await verificationDb.Units
                .Where(current => current.Id == modifiedUnitId)
                .Select(current => current.Name)
                .SingleAsync());
            Assert.True(await verificationDb.Units
                .AnyAsync(current => current.Id == deletedUnitId));
            Assert.False(await verificationDb.Units
                .AnyAsync(current => current.Id == addedUnitId));
            Assert.False(await verificationDb.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await verificationDb.Invoices.IgnoreQueryFilters()
                .Where(current => current.Id == invoiceId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_AppliesServerStateWithoutPushAndPreservesUnrelatedDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var customer = CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true);
            customer.NameOriginal = "local tombstone";
            customer.NameMatchKey = "localtombstone";
            db.Customers.Add(customer);
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "unrelated local draft",
                IsActive = true,
                IsDeleted = false,
                IsDirty = true,
                Revision = 4
            });
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            db.ChangeTracker.Clear();
            var handler = new PullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Customers =
                [
                    new CustomerDto
                    {
                        Id = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Shared,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "authoritative restored customer",
                        NameMatchKey = "authoritativerestoredcustomer",
                        TradeType = CustomerClassificationNormalizer.Sales,
                        IsDeleted = false,
                        Revision = 9,
                        CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            });
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/")
            };
            await using var syncLease = new SyncServiceTestLease(new SyncService(
                db,
                local,
                new RentalStateService(db, local),
                new ErpApiClient(http, session),
                session,
                dispatcher,
                new SyncDiagnosticsService(session)));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();

            var refreshed = await sync.TryAuthoritativePullOnlyAsync();

            Assert.True(refreshed);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal(1, handler.PullCount);
            Assert.Equal("9", await local.GetSettingAsync("LastSyncRevision"));
            await using var verificationDb = new LocalDbContext();
            var restored = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            var unrelatedDirty = await verificationDb.Units.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == unitId);
            Assert.False(restored.IsDeleted);
            Assert.False(restored.IsDirty);
            Assert.Equal("authoritative restored customer", restored.NameOriginal);
            Assert.Equal(9, restored.Revision);
            Assert.True(unrelatedDirty.IsDirty);
            Assert.Equal("unrelated local draft", unrelatedDirty.Name);
            Assert.Equal(4, unrelatedDirty.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_DirtyPulledTargetFailsWithoutAdvancingCursorOrPush()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-dirty-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            var customer = CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true);
            customer.NameOriginal = "dirty local tombstone";
            customer.NameMatchKey = "dirtylocaltombstone";
            customer.IsDirty = true;
            customer.Revision = 4;
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            db.ChangeTracker.Clear();
            var handler = new PullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Customers =
                [
                    new CustomerDto
                    {
                        Id = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Shared,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "server active customer",
                        NameMatchKey = "serveractivecustomer",
                        TradeType = CustomerClassificationNormalizer.Sales,
                        IsDeleted = false,
                        Revision = 9,
                        CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var syncLease = new SyncServiceTestLease(
                CreateRecycleBinSyncService(db, local, session, dispatcher, http));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();
            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry =>
                entry.Entity is not LocalSyncOutboxEntry &&
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

            var refreshed = await sync.TryAuthoritativePullOnlyAsync();

            Assert.False(refreshed);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal(1, handler.PullCount);
            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            await using var verificationDb = new LocalDbContext();
            var unchanged = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            Assert.True(unchanged.IsDeleted);
            Assert.True(unchanged.IsDirty);
            Assert.Equal(4, unchanged.Revision);
            Assert.Equal("dirty local tombstone", unchanged.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_OwnerChangesDuringPullReturnsFalseAndKeepsCursor()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-owner-change-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            db.ChangeTracker.Clear();
            var handler = new DelayedPullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var syncLease = new SyncServiceTestLease(
                CreateRecycleBinSyncService(db, local, session, dispatcher, http));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();

            var refreshTask = sync.TryAuthoritativePullOnlyAsync();
            try
            {
                await handler.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
                handler.ReleasePull.TrySetResult();
                var refreshed = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(refreshed);
                Assert.Equal(0, handler.PushCount);
                Assert.Equal(1, handler.PullCount);
                Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            }
            finally
            {
                handler.ReleasePull.TrySetResult();
                await sync.StopAndDrainAsync();
                await ObserveTestTaskCompletionAsync(refreshTask);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_TargetBecomesDirtyDuringHttpFailsInsideMutationGate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-http-toctou-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            var handler = new DelayedPullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Customers =
                [
                    new CustomerDto
                    {
                        Id = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Shared,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "server active during race",
                        NameMatchKey = "serveractiveduringrace",
                        TradeType = CustomerClassificationNormalizer.Sales,
                        IsDeleted = false,
                        Revision = 9,
                        CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var syncLease = new SyncServiceTestLease(
                CreateRecycleBinSyncService(db, local, session, dispatcher, http));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();

            var refreshTask = sync.TryAuthoritativePullOnlyAsync();
            try
            {
                await handler.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await using (var concurrentDb = new LocalDbContext())
                {
                    var concurrentCustomer = await concurrentDb.Customers.IgnoreQueryFilters()
                        .SingleAsync(current => current.Id == customerId);
                    concurrentCustomer.NameOriginal = "dirty edit during http";
                    concurrentCustomer.NameMatchKey = "dirtyeditduringhttp";
                    concurrentCustomer.IsDirty = true;
                    concurrentCustomer.Revision = 4;
                    concurrentCustomer.UpdatedAtUtc = DateTime.UtcNow;
                    await concurrentDb.SaveChangesAsync();
                }
                handler.ReleasePull.TrySetResult();
                var refreshed = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(refreshed);
                Assert.Equal(0, handler.PushCount);
                Assert.Equal(1, handler.PullCount);
                Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
                await using var verificationDb = new LocalDbContext();
                var preserved = await verificationDb.Customers.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(current => current.Id == customerId);
                Assert.True(preserved.IsDeleted);
                Assert.True(preserved.IsDirty);
                Assert.Equal("dirty edit during http", preserved.NameOriginal);
                Assert.Equal(4, preserved.Revision);
            }
            finally
            {
                handler.ReleasePull.TrySetResult();
                await sync.StopAndDrainAsync();
                await ObserveTestTaskCompletionAsync(refreshTask);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_IsolatedScopeParentUnsavedChangesFailBeforeAnyHttp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-parent-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            var session = CreateOnlineAdminSession();
            var handler = new PullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9
            });
            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var local = scope.ServiceProvider.GetRequiredService<LocalStateService>();
            await local.SetSettingAsync("LastSyncRevision", "5");
            var modifiedId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.Units.AddRange(
                new LocalUnit
                {
                    Id = modifiedId,
                    Name = "persisted modified parent",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 1
                },
                new LocalUnit
                {
                    Id = deletedId,
                    Name = "persisted deleted parent",
                    IsActive = true,
                    IsDirty = false,
                    Revision = 2
                });
            await db.SaveChangesAsync();
            var modified = await db.Units.SingleAsync(current => current.Id == modifiedId);
            modified.Name = "unsaved modified parent";
            modified.IsDirty = true;
            var deleted = await db.Units.SingleAsync(current => current.Id == deletedId);
            db.Units.Remove(deleted);
            var added = new LocalUnit
            {
                Id = Guid.NewGuid(),
                Name = "unsaved added parent",
                IsActive = true,
                IsDirty = true
            };
            db.Units.Add(added);

            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            var refreshed = await sync.TryAuthoritativePullOnlyAsync();

            Assert.False(refreshed);
            Assert.Equal(0, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            Assert.Equal(EntityState.Modified, db.Entry(modified).State);
            Assert.Equal("unsaved modified parent", modified.Name);
            Assert.Equal(EntityState.Deleted, db.Entry(deleted).State);
            Assert.Equal("persisted deleted parent", deleted.Name);
            Assert.Equal(EntityState.Added, db.Entry(added).State);
            Assert.Equal("unsaved added parent", added.Name);

            await using var verificationDb = new LocalDbContext();
            Assert.Equal("persisted modified parent", await verificationDb.Units
                .Where(current => current.Id == modifiedId)
                .Select(current => current.Name)
                .SingleAsync());
            Assert.True(await verificationDb.Units
                .AnyAsync(current => current.Id == deletedId));
            Assert.False(await verificationDb.Units
                .AnyAsync(current => current.Id == added.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_AuthoritativePullOnly_ParentDraftArrivingAfterApplyRollsBackPullAndPreservesTracker()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-parent-apply-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            var session = CreateOnlineAdminSession();
            var incomingCustomerId = Guid.NewGuid();
            var handler = new PullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Customers =
                [
                    new CustomerDto
                    {
                        Id = incomingCustomerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Shared,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "must roll back after parent draft",
                        NameMatchKey = "mustrollbackafterparentdraft",
                        TradeType = CustomerClassificationNormalizer.Sales,
                        Revision = 9,
                        CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            });
            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var local = scope.ServiceProvider.GetRequiredService<LocalStateService>();
            await local.SetSettingAsync("LastSyncRevision", "5");
            var modifiedId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.Units.AddRange(
                new LocalUnit { Id = modifiedId, Name = "persisted modified", IsActive = true },
                new LocalUnit { Id = deletedId, Name = "persisted deleted", IsActive = true });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            LocalUnit? modified = null;
            LocalUnit? deleted = null;
            LocalUnit? added = null;
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            sync.AfterPulledPurgeRecordsAsyncForTesting = async _ =>
            {
                modified = await db.Units.SingleAsync(unit => unit.Id == modifiedId);
                modified.Name = "unsaved modified during pull";
                modified.IsDirty = true;
                deleted = await db.Units.SingleAsync(unit => unit.Id == deletedId);
                db.Units.Remove(deleted);
                added = new LocalUnit
                {
                    Id = Guid.NewGuid(),
                    Name = "unsaved added during pull",
                    IsActive = true,
                    IsDirty = true
                };
                db.Units.Add(added);
            };

            Assert.False(await sync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            Assert.NotNull(modified);
            Assert.NotNull(deleted);
            Assert.NotNull(added);
            Assert.Equal(EntityState.Modified, db.Entry(modified!).State);
            Assert.Equal("unsaved modified during pull", modified!.Name);
            Assert.Equal(EntityState.Deleted, db.Entry(deleted!).State);
            Assert.Equal(EntityState.Added, db.Entry(added!).State);

            await using var verificationDb = new LocalDbContext();
            Assert.False(await verificationDb.Customers.IgnoreQueryFilters()
                .AnyAsync(customer => customer.Id == incomingCustomerId));
            Assert.Equal("persisted modified", await verificationDb.Units
                .Where(unit => unit.Id == modifiedId)
                .Select(unit => unit.Name)
                .SingleAsync());
            Assert.True(await verificationDb.Units.AnyAsync(unit => unit.Id == deletedId));
            Assert.False(await verificationDb.Units.AnyAsync(unit => unit.Id == added!.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("during-http")]
    [InlineData("after-apply-hook")]
    [InlineData("before-commit-guard")]
    public async Task SyncService_AuthoritativePullOnly_MirrorFlagArrivingAfterPreflightRollsBackAndKeepsCursor(
        string timing)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-mirror-race-{timing}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            db.ChangeTracker.Clear();
            var incomingUnitId = Guid.NewGuid();
            var response = new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Units =
                [
                    new UnitDto
                    {
                        Id = incomingUnitId,
                        Name = "STRICT MIRROR RACE MUST ROLL BACK",
                        IsActive = true,
                        Revision = 9,
                        CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            };

            if (timing == "during-http")
            {
                var handler = new DelayedPullOnlyRecoveryHandler(response);
                using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
                await using var syncLease = new SyncServiceTestLease(
                    CreateRecycleBinSyncService(db, local, session, dispatcher, http));
                var sync = syncLease.Service;
                var refreshTask = sync.TryAuthoritativePullOnlyAsync();
                try
                {
                    await handler.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await using (var flagDb = new LocalDbContext())
                    {
                        var flagLocal = new LocalStateService(
                            flagDb,
                            new OfficeAccessService(),
                            new SyncRequestDispatcher(),
                            session);
                        await flagLocal.MarkServerMirrorRefreshRequiredAsync();
                    }
                    handler.ReleasePull.TrySetResult();
                    Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(1, handler.PullCount);
                    Assert.Equal(0, handler.PushCount);
                }
                finally
                {
                    handler.ReleasePull.TrySetResult();
                    await sync.StopAndDrainAsync();
                    await ObserveTestTaskCompletionAsync(refreshTask);
                }
            }
            else
            {
                var handler = new PullOnlyRecoveryHandler(response);
                using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
                await using var syncLease = new SyncServiceTestLease(
                    CreateRecycleBinSyncService(db, local, session, dispatcher, http));
                var sync = syncLease.Service;
                LocalDbContext? flagDb = null;
                Task? markFlagTask = null;
                Task MarkMirrorRefreshRequiredAsync(CancellationToken _)
                {
                    flagDb = new LocalDbContext();
                    var flagLocal = new LocalStateService(
                        flagDb,
                        new OfficeAccessService(),
                        new SyncRequestDispatcher(),
                        session);
                    var requestStarted = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ExecutionContext.SuppressFlow())
                    {
                        markFlagTask = Task.Run(async () =>
                        {
                            var request = flagLocal.MarkServerMirrorRefreshRequiredAsync();
                            requestStarted.TrySetResult(true);
                            await request;
                        });
                    }
                    return requestStarted.Task;
                }
                try
                {
                    if (timing == "after-apply-hook")
                        sync.AfterPulledPurgeRecordsAsyncForTesting = MarkMirrorRefreshRequiredAsync;
                    else
                        sync.BeforeStrictPullCommitGuardAsyncForTesting = MarkMirrorRefreshRequiredAsync;
                    Assert.False(await sync.TryAuthoritativePullOnlyAsync());
                    Assert.NotNull(markFlagTask);
                    await markFlagTask!.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(1, handler.PullCount);
                    Assert.Equal(0, handler.PushCount);
                }
                finally
                {
                    await sync.StopAndDrainAsync();
                    if (markFlagTask is not null)
                        await ObserveTestTaskCompletionAsync(markFlagTask);
                    if (flagDb is not null)
                        await flagDb.DisposeAsync();
                }
            }

            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
            await using var verificationDb = new LocalDbContext();
            var verificationLocal = new LocalStateService(
                verificationDb,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            Assert.True(await verificationLocal.IsServerMirrorRefreshRequiredAsync());
            Assert.False(await verificationDb.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == incomingUnitId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true, "5")]
    [InlineData(false, "0")]
    public async Task SyncService_AuthoritativePullOnly_FullRefreshFallbackFailsClosedBeforeHttp(
        bool requireMirrorRefresh,
        string initialCursor)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-full-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var itemId = Guid.NewGuid();
            var optionId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "price grade dirty item",
                NameMatchKey = "pricegradedirtyitem",
                SpecificationOriginal = "A",
                SpecificationMatchKey = "a",
                IsDirty = false
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = optionId,
                Name = "strict price grade",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                IsActive = true,
                IsDirty = false
            });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                PriceGradeOptionId = optionId,
                PriceGradeName = "strict price grade",
                UnitPrice = 1000m,
                IsActive = true,
                IsDirty = true,
                Revision = 3
            });
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", initialCursor);
            if (requireMirrorRefresh)
                await local.MarkServerMirrorRefreshRequiredAsync();
            await db.Items.IgnoreQueryFilters()
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, false));
            await db.PriceGradeOptions.IgnoreQueryFilters()
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, false));
            await db.ItemPriceGrades.IgnoreQueryFilters()
                .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.IsDirty, true));
            db.ChangeTracker.Clear();
            Assert.Equal(0, await db.Items.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty));
            Assert.Equal(0, await db.PriceGradeOptions.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty));
            Assert.Equal(1, await db.ItemPriceGrades.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty));
            Assert.Equal(1, await local.CountDirtyAsync());
            var handler = new PullOnlyRecoveryHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9
            });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var syncLease = new SyncServiceTestLease(
                CreateRecycleBinSyncService(db, local, session, dispatcher, http));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();

            var refreshed = await sync.TryAuthoritativePullOnlyAsync();

            Assert.False(refreshed);
            Assert.Equal(0, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal(initialCursor, await local.GetSettingAsync("LastSyncRevision"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("item-alias-reference")]
    [InlineData("rental-settlement-profile")]
    public async Task SyncService_AuthoritativePullOnly_DirtyCascadeRootFailsClosed(
        string collisionRoot)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-pull-only-cascade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var pull = new SyncPullResponse { CurrentServerRevision = 9 };
            if (collisionRoot == "item-alias-reference")
            {
                var itemId = Guid.NewGuid();
                var optionId = Guid.NewGuid();
                db.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "dirty alias root",
                    NameMatchKey = "dirtyaliasroot",
                    SpecificationOriginal = "A",
                    SpecificationMatchKey = "a",
                    IsDirty = false
                });
                db.PriceGradeOptions.Add(new LocalPriceGradeOption
                {
                    Id = optionId,
                    Name = "alias grade",
                    PriceSource = SelectionOptionDefaults.PriceSourceSales,
                    IsActive = true,
                    IsDirty = false
                });
                db.ItemPriceGrades.Add(new LocalItemPriceGrade
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    PriceGradeOptionId = optionId,
                    PriceGradeName = "alias grade",
                    UnitPrice = 1000m,
                    IsDirty = true
                });
                pull.Items.Add(new ItemDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "server alias root",
                    NameMatchKey = "serveraliasroot",
                    SpecificationOriginal = "A",
                    SpecificationMatchKey = "a",
                    Revision = 9
                });
            }
            else
            {
                var profile = CreateDeletedRentalBillingProfile(Guid.NewGuid());
                profile.IsDeleted = false;
                profile.IsDirty = true;
                db.RentalBillingProfiles.Add(profile);
                pull.Transactions.Add(new TransactionDto
                {
                    Id = Guid.NewGuid(),
                    CustomerId = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                    Revision = 9
                });
            }
            await db.SaveChangesAsync();

            var session = CreateOnlineAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            await local.SetSettingAsync("LastSyncRevision", "5");
            var handler = new PullOnlyRecoveryHandler(pull);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await using var syncLease = new SyncServiceTestLease(
                CreateRecycleBinSyncService(db, local, session, dispatcher, http));
            var sync = syncLease.Service;
            db.ChangeTracker.Clear();

            var refreshed = await sync.TryAuthoritativePullOnlyAsync();

            Assert.False(refreshed);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal(1, handler.PullCount);
            Assert.Equal("5", await local.GetSettingAsync("LastSyncRevision"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_UpdatePaymentLedgerMemo_UsesInvoiceOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-memo-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = string.Empty,
                InvoiceNumber = "FB-MEMO-001",
                LocalTempNumber = "L202606-0002",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 22),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                IsDirty = false,
                Revision = 8
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 22),
                Amount = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 9
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.UpdatePaymentLedgerMemoAsync(paymentId, "fallback memo", session);

            Assert.True(result.Success, result.Message);
            var payment = await db.Payments.IgnoreQueryFilters().SingleAsync(current => current.Id == paymentId);
            Assert.Equal("fallback memo", payment.Note);
            Assert.True(payment.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreCustomer_RestoresContractsDeletedWithCustomer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-contract-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var primaryContractId = Guid.NewGuid();
            var secondaryContractId = Guid.NewGuid();
            var oldDeletedContractId = Guid.NewGuid();
            var unrelatedDirtyContractId = Guid.NewGuid();
            var originalUpdatedAt = new DateTime(2026, 6, 25, 1, 0, 0, DateTimeKind.Utc);
            var oldDeletedAt = originalUpdatedAt.AddHours(-2);

            db.Customers.Add(CreateScopedCustomer(customerId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, isDeleted: false));
            db.CustomerContracts.AddRange(
                new LocalCustomerContract
                {
                    Id = primaryContractId,
                    CustomerId = customerId,
                    ContractType = "Primary",
                    FileName = "primary.pdf",
                    IsPrimary = true,
                    IsDeleted = false,
                    IsDirty = false,
                    CreatedAtUtc = originalUpdatedAt,
                    UpdatedAtUtc = originalUpdatedAt,
                    Revision = 20
                },
                new LocalCustomerContract
                {
                    Id = secondaryContractId,
                    CustomerId = customerId,
                    ContractType = "Secondary",
                    FileName = "secondary.pdf",
                    IsPrimary = false,
                    IsDeleted = false,
                    IsDirty = false,
                    CreatedAtUtc = originalUpdatedAt,
                    UpdatedAtUtc = originalUpdatedAt,
                    Revision = 21
                },
                new LocalCustomerContract
                {
                    Id = oldDeletedContractId,
                    CustomerId = customerId,
                    ContractType = "OldDeleted",
                    FileName = "old-deleted.pdf",
                    IsPrimary = false,
                    IsDeleted = true,
                    IsDirty = false,
                    CreatedAtUtc = oldDeletedAt,
                    UpdatedAtUtc = oldDeletedAt,
                    Revision = 22
                });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var delete = await service.DeleteCustomerAsync(customerId, session);

            Assert.True(delete.Success, delete.Message);
            db.ChangeTracker.Clear();

            var deletedCustomer = await db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            var deletedContracts = await db.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(contract => contract.CustomerId == customerId)
                .ToDictionaryAsync(contract => contract.Id);

            Assert.True(deletedCustomer.IsDeleted);
            Assert.True(deletedContracts[primaryContractId].IsDeleted);
            Assert.True(deletedContracts[primaryContractId].IsPrimary);
            Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedContracts[primaryContractId].UpdatedAtUtc);
            Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedContracts[secondaryContractId].UpdatedAtUtc);
            Assert.True(deletedContracts[oldDeletedContractId].IsDeleted);
            Assert.Equal(oldDeletedAt, deletedContracts[oldDeletedContractId].UpdatedAtUtc);

            await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.IsDirty, false));
            await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == primaryContractId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.IsDirty, false));
            await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == secondaryContractId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.IsDirty, true));
            await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == oldDeletedContractId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.IsDirty, true));
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = unrelatedDirtyContractId,
                CustomerId = customerId,
                ContractType = "UnrelatedDirty",
                FileName = "unrelated-dirty.pdf",
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = deletedCustomer.UpdatedAtUtc,
                UpdatedAtUtc = deletedCustomer.UpdatedAtUtc,
                Revision = 23
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var restore = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                session);

            Assert.True(restore.Success, restore.Message);
            db.ChangeTracker.Clear();

            var restoredCustomer = await db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            var restoredContracts = await db.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(contract => contract.CustomerId == customerId)
                .ToDictionaryAsync(contract => contract.Id);

            Assert.False(restoredCustomer.IsDeleted);
            Assert.True(restoredCustomer.IsDirty);
            Assert.False(restoredContracts[primaryContractId].IsDeleted);
            Assert.True(restoredContracts[primaryContractId].IsDirty);
            Assert.True(restoredContracts[primaryContractId].IsPrimary);
            Assert.False(restoredContracts[secondaryContractId].IsDeleted);
            Assert.True(restoredContracts[secondaryContractId].IsDirty);
            Assert.True(restoredContracts[oldDeletedContractId].IsDeleted);
            Assert.True(restoredContracts[oldDeletedContractId].IsDirty);
            Assert.False(restoredContracts[unrelatedDirtyContractId].IsDeleted);
            Assert.True(restoredContracts[unrelatedDirtyContractId].IsDirty);
            Assert.Equal(deletedCustomer.UpdatedAtUtc, restoredContracts[unrelatedDirtyContractId].UpdatedAtUtc);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);
            Assert.Contains(dirtyContracts, contract => contract.Id == primaryContractId);
            Assert.Contains(dirtyContracts, contract => contract.Id == secondaryContractId);
            Assert.Contains(dirtyContracts, contract => contract.Id == oldDeletedContractId);
            Assert.Contains(dirtyContracts, contract => contract.Id == unrelatedDirtyContractId);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Customer,
                customerId);
            db.ChangeTracker.Clear();

            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == primaryContractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.True(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == secondaryContractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.True(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == oldDeletedContractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.True(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == unrelatedDirtyContractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreRentalBillingProfile_RestoresCustomerContractsDeletedWithCustomer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-profile-contract-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var deletedAt = new DateTime(2026, 6, 25, 2, 0, 0, DateTimeKind.Utc);

            db.Customers.Add(CreateDeletedScopedCustomer(customerId, deletedAt));
            db.CustomerContracts.Add(CreateDeletedCustomerContract(contractId, customerId, deletedAt));
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "restore-profile-contract-link",
                CustomerId = customerId,
                CustomerName = "Rental profile restore customer",
                InstallSiteName = "Rental profile site",
                MonthlyAmount = 120000m,
                IsActive = false,
                IsDeleted = true,
                IsDirty = false,
                CreatedAtUtc = deletedAt,
                UpdatedAtUtc = deletedAt,
                Revision = 30
            });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.RentalBillingProfile, profileId, session);

            Assert.True(restore.Success, restore.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(profile => profile.Id == profileId)
                .Select(profile => profile.IsDeleted)
                .SingleAsync());
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(customer => customer.Id == customerId)
                .Select(customer => customer.IsDeleted)
                .SingleAsync());

            var contract = await db.CustomerContracts.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == contractId);
            Assert.False(contract.IsDeleted);
            Assert.True(contract.IsDirty);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);
            Assert.Contains(dirtyContracts, current => current.Id == contractId);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId);
            db.ChangeTracker.Clear();
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(current => current.Id == profileId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == contractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_MarkRestoreClean_WithoutConfirmedRestoreReceipt_PreservesDirty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-restore-clean-no-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var customer = CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: false);
            customer.IsDirty = true;
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Customer,
                customerId);

            Assert.True(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalStateService_MarkRestoreClean_WhenRestoredEntityWasEditedAfterReceipt_PreservesDirty(
        bool saveEditBeforeMark)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-restore-clean-edit-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var customer = CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true);
            customer.IsDirty = false;
            customer.Revision = 71;
            customer.UpdatedAtUtc = new DateTime(2026, 8, 9, 1, 0, 0, DateTimeKind.Utc);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var restore = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                session);
            Assert.True(restore.Success, restore.Message);

            var edited = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == customerId);
            edited.Notes = saveEditBeforeMark ? "saved-after-restore" : "unsaved-after-restore";
            edited.IsDirty = true;
            if (saveEditBeforeMark)
                await db.SaveChangesAsync();
            else
            {
                db.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, db.Entry(edited).State);
            }

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Customer,
                customerId);

            Assert.True(edited.IsDirty);
            if (!saveEditBeforeMark)
                Assert.Equal(EntityState.Modified, db.Entry(edited).State);

            db.ChangeTracker.Clear();
            Assert.True(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_MarkRestoreClean_WhenSeparateContextSavedAfterReceipt_PreservesLatestDirtyContent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-restore-clean-cross-context-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var customer = CreateScopedCustomer(
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isDeleted: true);
            customer.IsDirty = false;
            customer.Revision = 81;
            customer.UpdatedAtUtc = new DateTime(2026, 8, 9, 2, 0, 0, DateTimeKind.Utc);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var restore = await service.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                session);
            Assert.True(restore.Success, restore.Message);

            await using (var editingDb = new LocalDbContext())
            {
                var edited = await editingDb.Customers.IgnoreQueryFilters()
                    .SingleAsync(current => current.Id == customerId);
                edited.Notes = "saved-by-separate-context-after-restore";
                edited.IsDirty = true;
                await editingDb.SaveChangesAsync();
            }

            // Keep the first context's tracked values stale while acknowledging the
            // newer runtime epoch, so Mark must refresh the row under the operation gate.
            db.AcceptCurrentRuntimeMutationEpoch();
            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.Customer,
                customerId);

            db.ChangeTracker.Clear();
            var preserved = await db.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == customerId);
            Assert.True(preserved.IsDirty);
            Assert.Equal("saved-by-separate-context-after-restore", preserved.Notes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreRentalAsset_RestoresCustomerContractsDeletedWithCustomer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-contract-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var deletedAt = new DateTime(2026, 6, 25, 2, 30, 0, DateTimeKind.Utc);

            db.Customers.Add(CreateDeletedScopedCustomer(customerId, deletedAt));
            db.CustomerContracts.Add(CreateDeletedCustomerContract(contractId, customerId, deletedAt));
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = $"restore-asset-contract-link-{assetId:N}",
                ManagementId = $"RA-{assetId:N}"[..12],
                ManagementNumber = $"RA-{assetId:N}"[..12],
                CustomerId = customerId,
                CustomerName = "Rental asset restore customer",
                InstallLocation = "Rental asset site",
                ItemName = "Restore asset item",
                AssetStatus = "설치",
                IsDeleted = true,
                IsDirty = false,
                CreatedAtUtc = deletedAt,
                UpdatedAtUtc = deletedAt,
                Revision = 40
            });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.RentalAsset, assetId, session);

            Assert.True(restore.Success, restore.Message);
            db.ChangeTracker.Clear();

            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.IsDeleted)
                .SingleAsync());
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(customer => customer.Id == customerId)
                .Select(customer => customer.IsDeleted)
                .SingleAsync());

            var contract = await db.CustomerContracts.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == contractId);
            Assert.False(contract.IsDeleted);
            Assert.True(contract.IsDirty);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);
            Assert.Contains(dirtyContracts, current => current.Id == contractId);

            await service.MarkRecycleBinServerMutationCleanAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId);
            db.ChangeTracker.Clear();
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .Where(current => current.Id == assetId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == customerId)
                .Select(current => current.IsDirty)
                .SingleAsync());
            Assert.False(await db.CustomerContracts.IgnoreQueryFilters()
                .Where(current => current.Id == contractId)
                .Select(current => current.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetRecycleBinEntriesAsync_FiltersRentalAssetsByBusinessDatabase()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var usenetAssetId = Guid.NewGuid();
            var itworldAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = usenetAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "USENET|TEST|001",
                    ManagementId = "803",
                    ManagementNumber = "2407-007",
                    ItemName = "IMC2010",
                    CustomerName = "USENET Customer",
                    InstallLocation = "USENET Office",
                    IsDeleted = true,
                    IsDirty = false,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    Revision = 10
                },
                new LocalRentalAsset
                {
                    Id = itworldAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "ITWORLD|TEST|803",
                    ManagementId = "803",
                    ManagementNumber = "2603-803",
                    ItemName = "JT-7270SC",
                    CustomerName = "ITWORLD Customer",
                    InstallLocation = "Seoul HQ",
                    IsDeleted = true,
                    IsDirty = false,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    Revision = 20
                });
            await db.SaveChangesAsync();

            var usenetSession = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var itworldSession = CreateSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);

            var usenetEntries = await service.GetRecycleBinEntriesAsync(usenetSession);
            var itworldEntries = await service.GetRecycleBinEntriesAsync(itworldSession);

            var usenetEntry = Assert.Single(usenetEntries, entry => entry.EntityId == usenetAssetId);
            Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup), usenetEntry.BusinessDatabaseName);
            Assert.DoesNotContain(usenetEntries, entry => entry.EntityId == itworldAssetId);

            var itworldEntry = Assert.Single(itworldEntries, entry => entry.EntityId == itworldAssetId);
            Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), itworldEntry.BusinessDatabaseName);
            Assert.DoesNotContain(itworldEntries, entry => entry.EntityId == usenetAssetId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreActiveOutOfScopeInvoice_IsDeniedBeforeAlreadyActive()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-invoice-restore-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(hiddenCustomerId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(new LocalInvoice
            {
                Id = hiddenInvoiceId,
                CustomerId = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                InvoiceNumber = "HIDDEN-ACTIVE-RESTORE-001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 24),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = hiddenInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                IsDirty = false,
                Revision = 30
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Invoice, hiddenInvoiceId, session);

            Assert.False(restore.Success);
            Assert.Contains("권한", restore.Message);
            Assert.DoesNotContain("이미 활성", restore.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeCustomer_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-customer-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.CustomerEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                hiddenCustomerId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeInvoice_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-invoice-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                hiddenInvoiceId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopePayment_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-payment-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var hiddenPaymentId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Payments.Add(new LocalPayment
            {
                Id = hiddenPaymentId,
                InvoiceId = hiddenInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 31
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                hiddenPaymentId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeTransaction_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-transaction-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenTransactionId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Transactions.Add(new LocalTransaction
            {
                Id = hiddenTransactionId,
                CustomerId = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionDate = new DateOnly(2026, 6, 24),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 32
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                hiddenTransactionId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeRentalAsset_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-rental-asset-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenAssetId = Guid.NewGuid();
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = hiddenAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                AssetKey = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ManagementId = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ManagementNumber = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ItemName = "권한 외 활성 자산",
                AssetStatus = "설치",
                IsDeleted = false,
                IsDirty = false,
                Revision = 33
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                hiddenAssetId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeInventoryTransfer_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-transfer-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenTransferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = hiddenTransferId,
                TransferNumber = "HIDDEN-ACTIVE-TRANSFER-001",
                TransferDate = new DateOnly(2026, 6, 24),
                FromWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                ToWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                TransferStatus = "수령대기",
                IsDeleted = false,
                IsDirty = false,
                Revision = 34
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.DeliveryEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                hiddenTransferId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("출발지", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetRecycleBinEntries_RentalBillingLogUsesLogScopeAndDoesNotLeakHiddenProfile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-log-hidden-profile-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenProfileId = Guid.NewGuid();
            var logId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = hiddenProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                ProfileKey = "LOCAL-HIDDEN-PROFILE-001",
                CustomerName = "숨김 렌탈 거래처",
                InstallSiteName = "숨김 설치처",
                ItemName = "숨김 품목",
                IsDeleted = true,
                IsDirty = false,
                Revision = 40
            });
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = logId,
                BillingProfileId = hiddenProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                Status = "예정",
                BilledAmount = 12000m,
                IsDeleted = true,
                IsDirty = false,
                Revision = 41
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var entries = await service.GetRecycleBinEntriesAsync(session);

            var entry = Assert.Single(entries, current => current.Kind == RecycleBinEntityKind.RentalBillingLog);
            Assert.Equal(logId, entry.EntityId);
            Assert.Equal("청구로그 2026-06", entry.Title);
            Assert.Equal(OfficeCodeCatalog.Usenet, entry.ResponsibleOfficeCode);
            Assert.DoesNotContain("숨김", entry.Title);
            Assert.DoesNotContain("숨김", entry.Subtitle);
            Assert.DoesNotContain("숨김", entry.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreRentalAsset_IgnoresActiveNaturalKeyConflictInOtherBusinessDatabase()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-restore-cross-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var targetAssetId = Guid.NewGuid();
            var hiddenAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = targetAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "USENET|RESTORE-CROSS-DB|001",
                    ManagementId = "RESTORE-CROSS-DB-ID",
                    ManagementNumber = "RESTORE-CROSS-DB-MN",
                    ItemName = "USENET restore target",
                    IsDeleted = true,
                    IsDirty = false,
                    Revision = 31
                },
                new LocalRentalAsset
                {
                    Id = hiddenAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "ITWORLD|RESTORE-CROSS-DB|001",
                    ManagementId = "RESTORE-CROSS-DB-ID",
                    ManagementNumber = "RESTORE-CROSS-DB-MN",
                    ItemName = "ITWORLD active asset",
                    IsDeleted = false,
                    IsDirty = false,
                    Revision = 32
                });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.RentalAsset, targetAssetId, session);

            Assert.True(restore.Success, restore.Message);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == targetAssetId)
                .Select(asset => asset.IsDeleted)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SyncService_GetMatchingIncomingRentalAssetIds_DoesNotMatchAcrossBusinessDatabase()
    {
        var incomingId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var incomingByManagementId = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld)}|803"] = [incomingId]
        };
        var empty = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var candidate = new LocalRentalAsset
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementId = "803"
        };

        var result = InvokePrivateStatic<HashSet<Guid>>(
            typeof(SyncService),
            "GetMatchingIncomingRentalAssetIds",
            candidate,
            empty,
            incomingByManagementId,
            empty);

        Assert.Empty(result);
    }

    [Fact]
    public void SyncService_DeduplicatePulledRentalAssets_KeepsSameManagementIdAcrossBusinessDatabases()
    {
        var now = DateTime.UtcNow;
        var usenetAssetId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var itworldAssetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var incoming = new List<RentalAssetDto>
        {
            new()
            {
                Id = usenetAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementId = "803",
                AssetKey = "USENET|2407-007|IMC2010",
                CreatedAtUtc = now.AddMinutes(-10),
                UpdatedAtUtc = now.AddMinutes(-5),
                Revision = 100
            },
            new()
            {
                Id = itworldAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementId = "803",
                AssetKey = "ITWORLD|2603-803|JT-7270SC",
                CreatedAtUtc = now.AddMinutes(-9),
                UpdatedAtUtc = now.AddMinutes(-4),
                Revision = 101
            }
        };

        var result = InvokePrivateStatic<IReadOnlyList<RentalAssetDto>>(
            typeof(SyncService),
            "DeduplicatePulledRentalAssets",
            incoming);

        Assert.Contains(result, asset => asset.Id == usenetAssetId);
        Assert.Contains(result, asset => asset.Id == itworldAssetId);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task LocalDbContext_RentalAssetNaturalKeyIndexes_AreScopedByTenant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-index-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    AssetKey = "SHARED-ASSET-KEY-001",
                    ManagementId = "803",
                    ManagementNumber = "MN-803",
                    ItemName = "USENET Asset",
                    IsDeleted = false
                },
                new LocalRentalAsset
                {
                    Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "SHARED-ASSET-KEY-001",
                    ManagementId = "803",
                    ManagementNumber = "MN-803",
                    ItemName = "ITWORLD Asset",
                    IsDeleted = false
                });

            await db.SaveChangesAsync();

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET-OTHER-ASSET-KEY",
                ManagementId = "803",
                ManagementNumber = "USENET-OTHER-MN",
                ItemName = "Same tenant duplicate",
                IsDeleted = false
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteRentalAsset_RemovesAssignmentHistories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-asset-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "LOCAL-PURGE-HISTORY-001"));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
            Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedRentalAsset_RemovesAssignmentHistories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-asset-server-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "SERVER-PURGE-HISTORY-001"));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId);

            Assert.True(result.Success);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
            Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteRentalBillingProfile_ClearsAssignmentHistoryProfileReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-profile-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateDeletedRentalBillingProfile(profileId));
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "LOCAL-PURGE-PROFILE-HISTORY-001", isDeleted: false, billingProfileId: profileId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false, billingProfileId: profileId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, billingProfileId: profileId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
            Assert.Null(await db.RentalAssets.IgnoreQueryFilters()
                .Where(current => current.Id == assetId)
                .Select(current => current.BillingProfileId)
                .SingleAsync());
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.BillingProfileId == profileId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedRentalBillingProfile_ClearsAssignmentHistoryProfileReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-profile-server-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateDeletedRentalBillingProfile(profileId));
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "SERVER-PURGE-PROFILE-HISTORY-001", isDeleted: false, billingProfileId: profileId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false, billingProfileId: profileId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, billingProfileId: profileId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId);

            Assert.True(result.Success);
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
            Assert.Null(await db.RentalAssets.IgnoreQueryFilters()
                .Where(current => current.Id == assetId)
                .Select(current => current.BillingProfileId)
                .SingleAsync());
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.BillingProfileId == profileId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteCustomer_ClearsAssignmentHistoryCustomerReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-customer-purge-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.Customers.Add(CreateDeletedCustomer(customerId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: false, customerId: customerId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, customerId: customerId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.CustomerId == customerId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId && current.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedCustomer_ClearsAssignmentHistoryCustomerReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-customer-server-purge-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.Customers.Add(CreateDeletedCustomer(customerId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: false, customerId: customerId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, customerId: customerId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId);

            Assert.True(result.Success);
            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.CustomerId == customerId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId && current.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateOfficeUserSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "office-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalCustomer CreateFallbackOfficeCustomer(Guid customerId, string tenantCode, string officeCode)
        => new()
        {
            Id = customerId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = $"Fallback customer {customerId:N}",
            NameMatchKey = $"fallbackcustomer{customerId:N}",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = false,
            IsDirty = false,
            Revision = 5
        };

    private static LocalCustomer CreateScopedCustomer(Guid customerId, string tenantCode, string officeCode, bool isDeleted)
        => new()
        {
            Id = customerId,
            TenantCode = tenantCode,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = $"Scoped customer {customerId:N}",
            NameMatchKey = $"scopedcustomer{customerId:N}",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 6
        };

    private static LocalCustomer CreateDeletedScopedCustomer(Guid customerId, DateTime deletedAt)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"Deleted scoped customer {customerId:N}",
            NameMatchKey = $"deletedscopedcustomer{customerId:N}",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true,
            IsDirty = false,
            CreatedAtUtc = deletedAt,
            UpdatedAtUtc = deletedAt,
            Revision = 18
        };

    private static LocalCustomerContract CreateDeletedCustomerContract(Guid contractId, Guid customerId, DateTime deletedAt)
        => new()
        {
            Id = contractId,
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = $"deleted-contract-{contractId:N}.pdf",
            IsPrimary = true,
            IsDeleted = true,
            IsDirty = false,
            CreatedAtUtc = deletedAt,
            UpdatedAtUtc = deletedAt,
            Revision = 19
        };

    private static LocalInvoice CreateInvoice(
        Guid invoiceId,
        Guid customerId,
        string tenantCode,
        string responsibleOfficeCode,
        bool isDeleted)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = tenantCode,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = responsibleOfficeCode,
            InvoiceNumber = $"INV-{invoiceId:N}",
            LocalTempNumber = $"L{invoiceId:N}"[..12],
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            IsConfirmed = true,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 7
        };

    private static LocalCustomer CreateDeletedCustomer(Guid customerId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "영구삭제 이력 거래처",
            NameMatchKey = "영구삭제이력거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true,
            IsDirty = false,
            Revision = 14
        };

    private static LocalRentalBillingProfile CreateDeletedRentalBillingProfile(Guid profileId)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-HISTORY-001",
            CustomerName = "영구삭제 이력 청구프로필",
            InstallSiteName = "테스트 설치처",
            IsDeleted = true,
            IsDirty = false,
            IsActive = false,
            Revision = 12
        };

    private static LocalRentalAsset CreateDeletedRentalAsset(
        Guid assetId,
        string key,
        bool isDeleted = true,
        Guid? billingProfileId = null)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = key,
            ManagementId = key,
            ManagementNumber = key,
            ItemName = "영구삭제 이력 자산",
            BillingProfileId = billingProfileId,
            AssetStatus = "설치",
            BillingEligibilityStatus = billingProfileId.HasValue ? "청구가능" : string.Empty,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 10
        };

    private static LocalRentalAssetAssignmentHistory CreateRentalAssetAssignmentHistory(
        Guid assetId,
        bool isCurrent,
        bool isDeleted,
        Guid? billingProfileId = null,
        Guid? customerId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            BillingProfileId = billingProfileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = customerId.HasValue ? "영구삭제 이력 거래처" : string.Empty,
            InstallLocation = customerId.HasValue ? "과거 설치처" : string.Empty,
            BillingProfileDisplay = billingProfileId.HasValue ? "영구삭제 이력 청구프로필" : string.Empty,
            ItemName = "영구삭제 이력 자산",
            ManagementNumber = "HISTORY-001",
            IsCurrent = isCurrent,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 11
        };

    private static SessionState CreateOnlineAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "recycle-bin-test-token",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddHours(1));
        return session;
    }

    private static SyncService CreateRecycleBinSyncService(
        LocalDbContext db,
        LocalStateService local,
        SessionState session,
        SyncRequestDispatcher dispatcher,
        HttpClient http)
        => new(
            db,
            local,
            new RentalStateService(db, local),
            new ErpApiClient(http, session),
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

    private static async Task ObserveTestTaskCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The primary test failure remains authoritative; this cleanup only observes task completion.
        }
    }

    private sealed class SyncServiceTestLease(SyncService service) : IAsyncDisposable
    {
        public SyncService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAndDrainAsync();
            }
            finally
            {
                Service.Dispose();
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 repository root was not found.");
    }

    private sealed class RecycleBinRestoreRetryHandler : HttpMessageHandler
    {
        private readonly Guid _entityId;
        private readonly string _responseMode;

        public RecycleBinRestoreRetryHandler(Guid entityId, string responseMode)
        {
            _entityId = entityId;
            _responseMode = responseMode;
        }

        public int RestoreRequestCount { get; private set; }
        public Guid SecondEntityId { get; } = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/recycle-bin/restore")
            {
                RestoreRequestCount++;
                if (string.Equals(_responseMode, "bad-gateway", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new StringContent("gateway response lost", Encoding.UTF8, "text/plain")
                    });
                }

                if (string.Equals(_responseMode, "http-exception", StringComparison.Ordinal))
                    throw new HttpRequestException("response lost after dispatch");

                if (string.Equals(_responseMode, "timeout", StringComparison.Ordinal))
                    throw new TaskCanceledException("response timeout after dispatch");

                if (string.Equals(_responseMode, "item-conflict", StringComparison.Ordinal))
                {
                    var itemConflictJson = $$"""
                        {
                          "requestedCount": 1,
                          "succeededCount": 0,
                          "messages": ["Expected revision mismatch"],
                          "results": [
                            {
                              "entityId": "{{_entityId:D}}",
                              "kind": "customer",
                              "success": false,
                              "message": "Expected revision mismatch"
                            }
                          ]
                        }
                        """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(itemConflictJson, Encoding.UTF8, "application/json")
                    });
                }

                if (string.Equals(_responseMode, "invalid-json", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{invalid", Encoding.UTF8, "application/json")
                    });
                }

                if (string.Equals(_responseMode, "null-result", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("null", Encoding.UTF8, "application/json")
                    });
                }

                if (_responseMode is
                    "empty-results-full-count" or
                    "duplicate-key" or
                    "missing-key" or
                    "unexpected-key" or
                    "id-mismatch" or
                    "kind-mismatch" or
                    "count-mismatch" or
                    "succeeded-count-mismatch" or
                    "null-results" or
                    "null-messages" or
                    "null-item" or
                    "valid-two-successes")
                {
                    var unexpectedId = Guid.NewGuid();
                    var firstReceipt = $$"""
                        {
                          "entityId": "{{_entityId:D}}",
                          "kind": "customer",
                          "success": true,
                          "message": "restored"
                        }
                        """;
                    var secondReceipt = $$"""
                        {
                          "entityId": "{{SecondEntityId:D}}",
                          "kind": "item",
                          "success": true,
                          "message": "restored"
                        }
                        """;
                    var resultsJson = _responseMode switch
                    {
                        "empty-results-full-count" => "[]",
                        "duplicate-key" => $"[{firstReceipt},{firstReceipt}]",
                        "missing-key" => $"[{firstReceipt}]",
                        "unexpected-key" => $"[{firstReceipt},{{\"entityId\":\"{unexpectedId:D}\",\"kind\":\"item\",\"success\":true,\"message\":\"restored\"}}]",
                        "id-mismatch" => $"[{{\"entityId\":\"{unexpectedId:D}\",\"kind\":\"customer\",\"success\":true,\"message\":\"restored\"}},{secondReceipt}]",
                        "kind-mismatch" => $"[{{\"entityId\":\"{_entityId:D}\",\"kind\":\"item\",\"success\":true,\"message\":\"restored\"}},{secondReceipt}]",
                        "null-results" => "null",
                        "null-item" => $"[null,{secondReceipt}]",
                        _ => $"[{firstReceipt},{secondReceipt}]"
                    };
                    var requestedCount = _responseMode == "count-mismatch" ? 3 : 2;
                    var messagesJson = _responseMode == "null-messages" ? "null" : "[]";
                    var succeededCount = _responseMode switch
                    {
                        "missing-key" => 1,
                        "null-item" => 1,
                        "succeeded-count-mismatch" => 1,
                        _ => 2
                    };
                    var receiptJson = $$"""
                        {
                          "requestedCount": {{requestedCount}},
                          "succeededCount": {{succeededCount}},
                          "messages": {{messagesJson}},
                          "results": {{resultsJson}}
                        }
                        """;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(receiptJson, Encoding.UTF8, "application/json")
                    });
                }

                var conflictJson = $$"""
                    {
                      "entityName": "Customer",
                      "entityId": "{{_entityId:D}}",
                      "expectedRevision": 7,
                      "currentRevision": 8,
                      "reason": "Expected revision mismatch"
                    }
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(conflictJson, Encoding.UTF8, "application/json")
                });
            }

            if (request.RequestUri?.AbsolutePath == "/runtime/edit-sessions/active")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"activeEditors\":[]}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class PullOnlyRecoveryHandler(SyncPullResponse response)
        : HttpMessageHandler
    {
        public int PushCount { get; private set; }
        public int PullCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (string.Equals(path, "/sync/push", StringComparison.OrdinalIgnoreCase))
            {
                PushCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            if (string.Equals(path, "/sync/pull", StringComparison.OrdinalIgnoreCase))
            {
                PullCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DelayedPullOnlyRecoveryHandler(SyncPullResponse response)
        : HttpMessageHandler
    {
        public TaskCompletionSource PullStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePull { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PushCount { get; private set; }
        public int PullCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (string.Equals(path, "/sync/push", StringComparison.OrdinalIgnoreCase))
            {
                PushCount++;
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            if (string.Equals(path, "/sync/pull", StringComparison.OrdinalIgnoreCase))
            {
                PullCount++;
                PullStarted.TrySetResult();
                await ReleasePull.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }
}
