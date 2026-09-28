using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ItemAmountPermissions_PriceChangesStayPendingWithoutBlockingItemMemo(
        bool viewSales, bool tenantAll)
    {
        PrepareAppRoot("georaeplan-item-amount-permissions");
        try
        {
            var permissions = new List<string>
            {
                AppPermissionNames.ItemEdit, AppPermissionNames.SettingsEdit,
                AppPermissionNames.AmountViewPurchase
            };
            if (viewSales) permissions.Add(AppPermissionNames.AmountViewSales);
            var session = new SessionState();
            session.SetSession("test-token", new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "restricted-price-editor",
                Role = DomainConstants.RoleUser, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = tenantAll ? TenantScopeCatalog.ScopeTenantAll : TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = permissions
            }, DateTime.UtcNow.AddDays(1));
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var item = new LocalItem
            {
                Id = Guid.NewGuid(), NameOriginal = "권한 시험 품목", OfficeCode = OfficeCodeCatalog.Usenet,
                TenantCode = TenantScopeCatalog.UsenetGroup, SimpleMemo = "금액 권한 없이 메모 저장",
                IsDirty = true, Revision = 1
            };
            var option = new LocalPriceGradeOption
            {
                Id = Guid.NewGuid(), Name = "보존할 등급", IsActive = true, IsDirty = true, Revision = 1
            };
            var grade = new LocalItemPriceGrade
            {
                Id = Guid.NewGuid(), ItemId = item.Id, PriceGradeOptionId = option.Id,
                PriceGradeName = option.Name, UnitPrice = 123m, IsActive = true, IsDirty = true, Revision = 1
            };
            var pending = new LocalSyncOutboxEntry
            {
                MutationId = "preserved-price-mutation", EntityName = "ItemPriceGrade", EntityId = grade.Id,
                ExpectedRevision = 1, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, UserId = session.User!.UserId,
                Status = "Prepared"
            };
            db.Items.Add(item);
            db.PriceGradeOptions.Add(option);
            db.ItemPriceGrades.Add(grade);
            if (!viewSales) db.SyncOutboxEntries.Add(pending);
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "1" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            Assert.Equal(viewSales ? 3 : 1, await local.CountDirtyAsync(session));
            var handler = new ItemAmountPermissionHandler(viewSales);
            var sync = CreateSyncService(db, session, handler);
            var synced = await sync.TrySyncAsync();
            // Sync completion still reports preserved, unacknowledged changes;
            // a successful memo upload must not falsely mark those prices complete.
            Assert.Equal(viewSales, synced);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(item.SimpleMemo, Assert.Single(request.Items).SimpleMemo);
            Assert.Equal(viewSales ? 1 : 0, request.ItemPriceGrades.Count);
            Assert.Equal(viewSales ? 1 : 0, request.PriceGradeOptions.Count);
            db.ChangeTracker.Clear();
            Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync()).IsDirty);
            Assert.Equal(!viewSales, (await db.ItemPriceGrades.IgnoreQueryFilters().SingleAsync()).IsDirty);
            Assert.Equal(!viewSales, (await db.PriceGradeOptions.IgnoreQueryFilters().SingleAsync()).IsDirty);
            Assert.Equal(0, await local.CountDirtyAsync(session));
            if (!viewSales)
            {
                Assert.Equal(2, await local.CountDirtyAsync());
                var untouched = await db.SyncOutboxEntries.SingleAsync(x => x.Id == pending.Id);
                Assert.Equal("Prepared", untouched.Status);
                Assert.Equal("preserved-price-mutation", untouched.MutationId);
                Assert.Null(untouched.AcknowledgedAtUtc);
                Assert.Null(await local.GetPendingSyncWaitingMessageAsync(session));
                var sharedReason = await local.GetPendingSyncBlockingReasonAsync(session, "SHARED");
                Assert.NotNull(sharedReason);
                Assert.Contains("매출 금액", sharedReason!.Message);
                var itemReason = await local.GetPendingSyncBlockingReasonAsync(session, "OFFICE:USENET");
                Assert.NotNull(itemReason);
                Assert.Contains("매출 금액", itemReason!.Message);
                Assert.Equal(123m, (await db.ItemPriceGrades.SingleAsync()).UnitPrice);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class ItemAmountPermissionHandler(bool viewSales) : HttpMessageHandler
    {
        public List<SyncPushRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/push")
            {
                var pushed = (await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: ct))!;
                Requests.Add(pushed);
                if (!viewSales && (pushed.ItemPriceGrades.Count > 0 || pushed.PriceGradeOptions.Count > 0))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var result = new SyncPushResult { CurrentServerRevision = 2 };
                void Accept(string name, SyncEntityDto dto) => result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto
                {
                    EntityName = name, EntityId = dto.Id, Revision = 2, UpdatedAtUtc = DateTime.UtcNow
                });
                foreach (var row in pushed.Items) Accept("Item", row);
                foreach (var row in pushed.ItemPriceGrades) Accept("ItemPriceGrade", row);
                foreach (var row in pushed.PriceGradeOptions) Accept("PriceGradeOption", row);
                result.AcceptedCount = result.AcceptedRevisions.Count;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
            }
            if (request.RequestUri.AbsolutePath == "/sync/pull")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse { CurrentServerRevision = 2 })
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
