using System.Net.Http.Json;
using System.Text.Json;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Mobile.Http.Tests;

public sealed partial class RentalHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol4Candidate_NewCurrentAssignmentAndBillingLog_UseServerMoneyAndReplayExactly(bool zero)
    {
        await RunAsync(async (_, admin, session, settings) =>
        {
            var seeded = await SeedRecords(admin, OfficeCodeCatalog.Usenet, zero);
            await SeedAuthoritativeRun(admin, seeded, zero, "valid");
            var api = Client(session, settings, 4);
            using var capture = new CaptureHandler(); using var http = InjectCapture(api, capture);
            var sync = Coordinator(session, new JsonSyncStateStore(session), api);
            var pulled = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(pulled.LastError), pulled.LastError);
            var closed = Copy(Assert.Single(pulled.SyncedRentalAssetAssignmentHistories, x => x.Id == seeded.History.Id));
            var boundary = DateTime.UtcNow;
            Stamp(closed); closed.IsCurrent = false; closed.UnlinkedAtUtc = boundary; closed.ChangeReason = "이전 설치 종료";
            var current = Copy(closed); current.Id = Guid.NewGuid(); current.Revision = 0; Stamp(current);
            current.IsCurrent = true; current.LinkedAtUtc = boundary; current.UnlinkedAtUtc = null; current.ChangeReason = "새 설치 기록";
            var log = Copy(Assert.Single(pulled.SyncedRentalBillingLogs, x => x.Id == seeded.Log.Id));
            log.Id = Guid.NewGuid(); log.Revision = 0; Stamp(log); log.BillingYearMonth = "2026-08"; log.ScheduledDate = new(2026, 8, 25);
            log.Note = "서버 청구 기준 적용"; log.Status = "완료"; log.ProcessedByUsername = "forged";
            await sync.QueueRentalAssetAssignmentHistoryDraftAsync(closed);
            await sync.QueueRentalAssetAssignmentHistoryDraftAsync(current);
            await sync.QueueRentalBillingLogDraftAsync(log);
            capture.LoseNextPushAck = true;
            var lost = await sync.PushAsync(); Assert.NotEmpty(lost.LastError);
            var committed = Assert.Single(capture.PushResults);
            Assert.True(committed.ConflictCount == 0, string.Join(" | ", committed.Conflicts.Select(x => x.Reason)));
            Assert.Equal(3, committed.AcceptedCount);
            var saved = await AdminPull(admin);
            var oldSaved = Assert.Single(saved.RentalAssetAssignmentHistories, x => x.Id == closed.Id);
            var newSaved = Assert.Single(saved.RentalAssetAssignmentHistories, x => x.Id == current.Id);
            Assert.True(saved.RentalBillingLogs.Any(x => x.Id == log.Id), "New billing log was not persisted: " + lost.LastError);
            var logSaved = Assert.Single(saved.RentalBillingLogs, x => x.Id == log.Id);
            Assert.False(oldSaved.IsCurrent); Assert.Equal(seeded.History.MonthlyFee, oldSaved.MonthlyFee);
            Assert.Equal(seeded.History.LinkedAtUtc, oldSaved.LinkedAtUtc); Assert.Equal(boundary, oldSaved.UnlinkedAtUtc);
            Assert.Equal(seeded.Asset.MonthlyFee, newSaved.MonthlyFee); Assert.Equal(boundary, newSaved.LinkedAtUtc);
            Assert.Single(saved.RentalAssetAssignmentHistories, x => x.AssetId == seeded.Asset.Id && x.IsCurrent && !x.IsDeleted);
            var run = System.Text.Json.Nodes.JsonNode.Parse((await AdminProfile(admin, seeded.Asset.BillingProfileId!.Value)).BillingRunsJson)![0]!;
            Assert.Equal(zero ? 0m : 100000m, logSaved.BilledAmount);
            Assert.Equal(run["SettlementStatus"]!.GetValue<string>(), logSaved.Status);
            Assert.Equal(string.Empty, logSaved.ProcessedByUsername);
            var serverSnapshot = JsonSerializer.Serialize(new[] { JsonSerializer.Serialize(oldSaved), JsonSerializer.Serialize(newSaved), JsonSerializer.Serialize(logSaved) });
            var restarted = new SessionStore(); var retryApi = Client(restarted, settings, 4);
            using var retryCapture = new CaptureHandler(); using var retryHttp = InjectCapture(retryApi, retryCapture);
            var retrySync = Coordinator(restarted, new JsonSyncStateStore(restarted), retryApi);
            var retry = await retrySync.PushAsync(); Assert.True(string.IsNullOrEmpty(retry.LastError), retry.LastError);
            Assert.Empty(retry.PendingPush.RentalAssetAssignmentHistories); Assert.Empty(retry.PendingPush.RentalBillingLogs);
            Assert.Equal(Assert.Single(capture.PushPayloads), Assert.Single(retryCapture.PushPayloads));
            var final = await AdminPull(admin);
            Assert.Equal(serverSnapshot, JsonSerializer.Serialize(new[] {
                JsonSerializer.Serialize(Assert.Single(final.RentalAssetAssignmentHistories, x => x.Id == closed.Id)),
                JsonSerializer.Serialize(Assert.Single(final.RentalAssetAssignmentHistories, x => x.Id == current.Id)),
                JsonSerializer.Serialize(Assert.Single(final.RentalBillingLogs, x => x.Id == log.Id)) }));
            await retrySync.PullAsync(); var disk = await new JsonSyncStateStore(restarted).LoadAsync();
            Assert.Null(Assert.Single(disk.SyncedRentalAssetAssignmentHistories, x => x.Id == current.Id).MonthlyFee);
            Assert.Null(Assert.Single(disk.SyncedRentalBillingLogs, x => x.Id == log.Id).BilledAmount);
            Assert.Equal("새 설치 기록", Assert.Single(disk.SyncedRentalAssetAssignmentHistories, x => x.Id == current.Id).ChangeReason);
            var sent = JsonSerializer.Deserialize<SyncPushRequest>(capture.PushPayloads[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.All(sent.RentalAssetAssignmentHistories, x => Assert.Null(x.MonthlyFee));
            Assert.All(sent.RentalBillingLogs, x => Assert.Null(x.BilledAmount));
        });
    }

    [Theory]
    [InlineData("new-asset")]
    [InlineData("past-assignment")]
    [InlineData("missing-run")]
    [InlineData("ambiguous-run")]
    public async Task Protocol4Candidate_UnknownNewRentalMoney_IsRejectedAndDraftRetained(string kind)
    {
        await RunAsync(async (_, admin, session, settings) =>
        {
            var seeded = await SeedRecords(admin, OfficeCodeCatalog.Usenet, false);
            if (kind == "ambiguous-run") await SeedAuthoritativeRun(admin, seeded, false, "ambiguous");
            var before = await AdminPull(admin);
            var api = Client(session, settings, 4); var sync = Coordinator(session, new JsonSyncStateStore(session), api);
            var pulled = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(pulled.LastError), pulled.LastError);
            var id = Guid.NewGuid();
            if (kind == "new-asset")
            {
                var asset = Copy(Assert.Single(pulled.SyncedRentalAssets, x => x.Id == seeded.Asset.Id));
                asset.Id = id; asset.Revision = 0; Stamp(asset); asset.AssetKey = asset.ManagementNumber = asset.ManagementId = "NEW-" + id.ToString("N");
                await sync.QueueRentalAssetDraftAsync(asset);
            }
            else if (kind == "past-assignment")
            {
                var h = Copy(Assert.Single(pulled.SyncedRentalAssetAssignmentHistories, x => x.Id == seeded.History.Id));
                h.Id = id; h.Revision = 0; Stamp(h); h.IsCurrent = false; h.UnlinkedAtUtc = DateTime.UtcNow;
                await sync.QueueRentalAssetAssignmentHistoryDraftAsync(h);
            }
            else
            {
                var log = Copy(Assert.Single(pulled.SyncedRentalBillingLogs, x => x.Id == seeded.Log.Id));
                log.Id = id; log.Revision = 0; Stamp(log); log.BillingYearMonth = "2026-08"; log.ScheduledDate = new(2026, 8, 25);
                await sync.QueueRentalBillingLogDraftAsync(log);
            }
            var result = await sync.PushAsync(); Assert.NotEmpty(result.LastError);
            var after = await AdminPull(admin);
            Assert.DoesNotContain(after.RentalAssets, x => x.Id == id);
            Assert.DoesNotContain(after.RentalAssetAssignmentHistories, x => x.Id == id);
            Assert.DoesNotContain(after.RentalBillingLogs, x => x.Id == id);
            Assert.Equal(JsonSerializer.Serialize(before.RentalAssets), JsonSerializer.Serialize(after.RentalAssets));
            Assert.Equal(JsonSerializer.Serialize(before.RentalAssetAssignmentHistories), JsonSerializer.Serialize(after.RentalAssetAssignmentHistories));
            Assert.Equal(JsonSerializer.Serialize(before.RentalBillingLogs), JsonSerializer.Serialize(after.RentalBillingLogs));
            var disk = await new JsonSyncStateStore(session).LoadAsync();
            Assert.Contains(disk.PendingPush.RentalAssets.Cast<SyncEntityDto>().Concat(disk.PendingPush.RentalAssetAssignmentHistories).Concat(disk.PendingPush.RentalBillingLogs), x => x.Id == id);
        });
    }

    private static async Task<SyncPullResponse> AdminPull(HttpClient admin)
        => (await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!;

    private static async Task SeedAuthoritativeRun(HttpClient admin, Records records, bool zero, string mode)
    {
        var p = await AdminProfile(admin, records.Asset.BillingProfileId!.Value); Stamp(p);
        p.BillingDay = 25; p.BillingCycleMonths = 1;
        var items = new[] { new { ItemId = Guid.NewGuid(), DisplayItemName = "확정 계약 품목", Quantity = 2m,
            UnitPrice = zero ? 0m : 50000m, Amount = zero ? 0m : 100000m, IncludedAssetIds = Array.Empty<Guid>() } };
        var run = new { RunId = Guid.NewGuid(), RunKey = "2026-08", ScheduledDate = "2026-08-25",
            PeriodStartDate = "2026-08-01", PeriodEndDate = "2026-08-31", CycleMonths = 1, PeriodLabel = "2026-08",
            Status = "청구중", BilledAmount = zero ? 0m : 100000m, SettledAmount = 0m, SettlementStatus = "미입금", Items = items };
        var rows = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new[] { run }))!.AsArray();
        if (mode == "ambiguous") { var other = rows[0]!.DeepClone(); other["RunId"] = Guid.NewGuid(); other["RunKey"] = "other"; rows.Add(other); }
        p.BillingRunsJson = rows.ToJsonString();
        using var response = await admin.PostAsJsonAsync("sync/push", new SyncPushRequest { DeviceId = "isolated-authoritative-run",
            RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion, RentalBillingProfiles = [p] });
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.True(result.ConflictCount == 0, string.Join(" | ", result.Conflicts.Select(x => x.Reason))); Assert.Equal(1, result.AcceptedCount);
        // A sync payload alone is not authoritative billing evidence, even for an admin.
        // Persist real linked sales invoices so the server derives each run's date and billed amount.
        var customer = new CustomerDto { Id = Guid.NewGuid(), NameOriginal = "격리 청구 근거 거래처",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var item = InvoiceItem("청구 근거 품목", zero ? 0m : 100000m, 0m);
        await PostAsync(admin, "customers", customer); await PostAsync(admin, "items", item);
        foreach (var row in rows)
        {
            var amount = 100000m;
            var invoice = new InvoiceDto { Id = Guid.NewGuid(), CustomerId = customer.Id, CustomerName = customer.NameOriginal,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales, InvoiceDate = new(2026, 8, 25), LinkedRentalBillingProfileId = p.Id,
                LinkedRentalBillingRunId = row!["RunId"]!.GetValue<Guid>(), TotalAmount = amount, SupplyAmount = amount, VatAmount = 0m,
                Lines = [new() { Id = Guid.NewGuid(), ItemId = item.Id, ItemNameOriginal = item.NameOriginal,
                    Quantity = 1m, UnitPrice = amount, LineAmount = amount, Unit = "EA" }] };
            Stamp(invoice); await PostAsync(admin, "invoices", invoice);
            if (zero)
            {
                // An unconfirmed zero-only invoice does not establish a historical billing date.
                // Revise a real billed invoice to an explicit zero while retaining its confirmed period.
                var savedInvoice = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoice.Id}"))!;
                Stamp(savedInvoice); savedInvoice.TotalAmount = savedInvoice.SupplyAmount = savedInvoice.VatAmount = 0m;
                foreach (var line in savedInvoice.Lines) line.UnitPrice = line.LineAmount = 0m;
                using var update = await admin.PutAsJsonAsync($"invoices/{invoice.Id}", savedInvoice);
                update.EnsureSuccessStatusCode();
                Assert.Equal(0m, (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoice.Id}"))!.TotalAmount);
            }
        }
        var saved = await AdminProfile(admin, p.Id);
        Assert.Equal(mode == "ambiguous" ? 2 : 1, System.Text.Json.Nodes.JsonNode.Parse(saved.BillingRunsJson)!.AsArray().Count);
        Assert.True(RentalBillingRunTombstonePolicy.ValidateForServerMutation(saved.BillingRunsJson).IsValid, saved.BillingRunsJson);
        var persistedRun = System.Text.Json.Nodes.JsonNode.Parse(saved.BillingRunsJson)![0]!;
        Assert.True(persistedRun["ScheduledDate"]!.GetValue<string>() == "2026-08-25", saved.BillingRunsJson);
        Assert.True(persistedRun["BilledAmount"]!.GetValue<decimal>() == (zero ? 0m : 100000m), saved.BillingRunsJson);
    }
}
