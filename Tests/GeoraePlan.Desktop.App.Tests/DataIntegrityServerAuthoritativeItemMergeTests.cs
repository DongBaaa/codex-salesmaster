using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityServerAuthoritativeItemMergeTests
{
    [Fact]
    public async Task MergeDuplicateItemIssueAsync_UsesServerSnapshotAndCommandThenRefreshesWithoutLocalMergeWrites()
    {
        PrepareAppRoot("server-authoritative-success");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("90111111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("90222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var localComparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            ItemDuplicateMergePreviewRequestDto? capturedPreview = null;
            ItemDuplicateMergeRequestDto? capturedExecute = null;
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
            {
                capturedPreview = request;
                return Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot-v1"));
            };
            service.TestOnlyExecuteItemDuplicateMergeAsync = (request, _) =>
            {
                capturedExecute = request;
                return Task.FromResult<ItemDuplicateMergeResultDto?>(new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = request.CanonicalItemId,
                    TombstonedItemIds = request.CandidateItemIds.Where(id => id != request.CanonicalItemId).ToList(),
                    ServerSnapshotToken = "server-snapshot-v1",
                    MovedInvoiceLineCount = 2,
                    MovedRentalAssetCount = 1
                });
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = async ct =>
            {
                refreshCount++;
                await ApplyAuthoritativeItemMergeRefreshAsync(
                    canonical.Id,
                    [duplicate.Id],
                    ct);
                return true;
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                localComparison.SnapshotToken,
                session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(canonical.Id, result.EntityId);
            Assert.Equal(1, refreshCount);
            Assert.NotNull(capturedPreview);
            Assert.Equal(canonical.Id, capturedPreview!.CanonicalItemId);
            Assert.Equal(issue.RelatedEntityIds.OrderBy(id => id), capturedPreview.CandidateItemIds.OrderBy(id => id));
            Assert.NotNull(capturedExecute);
            Assert.Equal("server-snapshot-v1", capturedExecute!.ExpectedServerSnapshotToken);
            Assert.StartsWith("item-duplicate-merge:", capturedExecute.MutationId, StringComparison.Ordinal);
            Assert.Equal(capturedPreview.CandidateItemIds.OrderBy(id => id), capturedExecute.CandidateItemIds.OrderBy(id => id));

            Assert.Equal(EntityState.Unchanged, db.Entry(canonical).State);
            Assert.Same(canonical, db.Items.Local.Single(item => item.Id == canonical.Id));
            canonical.SimpleMemo = "post-refresh-owner-context-edit";
            Assert.Equal(1, await db.SaveChangesAsync());

            var stored = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(stored[canonical.Id].IsDeleted);
            Assert.True(stored[duplicate.Id].IsDeleted);
            Assert.False(stored[canonical.Id].IsDirty);
            Assert.False(stored[duplicate.Id].IsDirty);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_CurrentScopeDirtyBlocksBeforeServerPreview()
    {
        PrepareAppRoot("server-authoritative-dirty");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("91111111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("91222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var previewCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(3);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
            {
                previewCount++;
                return Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "unused"));
            };
            service.TestOnlyExecuteItemDuplicateMergeAsync = static (_, _) =>
                throw new Xunit.Sdk.XunitException("미동기화 변경이 있으면 서버 실행을 호출하면 안 됩니다.");
            service.TestOnlyRefreshCurrentBusinessScopeAsync = static _ => Task.FromResult(true);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Contains("미동기화 변경 3건", result.Message, StringComparison.Ordinal);
            Assert.Equal(0, previewCount);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task PrepareItemDuplicateReviewAsync_OfflineSessionShowsServerRequirementAndDisablesMerge()
    {
        PrepareAppRoot("server-authoritative-offline-review");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Items.AddRange(
                CreateItem("91611111-1111-1111-1111-111111111111"),
                CreateItem("91622222-2222-2222-2222-222222222222"));
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(CreateAdminUser());
            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);

            var preparation = await service.PrepareItemDuplicateReviewAsync(issue, session);

            Assert.False(preparation.CanMerge);
            Assert.Contains("온라인 서버", preparation.BlockingReasonText, StringComparison.Ordinal);
            Assert.Contains("온라인 로그인과 동기화", preparation.BlockingReasonText, StringComparison.Ordinal);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_DirtyAppearingAfterPreviewBlocksExecute()
    {
        PrepareAppRoot("server-authoritative-post-preview-dirty");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("91711111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("91722222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var dirtyChecks = 0;
            var executeCount = 0;
            service.TestOnlyCountDirtyAsync = (_, _) => Task.FromResult(++dirtyChecks == 1 ? 0 : 2);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = (_, _) =>
            {
                executeCount++;
                return Task.FromResult<ItemDuplicateMergeResultDto?>(null);
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = static _ => Task.FromResult(true);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(2, dirtyChecks);
            Assert.Equal(0, executeCount);
            Assert.Contains("사전검증 중 미동기화 변경 2건", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_ExecuteResponseLossRefreshesAndForbidsBlindReexecute()
    {
        PrepareAppRoot("server-authoritative-commit-unknown");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("91811111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("91822222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var executeCount = 0;
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = (_, _) =>
            {
                executeCount++;
                throw new HttpRequestException("response-lost-after-server-commit");
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = _ =>
            {
                refreshCount++;
                return Task.FromResult(true);
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(1, executeCount);
            Assert.Equal(1, refreshCount);
            Assert.Contains("응답은 확인하지 못했지만", result.Message, StringComparison.Ordinal);
            Assert.Contains("바로 다시 실행하지 말고", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_ServerCommitWithRefreshFailureDoesNotReexecuteAndReportsCommittedState()
    {
        PrepareAppRoot("server-authoritative-refresh-failure");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("92111111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("92222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var executeCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = (request, _) =>
            {
                executeCount++;
                return Task.FromResult<ItemDuplicateMergeResultDto?>(new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = request.CanonicalItemId,
                    TombstonedItemIds = request.CandidateItemIds.Where(id => id != request.CanonicalItemId).ToList(),
                    ServerSnapshotToken = "server-snapshot"
                });
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = static _ => Task.FromResult(false);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(1, executeCount);
            Assert.Contains("서버 병합은 완료됐지만", result.Message, StringComparison.Ordinal);
            Assert.Contains("병합을 다시 실행하지 말고", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Theory]
    [InlineData("missing-canonical")]
    [InlineData("partial-tombstones")]
    [InlineData("remaining-active-duplicate")]
    [InlineData("dirty-canonical")]
    [InlineData("deleted-canonical")]
    [InlineData("dirty-tombstone")]
    public async Task MergeDuplicateItemIssueAsync_RefreshTrueWithoutCompleteLocalTombstoneStateDoesNotReportSuccess(
        string refreshMode)
    {
        PrepareAppRoot($"server-authoritative-incomplete-refresh-{refreshMode}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("92311111-1111-1111-1111-111111111111");
            var duplicateA = CreateItem("92322222-2222-2222-2222-222222222222");
            var duplicateB = CreateItem("92333333-3333-3333-3333-333333333333");
            db.Items.AddRange(canonical, duplicateA, duplicateB);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var executeCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = (request, _) =>
            {
                executeCount++;
                return Task.FromResult<ItemDuplicateMergeResultDto?>(new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = request.CanonicalItemId,
                    TombstonedItemIds = request.CandidateItemIds.Where(id => id != request.CanonicalItemId).ToList(),
                    ServerSnapshotToken = request.ExpectedServerSnapshotToken
                });
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = async ct =>
            {
                await ApplyAuthoritativeItemMergeRefreshAsync(
                    canonical.Id,
                    refreshMode == "partial-tombstones"
                        ? [duplicateA.Id]
                        : refreshMode == "remaining-active-duplicate"
                            ? [duplicateB.Id]
                            : [duplicateA.Id, duplicateB.Id],
                    ct);

                if (refreshMode == "missing-canonical")
                {
                    await using var refreshDb = new LocalDbContext();
                    await refreshDb.Items
                        .IgnoreQueryFilters()
                        .Where(item => item.Id == canonical.Id)
                        .ExecuteDeleteAsync(ct);
                }
                else if (refreshMode == "dirty-canonical")
                {
                    await using var refreshDb = new LocalDbContext();
                    await refreshDb.Items
                        .IgnoreQueryFilters()
                        .Where(item => item.Id == canonical.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(item => item.IsDirty, true),
                            ct);
                }
                else if (refreshMode == "deleted-canonical")
                {
                    await using var refreshDb = new LocalDbContext();
                    await refreshDb.Items
                        .IgnoreQueryFilters()
                        .Where(item => item.Id == canonical.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(item => item.IsDeleted, true),
                            ct);
                }
                else if (refreshMode == "dirty-tombstone")
                {
                    await using var refreshDb = new LocalDbContext();
                    await refreshDb.Items
                        .IgnoreQueryFilters()
                        .Where(item => item.Id == duplicateA.Id)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(item => item.IsDirty, true),
                            ct);
                }

                return true;
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(1, executeCount);
            Assert.Contains("서버 병합은 완료됐지만", result.Message, StringComparison.Ordinal);
            Assert.Contains("병합을 다시 실행하지 말고", result.Message, StringComparison.Ordinal);

            canonical.SimpleMemo = "stale-owner-context-must-not-save";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await db.SaveChangesAsync());
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate-tombstone")]
    public async Task MergeDuplicateItemIssueAsync_StructurallyInvalid2xxIsCommitUnknownAndRefreshes(string responseMode)
    {
        PrepareAppRoot($"server-authoritative-invalid-2xx-{responseMode}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("92511111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("92522222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergeResultDto?>(responseMode == "empty"
                    ? new ItemDuplicateMergeResultDto()
                    : new ItemDuplicateMergeResultDto
                    {
                        CanonicalItemId = request.CanonicalItemId,
                        TombstonedItemIds = [duplicate.Id, duplicate.Id],
                        ServerSnapshotToken = request.ExpectedServerSnapshotToken
                    });
            service.TestOnlyRefreshCurrentBusinessScopeAsync = _ =>
            {
                refreshCount++;
                return Task.FromResult(true);
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(1, refreshCount);
            Assert.Contains("응답은 확인하지 못했지만", result.Message, StringComparison.Ordinal);
            Assert.Contains("다시 실행하지 말고", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_RuntimeMutationGateRejectsAutosaveStartedBeforeServerExecute()
    {
        PrepareAppRoot("server-authoritative-autosave-gate");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("92611111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("92622222-2222-2222-2222-222222222222");
            var customer = new LocalCustomer
            {
                Id = Guid.Parse("92633333-3333-3333-3333-333333333333"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "동시 저장 거래처",
                IsDirty = false
            };
            var invoiceId = Guid.Parse("92644444-4444-4444-4444-444444444444");
            db.Items.AddRange(canonical, duplicate);
            db.Customers.Add(customer);
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "AUTOSAVE-GATE",
                InvoiceDate = new DateOnly(2026, 8, 5),
                Memo = "before",
                IsDirty = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        InvoiceId = invoiceId,
                        ItemId = duplicate.Id,
                        ItemNameOriginal = duplicate.NameOriginal,
                        SpecificationOriginal = duplicate.SpecificationOriginal,
                        Quantity = 0m
                    }
                }
            });
            await db.SaveChangesAsync();

            await using var autosaveDb = new LocalDbContext();
            var staleInvoice = await autosaveDb.Invoices.SingleAsync(invoice => invoice.Id == invoiceId);
            staleInvoice.Memo = "stale-autosave";
            staleInvoice.IsDirty = true;

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOnlineAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var startAutosave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var autosaveAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedAutosave = Task.Run(async () =>
            {
                await startAutosave.Task;
                autosaveAttempted.TrySetResult();
                return await autosaveDb.SaveChangesAsync();
            });
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyPreviewItemDuplicateMergeAsync = (request, _) =>
                Task.FromResult<ItemDuplicateMergePreviewDto?>(CreatePreview(request, "server-snapshot"));
            service.TestOnlyExecuteItemDuplicateMergeAsync = async (request, _) =>
            {
                startAutosave.TrySetResult();
                await autosaveAttempted.Task;
                await Task.Delay(100);
                Assert.False(blockedAutosave.IsCompleted);
                return new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = request.CanonicalItemId,
                    TombstonedItemIds = [duplicate.Id],
                    ServerSnapshotToken = request.ExpectedServerSnapshotToken
                };
            };
            service.TestOnlyRefreshCurrentBusinessScopeAsync = async ct =>
            {
                Assert.False(blockedAutosave.IsCompleted);
                await ApplyAuthoritativeItemMergeRefreshAsync(
                    canonical.Id,
                    [duplicate.Id],
                    ct);
                return true;
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.True(result.Success, result.Message);
            var concurrency = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await blockedAutosave);
            Assert.Contains("다른 화면에서 시작된 저장", concurrency.Message, StringComparison.Ordinal);
            await using var verificationDb = new LocalDbContext();
            Assert.Equal("before", (await verificationDb.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId)).Memo);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task ErpApiClient_ItemDuplicateMergeUsesBusinessScopeHeadersAndExactRoutes()
    {
        var canonicalId = Guid.Parse("93111111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("93222222-2222-2222-2222-222222222222");
        var handler = new ItemMergeHandler(canonicalId, duplicateId);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var session = CreateOnlineAdminSession();
        var api = new ErpApiClient(http, session);

        var preview = await api.PreviewItemDuplicateMergeAsync(new ItemDuplicateMergePreviewRequestDto
        {
            CandidateItemIds = [canonicalId, duplicateId],
            CanonicalItemId = canonicalId
        });
        var result = await api.ExecuteItemDuplicateMergeAsync(new ItemDuplicateMergeRequestDto
        {
            CandidateItemIds = [canonicalId, duplicateId],
            CanonicalItemId = canonicalId,
            ExpectedServerSnapshotToken = preview!.ServerSnapshotToken,
            MutationId = "item-duplicate-merge:route-proof"
        });

        Assert.NotNull(result);
        Assert.Equal(canonicalId, result!.CanonicalItemId);
        Assert.Equal(
            new[] { "/items/duplicate-merge/preview", "/items/duplicate-merge" },
            handler.Requests.Select(request => request.Path));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer proof-token", request.Authorization);
            Assert.False(string.IsNullOrWhiteSpace(request.BusinessDatabase));
        });
        var executeBody = JsonSerializer.Deserialize<ItemDuplicateMergeRequestDto>(
            handler.Requests[1].Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(executeBody);
        Assert.Equal("server-route-token", executeBody!.ExpectedServerSnapshotToken);
        Assert.Equal("item-duplicate-merge:route-proof", executeBody.MutationId);
    }

    [Theory]
    [InlineData("http", HttpStatusCode.Forbidden)]
    [InlineData("timeout", HttpStatusCode.Conflict)]
    [InlineData("http", HttpStatusCode.UpgradeRequired)]
    [InlineData("gateway-timeout", HttpStatusCode.Forbidden)]
    [InlineData("gateway-timeout", HttpStatusCode.Conflict)]
    [InlineData("gateway-timeout", HttpStatusCode.UpgradeRequired)]
    public async Task MergeDuplicateItemIssueAsync_AmbiguousDispatchThenDefinitiveResponseStillRefreshes(
        string ambiguousFailure,
        HttpStatusCode finalStatus)
    {
        PrepareAppRoot($"server-authoritative-ambiguous-{ambiguousFailure}-{(int)finalStatus}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("93311111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("93422222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var handler = new RetryingItemMergeHandler(
                canonical.Id,
                duplicate.Id,
                ambiguousFailure,
                finalStatus);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOnlineAdminSession();
            var api = new ErpApiClient(http, session);
            var service = new DataIntegrityIssueService(
                db,
                new SyncRequestDispatcher(),
                erpApiClient: api);
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyRefreshCurrentBusinessScopeAsync = _ =>
            {
                refreshCount++;
                return Task.FromResult(true);
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Equal(2, handler.ExecuteRequests.Count);
            Assert.Equal(1, refreshCount);
            Assert.Contains("응답은 확인하지 못했지만", result.Message, StringComparison.Ordinal);
            Assert.Contains("바로 다시 실행하지 말고", result.Message, StringComparison.Ordinal);
            AssertSingleExecuteRequestSemantics(handler.ExecuteRequests);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UpgradeRequired)]
    public async Task MergeDuplicateItemIssueAsync_FirstDefinitiveResponseDoesNotRefresh(HttpStatusCode status)
    {
        PrepareAppRoot($"server-authoritative-definitive-{(int)status}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("93511111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("93622222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var handler = new RetryingItemMergeHandler(
                canonical.Id,
                duplicate.Id,
                ambiguousFailure: null,
                status);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOnlineAdminSession();
            var api = new ErpApiClient(http, session);
            var service = new DataIntegrityIssueService(
                db,
                new SyncRequestDispatcher(),
                erpApiClient: api);
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyRefreshCurrentBusinessScopeAsync = _ =>
            {
                refreshCount++;
                return Task.FromResult(true);
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Single(handler.ExecuteRequests);
            Assert.Equal(0, refreshCount);
            AssertSingleExecuteRequestSemantics(handler.ExecuteRequests);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("gateway-timeout")]
    public async Task MergeDuplicateItemIssueAsync_AmbiguousDispatchThenReplaySucceedsWithoutZeroCountClaim(
        string ambiguousFailure)
    {
        PrepareAppRoot($"server-authoritative-replay-{ambiguousFailure}");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var canonical = CreateItem("93711111-1111-1111-1111-111111111111");
            var duplicate = CreateItem("93822222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            var handler = new RetryingItemMergeHandler(
                canonical.Id,
                duplicate.Id,
                ambiguousFailure,
                finalStatus: null);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var session = CreateOnlineAdminSession();
            var api = new ErpApiClient(http, session);
            var service = new DataIntegrityIssueService(
                db,
                new SyncRequestDispatcher(),
                erpApiClient: api);
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var refreshCount = 0;
            service.TestOnlyCountDirtyAsync = static (_, _) => Task.FromResult(0);
            service.TestOnlyRefreshCurrentBusinessScopeAsync = async ct =>
            {
                refreshCount++;
                await ApplyAuthoritativeItemMergeRefreshAsync(canonical.Id, [duplicate.Id], ct);
                return true;
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, handler.ExecuteRequests.Count);
            Assert.Equal(1, refreshCount);
            Assert.Contains("이전에 완료된 동일 요청", result.Message, StringComparison.Ordinal);
            Assert.Contains("이동 건수", result.Message, StringComparison.Ordinal);
            Assert.Contains("새로고침", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("전표 0건", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("렌탈 자산 0건", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("렌탈 청구 0건", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("재고이동 0건", result.Message, StringComparison.Ordinal);
            AssertSingleExecuteRequestSemantics(handler.ExecuteRequests);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task PreviewItemDuplicateMergeAsync_AmbiguousTransportFailureStillRetries()
    {
        var canonicalId = Guid.Parse("93911111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("93922222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure: null,
            finalStatus: null,
            failFirstPreview: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());
        var request = new ItemDuplicateMergePreviewRequestDto
        {
            CandidateItemIds = [canonicalId, duplicateId],
            CanonicalItemId = canonicalId
        };

        var preview = await api.PreviewItemDuplicateMergeAsync(request);

        Assert.NotNull(preview);
        Assert.Equal(2, handler.PreviewRequests.Count);
        Assert.Equal(handler.PreviewRequests[0].Body, handler.PreviewRequests[1].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ExecuteItemDuplicateMergeAsync_ThreeAmbiguousServerFailuresExhaustWithUnknownStatus(
        HttpStatusCode status)
    {
        var canonicalId = Guid.Parse("94011111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("94022222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure: null,
            finalStatus: status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.ExecuteItemDuplicateMergeAsync(CreateExecuteRequest(canonicalId, duplicateId)));

        Assert.Null(exception.StatusCode);
        Assert.Equal(3, handler.ExecuteRequests.Count);
        AssertSingleExecuteRequestSemantics(handler.ExecuteRequests);
    }

    [Theory]
    [InlineData("http-always")]
    [InlineData("timeout-always")]
    public async Task ExecuteItemDuplicateMergeAsync_AmbiguousExceptionExhaustionKeepsUnknownStatus(
        string ambiguousFailure)
    {
        var canonicalId = Guid.Parse("94111111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("94122222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure,
            finalStatus: null);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.ExecuteItemDuplicateMergeAsync(CreateExecuteRequest(canonicalId, duplicateId)));

        Assert.Null(exception.StatusCode);
        Assert.Equal(3, handler.ExecuteRequests.Count);
        AssertSingleExecuteRequestSemantics(handler.ExecuteRequests);
    }

    [Fact]
    public async Task ExecuteItemDuplicateMergeAsync_CallerCancellationRemainsCancellationWithoutRetry()
    {
        var canonicalId = Guid.Parse("94211111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("94222222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure: "caller-cancel",
            finalStatus: null);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.ExecuteItemDuplicateMergeAsync(
                CreateExecuteRequest(canonicalId, duplicateId),
                cancellation.Token));

        Assert.Single(handler.ExecuteRequests);
    }

    [Fact]
    public async Task PreviewItemDuplicateMergeAsync_GatewayThenForbiddenKeepsConcreteFinalStatus()
    {
        var canonicalId = Guid.Parse("94311111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("94322222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure: null,
            finalStatus: null,
            firstPreviewStatus: HttpStatusCode.GatewayTimeout,
            finalPreviewStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.PreviewItemDuplicateMergeAsync(new ItemDuplicateMergePreviewRequestDto
            {
                CandidateItemIds = [canonicalId, duplicateId],
                CanonicalItemId = canonicalId
            }));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(2, handler.PreviewRequests.Count);
    }

    [Theory]
    [InlineData("request-timeout")]
    [InlineData("too-many-requests")]
    public async Task ExecuteItemDuplicateMergeAsync_PreExecutionRetryStatusThenForbiddenKeepsConcreteStatus(
        string firstResponse)
    {
        var canonicalId = Guid.Parse("94411111-1111-1111-1111-111111111111");
        var duplicateId = Guid.Parse("94422222-2222-2222-2222-222222222222");
        var handler = new RetryingItemMergeHandler(
            canonicalId,
            duplicateId,
            ambiguousFailure: firstResponse,
            finalStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, CreateOnlineAdminSession());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.ExecuteItemDuplicateMergeAsync(CreateExecuteRequest(canonicalId, duplicateId)));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(2, handler.ExecuteRequests.Count);
    }

    private static ItemDuplicateMergeRequestDto CreateExecuteRequest(Guid canonicalId, Guid duplicateId)
        => new()
        {
            CandidateItemIds = [canonicalId, duplicateId],
            CanonicalItemId = canonicalId,
            ExpectedServerSnapshotToken = "server-retry-token",
            MutationId = $"item-duplicate-merge:{Guid.NewGuid():N}"
        };

    private static void AssertSingleExecuteRequestSemantics(IReadOnlyList<CapturedRequest> requests)
    {
        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
        {
            Assert.Equal("/items/duplicate-merge", request.Path);
            Assert.Equal("Bearer proof-token", request.Authorization);
            Assert.False(string.IsNullOrWhiteSpace(request.BusinessDatabase));
        });
        Assert.All(requests.Skip(1), request => Assert.Equal(requests[0].Body, request.Body));

        var payload = JsonSerializer.Deserialize<ItemDuplicateMergeRequestDto>(
            requests[0].Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(payload);
        Assert.StartsWith("item-duplicate-merge:", payload!.MutationId, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(payload.ExpectedServerSnapshotToken));
    }

    private static ItemDuplicateMergePreviewDto CreatePreview(
        ItemDuplicateMergePreviewRequestDto request,
        string token)
        => new()
        {
            Candidates = request.CandidateItemIds.Select(id => new ItemDuplicateMergeCandidateDto { ItemId = id }).ToList(),
            CanonicalItemId = request.CanonicalItemId,
            ServerSnapshotToken = token,
            CanMerge = true
        };

    private static LocalItem CreateItem(string id)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "서버 권위 중복 품목",
            NameMatchKey = "서버 권위 중복 품목",
            SpecificationOriginal = "A4",
            SpecificationMatchKey = "A4",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m,
            IsDirty = false
        };

    private static async Task ApplyAuthoritativeItemMergeRefreshAsync(
        Guid canonicalItemId,
        IReadOnlyCollection<Guid> tombstonedItemIds,
        CancellationToken ct)
    {
        await using var refreshDb = new LocalDbContext();
        await refreshDb.Items
            .IgnoreQueryFilters()
            .Where(item => item.Id == canonicalItemId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.IsDeleted, false)
                    .SetProperty(item => item.IsDirty, false),
                ct);

        var tombstoneIds = tombstonedItemIds.Distinct().ToList();
        if (tombstoneIds.Count == 0)
            return;

        await refreshDb.Items
            .IgnoreQueryFilters()
            .Where(item => tombstoneIds.Contains(item.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.IsDeleted, true)
                    .SetProperty(item => item.IsDirty, false),
                ct);
    }

    private static SessionState CreateOnlineAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "proof-token",
            CreateAdminUser(),
            DateTime.UtcNow.AddHours(2));
        return session;
    }

    private static UserSessionDto CreateAdminUser()
        => new()
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        };

    private static void PrepareAppRoot(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"georaeplan-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", path);
    }

    private static void ResetAppRoot()
    {
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class ItemMergeHandler(Guid canonicalId, Guid duplicateId) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("X-Tenant-Code", out var values) ? values.Single() : string.Empty,
                body));

            return request.RequestUri.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ItemDuplicateMergePreviewDto
                    {
                        Candidates =
                        [
                            new ItemDuplicateMergeCandidateDto { ItemId = canonicalId },
                            new ItemDuplicateMergeCandidateDto { ItemId = duplicateId }
                        ],
                        CanonicalItemId = canonicalId,
                        ServerSnapshotToken = "server-route-token",
                        CanMerge = true
                    })
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ItemDuplicateMergeResultDto
                    {
                        CanonicalItemId = canonicalId,
                        TombstonedItemIds = [duplicateId],
                        ServerSnapshotToken = "server-route-result"
                    })
                };
        }
    }

    private sealed class RetryingItemMergeHandler(
        Guid canonicalId,
        Guid duplicateId,
        string? ambiguousFailure,
        HttpStatusCode? finalStatus,
        bool failFirstPreview = false,
        HttpStatusCode? firstPreviewStatus = null,
        HttpStatusCode? finalPreviewStatus = null) : HttpMessageHandler
    {
        public List<CapturedRequest> PreviewRequests { get; } = [];
        public List<CapturedRequest> ExecuteRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.TryGetValues("X-Tenant-Code", out var values) ? values.Single() : string.Empty,
                body);

            if (request.RequestUri.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal))
            {
                PreviewRequests.Add(captured);
                if (failFirstPreview && PreviewRequests.Count == 1)
                    throw new HttpRequestException("simulated preview response loss");
                if (PreviewRequests.Count == 1 && firstPreviewStatus is not null)
                    return ErrorResponse(firstPreviewStatus.Value);
                if (finalPreviewStatus is not null)
                    return ErrorResponse(finalPreviewStatus.Value);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ItemDuplicateMergePreviewDto
                    {
                        Candidates =
                        [
                            new ItemDuplicateMergeCandidateDto { ItemId = canonicalId },
                            new ItemDuplicateMergeCandidateDto { ItemId = duplicateId }
                        ],
                        CanonicalItemId = canonicalId,
                        ServerSnapshotToken = "server-retry-token",
                        CanMerge = true
                    })
                };
            }

            ExecuteRequests.Add(captured);
            if (string.Equals(ambiguousFailure, "caller-cancel", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var failEveryAttempt = ambiguousFailure is "http-always" or "timeout-always";
            if ((ExecuteRequests.Count == 1 || failEveryAttempt) && ambiguousFailure is not null)
            {
                if (ambiguousFailure is "gateway-timeout")
                    return ErrorResponse(HttpStatusCode.GatewayTimeout);
                if (ambiguousFailure is "request-timeout")
                    return ErrorResponse(HttpStatusCode.RequestTimeout);
                if (ambiguousFailure is "too-many-requests")
                    return ErrorResponse(HttpStatusCode.TooManyRequests);
                if (ambiguousFailure is "timeout" or "timeout-always")
                    throw new TaskCanceledException("simulated execute timeout");

                throw new HttpRequestException("simulated execute response loss");
            }

            if (finalStatus is not null)
            {
                return new HttpResponseMessage(finalStatus.Value)
                {
                    Content = finalStatus == HttpStatusCode.UpgradeRequired
                        ? JsonContent.Create(new ClientUpgradeRequiredResponse
                        {
                            Message = "upgrade required",
                            Upgrade = "required",
                            Required = new ClientCompatibilityPolicyDto
                            {
                                RequiresUserAction = true,
                                MinimumVersion = "99.0.0"
                            }
                        })
                        : JsonContent.Create(new { message = $"definitive {(int)finalStatus.Value}" })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ItemDuplicateMergeResultDto
                {
                    CanonicalItemId = canonicalId,
                    TombstonedItemIds = [duplicateId],
                    ServerSnapshotToken = "server-retry-token",
                    IsReplay = true
                })
            };
        }

        private static HttpResponseMessage ErrorResponse(HttpStatusCode status)
            => new(status)
            {
                Content = JsonContent.Create(new { message = $"status {(int)status}" })
            };
    }

    private sealed record CapturedRequest(
        string Path,
        string Authorization,
        string BusinessDatabase,
        string Body);
}
