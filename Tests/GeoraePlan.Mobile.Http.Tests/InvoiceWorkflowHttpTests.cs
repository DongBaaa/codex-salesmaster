using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Mobile.Http.Tests;

public sealed partial class RentalHttpTests
{
    [Theory]
    [InlineData(VoucherType.Sales)]
    [InlineData(VoucherType.Purchase)]
    [InlineData(VoucherType.Procurement)]
    public async Task Protocol4Candidate_RealInvoiceEditorSavesItemsQuantityNotes_WithServerMoneyAndDurableOutbox(VoucherType voucher)
    {
        await RunAsync(async (_, admin, session, settings) =>
        {
            var customer = new CustomerDto { Id = Guid.NewGuid(), NameOriginal = "화면모델 거래처", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, TradeType = "매출매입" };
            var first = InvoiceItem("첫 품목", 1100, 700); var replacement = InvoiceItem("교체 품목", 2200, 1400);
            await PostAsync(admin, "customers", customer); await PostAsync(admin, "items", first); await PostAsync(admin, "items", replacement);
            var api = Client(session, settings, 4); var store = new JsonSyncStateStore(session); var sync = Coordinator(session, store, api);
            using var capture = new InvoiceCaptureHandler(); var field = typeof(GeoraePlanApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((HttpClient)field.GetValue(api)!).Dispose(); using var http = new HttpClient(capture); field.SetValue(api, http);
            var durableAtDispatch = new List<bool>();
            capture.BeforeInvoiceSendAsync = async dto =>
            {
                var disk = await new JsonSyncStateStore(session).LoadAsync();
                durableAtDispatch.Add(disk.PendingPush.Invoices.Any(x => x.Id == dto.Id && x.MutationId == dto.MutationId));
            };
            var initial = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(initial.LastError), initial.LastError);
            var vm = Editor(api, sync, session); vm.ConfigureVoucherType(voucher);
            await vm.LoadAsync(); await vm.SelectCustomerAsync(customer); await vm.SelectItemAsync(first);
            Assert.NotNull(vm.SelectedItem); Assert.False(vm.CanViewInvoiceAmounts); Assert.False(vm.CanViewCurrentLineAmounts);
            vm.LineQuantityText = "3"; vm.LineRemark = "첫 품목 비고"; vm.Memo = "첫 전표 비고";
            await vm.AddOrUpdateLineAsync(); Assert.Single(vm.LineItems); Assert.True(vm.LineItems[0].AmountsHidden);
            var savedEvents = 0; vm.SavedSuccessfully += () => { savedEvents++; return Task.CompletedTask; };
            await vm.SaveDraftCommand.ExecuteAsync(); Assert.True(savedEvents == 1, vm.StatusMessage);
            var created = Assert.Single(capture.InvoicePayloads); AssertInvoiceHidden(created);
            var unit = voucher == VoucherType.Sales ? 1100m : 700m;
            var authoritative = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{created.Id}"))!;
            Assert.Equal(3 * unit, authoritative.TotalAmount); Assert.Equal("첫 전표 비고", authoritative.Memo);
            Assert.Equal("첫 품목 비고", Assert.Single(authoritative.Lines).Remark);
            var restricted = (await api.GetInvoiceByIdAsync(created.Id))!; AssertInvoiceHidden(restricted);
            var edit = Editor(api, sync, session); await edit.LoadExistingInvoiceAsync(restricted);
            await edit.EditLineAsync(Assert.Single(edit.LineItems)); edit.LineQuantityText = "4"; edit.LineRemark = "수량 수정 비고";
            await edit.AddOrUpdateLineAsync(); edit.Memo = "수정 전표 비고";
            var editEvents = 0; edit.SavedSuccessfully += () => { editEvents++; return Task.CompletedTask; };
            await edit.SaveDraftCommand.ExecuteAsync(); Assert.True(editEvents == 1, edit.StatusMessage);
            authoritative = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{created.Id}"))!;
            Assert.Equal(4 * unit, authoritative.TotalAmount); Assert.Equal("수량 수정 비고", Assert.Single(authoritative.Lines).Remark);
            restricted = (await api.GetInvoiceByIdAsync(created.Id))!;
            var replace = Editor(api, sync, session); await replace.LoadExistingInvoiceAsync(restricted);
            await replace.RemoveLineAsync(Assert.Single(replace.LineItems)); await replace.SelectItemAsync(replacement);
            replace.LineQuantityText = "5"; replace.LineRemark = "교체 후 비고"; replace.Memo = "응답 유실 후 보존";
            await replace.AddOrUpdateLineAsync(); capture.LoseNextInvoiceAck = true;
            await replace.SaveDraftCommand.ExecuteAsync();
            var pending = await new JsonSyncStateStore(session).LoadAsync(); Assert.Single(pending.PendingPush.Invoices);
            authoritative = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{created.Id}"))!;
            Assert.Equal(10 * unit, authoritative.TotalAmount); var revision = authoritative.Revision;
            Assert.Equal(replacement.Id, Assert.Single(authoritative.Lines).ItemId);
            var restartedSession = new SessionStore(); var restartedApi = Client(restartedSession, settings, 4);
            var reopened = new JsonSyncStateStore(restartedSession); var retrySync = Coordinator(restartedSession, reopened, restartedApi);
            var retry = await retrySync.PushAsync(); Assert.True(string.IsNullOrEmpty(retry.LastError), retry.LastError); Assert.Empty(retry.PendingPush.Invoices);
            Assert.Equal(revision, (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{created.Id}"))!.Revision);
            await retrySync.PullAsync(); var final = await new JsonSyncStateStore(restartedSession).LoadAsync();
            var finalInvoice = Assert.Single(final.SyncedInvoices, x => x.Id == created.Id); AssertInvoiceHidden(finalInvoice);
            Assert.Equal("응답 유실 후 보존", finalInvoice.Memo); Assert.Equal(5m, Assert.Single(finalInvoice.Lines).Quantity);
            Assert.All(capture.InvoicePayloads, AssertInvoiceHidden);
            Assert.Equal(3, durableAtDispatch.Count);
            Assert.All(durableAtDispatch, persisted => Assert.True(persisted, "Invoice mutation must be durable before the real HTTP request can commit."));
        }, ["Invoice.Edit"]);
    }

    private static ItemDto InvoiceItem(string name, decimal sales, decimal purchase) => new() { Id = Guid.NewGuid(), NameOriginal = name,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.NonStock, Unit = "EA", SalePrice = sales, PurchasePrice = purchase };
    private static InvoiceDraftViewModel Editor(GeoraePlanApiClient api, SyncCoordinator sync, SessionStore session)
        => new(api, sync, new MobileRefreshCoordinator(), session, new RecentItemSelectionStore(), new MobileInvoicePdfExportService());
    private static void AssertInvoiceHidden(InvoiceDto invoice)
    {
        Assert.Null(invoice.TotalAmount); Assert.Null(invoice.SupplyAmount); Assert.Null(invoice.VatAmount);
        foreach (var line in invoice.Lines) { Assert.Null(line.UnitPrice); Assert.Null(line.LineAmount); }
    }
    private sealed class InvoiceCaptureHandler : DelegatingHandler
    {
        public bool LoseNextInvoiceAck { get; set; }
        public Func<InvoiceDto, Task>? BeforeInvoiceSendAsync { get; set; }
        public List<InvoiceDto> InvoicePayloads { get; } = [];
        public InvoiceCaptureHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var invoiceWrite = request.RequestUri!.AbsolutePath.StartsWith("/invoices", StringComparison.Ordinal) &&
                (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put);
            if (invoiceWrite)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var dto = JsonSerializer.Deserialize<InvoiceDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                InvoicePayloads.Add(dto); if (BeforeInvoiceSendAsync is not null) await BeforeInvoiceSendAsync(dto);
            }
            var response = await base.SendAsync(request, ct);
            if (invoiceWrite && LoseNextInvoiceAck && response.IsSuccessStatusCode)
            { LoseNextInvoiceAck = false; response.Dispose(); throw new HttpRequestException("simulated invoice response loss after real server commit"); }
            return response;
        }
    }
}
