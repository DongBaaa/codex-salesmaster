using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class HostedAuthenticationPipelineTests
{
    [Fact]
    public async Task HostedAmountRead_RestrictedQuantityMemoSaveCalculatesOnServerAndReturnsNull()
    {
        var root=Path.Combine(Path.GetTempPath(),"trade-hosted-amount-read",Guid.NewGuid().ToString("N"));
        var serverRoot=Path.Combine(root,"server");Directory.CreateDirectory(serverRoot);
        CopyDirectory(AppContext.BaseDirectory,serverRoot);
        var password="AmountFixture-"+Guid.NewGuid().ToString("N")+"!9aA";
        var port=GetAvailableLoopbackPort();
        await using var server=StartServer(serverRoot,Path.Combine(serverRoot,"거래플랜.Server.Api.dll"),port,password,password);
        using var client=new HttpClient {BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(15)};
        try
        {
            await WaitUntilReadyAsync(server,client);
            var adminToken=await LoginAsync(client,"admin",password);
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",adminToken);
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.AppId,"kr.georaeplan.desktop");
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Platform,"windows");
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Version,"1.1.743");
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Build,"743");
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Protocol,"2");
            using(var created=await client.PostAsJsonAsync("users",new CreateUserRequest {
                Username="amount-editor",Password=password,Role="User",TenantCode=TenantScopeCatalog.UsenetGroup,
                OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeOfficeOnly,
                Permissions=[PermissionNames.InvoiceEdit,PermissionNames.ItemEdit,PermissionNames.RentalProfileEdit,PermissionNames.RentalAssetEdit] }))
                Assert.True(created.IsSuccessStatusCode,await created.Content.ReadAsStringAsync());
            var customerId=Guid.NewGuid();var itemId=Guid.NewGuid();var invoiceId=Guid.NewGuid();
            using(var customer=await client.PostAsJsonAsync("customers",new CustomerDto {Id=customerId,
                NameOriginal="AMOUNT-FIXTURE",TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode=OfficeCodeCatalog.Usenet}))
                Assert.True(customer.IsSuccessStatusCode,await customer.Content.ReadAsStringAsync());
            using(var item=await client.PostAsJsonAsync("items",new ItemDto {Id=itemId,NameOriginal="AMOUNT-FIXTURE-ITEM",
                TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,Unit="EA",
                TrackingType=ItemTrackingTypes.NonStock,ItemKind=ItemKinds.Product,SalePrice=1100}))
                Assert.True(item.IsSuccessStatusCode,await item.Content.ReadAsStringAsync());
            var transactionId=Guid.NewGuid();
            var rentalId=Guid.NewGuid();
            using(var rental=await client.PostAsJsonAsync("sync/push",new SyncPushRequest
            {
                DeviceId="hosted-rental-privacy",
                RentalBillingProfiles=[new RentalBillingProfileDto {
                    Id=rentalId,ProfileKey="HOSTED-RENTAL-"+rentalId.ToString("N"),CustomerId=customerId,
                    TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,MonthlyAmount=100000,
                    BillingTemplateJson=JsonSerializer.Serialize(new[] {new {ItemId=Guid.NewGuid(),DisplayItemName="월 렌탈",Quantity=2m,UnitPrice=50000m,Amount=100000m,Note="기존 비고"}}),
                    MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow }]
            }))
            {
                Assert.True(rental.IsSuccessStatusCode,await rental.Content.ReadAsStringAsync());
                var rentalResult=(await rental.Content.ReadFromJsonAsync<SyncPushResult>())!;
                Assert.True(rentalResult.ConflictCount==0,string.Join(" | ",rentalResult.Conflicts.Select(x=>x.Reason)));
            }
            var assetId=Guid.NewGuid(); var historyId=Guid.NewGuid(); var logId=Guid.NewGuid();
            using(var assetSeed=await client.PostAsJsonAsync("sync/push",new SyncPushRequest {
                DeviceId="hosted-rental-response-privacy",
                RentalAssets=[new RentalAssetDto {Id=assetId,ManagementNumber="PRIVACY-001",BillingProfileId=rentalId,
                    TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                    PurchasePrice=123456,SalePrice=765432,MonthlyFee=654321,DepositText="50000",BlackOverageUnitPrice=12.5m,
                    Notes="자산 비고 보존",MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow}],
                RentalAssetAssignmentHistories=[new RentalAssetAssignmentHistoryDto {Id=historyId,AssetId=assetId,BillingProfileId=rentalId,
                    TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                    MonthlyFee=234567,MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow}],
                RentalBillingLogs=[new RentalBillingLogDto {Id=logId,BillingProfileId=rentalId,BillingYearMonth="2026-09",ScheduledDate=new(2026,9,25),
                    TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                    BilledAmount=345678,MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow}]}))
            {
                Assert.True(assetSeed.IsSuccessStatusCode,await assetSeed.Content.ReadAsStringAsync());
                var seeded=(await assetSeed.Content.ReadFromJsonAsync<SyncPushResult>())!;
                Assert.True(seeded.ConflictCount==0,string.Join(" | ",seeded.Conflicts.Select(x=>x.Reason)));
            }
            using(var savedTransaction=await client.PostAsJsonAsync("sync/push",new SyncPushRequest {DeviceId="hosted-transaction-privacy",Transactions=[new TransactionDto {
                Id=transactionId,CustomerId=customerId,TransactionKind="일반수금",CashReceipt=77,ReceiptTotal=77,Memo="수금 비고 보존",
                TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow }]}))
            {
                Assert.True(savedTransaction.IsSuccessStatusCode,await savedTransaction.Content.ReadAsStringAsync());
                Assert.Empty((await savedTransaction.Content.ReadFromJsonAsync<SyncPushResult>())!.Conflicts);
            }
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",await LoginAsync(client,"amount-editor",password));
            using(var oldItems=await client.GetAsync($"items/{itemId}"))
                Assert.Equal(HttpStatusCode.UpgradeRequired,oldItems.StatusCode);
            var invoice=new InvoiceDto {Id=invoiceId,CustomerId=customerId,VersionGroupId=invoiceId,IsLatestVersion=true,
                TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                VoucherType=VoucherType.Sales,InvoiceDate=new DateOnly(2026,9,24),VatMode=InvoiceVatModes.Included,
                SourceWarehouseCode=OfficeCodeCatalog.UsenetMainWarehouse,TotalAmount=null,SupplyAmount=null,VatAmount=null,
                Memo="첫 비고",MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow,
                Lines=[new InvoiceLineDto {Id=Guid.NewGuid(),ItemId=itemId,InvoiceId=invoiceId,ItemNameOriginal="AMOUNT-FIXTURE-ITEM",
                    ItemTrackingType=ItemTrackingTypes.NonStock,Unit="EA",Quantity=3,UnitPrice=null,LineAmount=null,Remark="품목 비고"}]};
            using(var created=await client.PostAsJsonAsync("invoices",invoice))
            {
                Assert.True(created.IsSuccessStatusCode,await created.Content.ReadAsStringAsync());
                invoice=(await created.Content.ReadFromJsonAsync<InvoiceDto>())!;
                Assert.Null(invoice.TotalAmount);Assert.Null(invoice.Lines[0].UnitPrice);
            }
            invoice.ExpectedRevision=invoice.Revision;invoice.Lines[0].Quantity=4;invoice.Lines[0].Remark="수량·비고 변경";
            invoice.Memo="수정 비고";invoice.MutationId=Guid.NewGuid().ToString("N");invoice.MutationCreatedAtUtc=DateTime.UtcNow;
            var updateJson=JsonSerializer.Serialize(invoice);
            for(var retry=0;retry<2;retry++)
            {
                using var updated=await client.PutAsJsonAsync($"invoices/{invoiceId}",JsonSerializer.Deserialize<InvoiceDto>(updateJson));
                Assert.True(updated.IsSuccessStatusCode,await updated.Content.ReadAsStringAsync());
                var response=(await updated.Content.ReadFromJsonAsync<InvoiceDto>())!;
                Assert.Null(response.TotalAmount);Assert.Null(response.Lines[0].UnitPrice);Assert.Equal(4m,response.Lines[0].Quantity);
            }
            using(var oldPull=await client.GetAsync("sync/pull?sinceRev=0"))
                Assert.Equal(HttpStatusCode.UpgradeRequired,oldPull.StatusCode);
            client.DefaultRequestHeaders.Remove(ClientCompatibilityHeaders.Protocol);
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Protocol,ClientCompatibilityHeaders.NullableItemAmountsProtocolVersion.ToString());
            using(var oldRentalPull=await client.GetAsync("sync/pull?sinceRev=0"))
            {
                Assert.Equal(HttpStatusCode.UpgradeRequired,oldRentalPull.StatusCode);
                Assert.Equal(4,(await oldRentalPull.Content.ReadFromJsonAsync<ClientUpgradeRequiredResponse>())!.Required.MinimumProtocolVersion);
            }
            client.DefaultRequestHeaders.Remove(ClientCompatibilityHeaders.Protocol);
            client.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Protocol,ClientCompatibilityHeaders.NullableRentalProfileAmountsProtocolVersion.ToString());
            var hiddenItem=(await client.GetFromJsonAsync<ItemDto>($"items/{itemId}"))!;
            Assert.Null(hiddenItem.PurchasePrice);Assert.Null(hiddenItem.SalePrice);
            hiddenItem.SimpleMemo="제한 계정 품목 비고";hiddenItem.ExpectedRevision=hiddenItem.Revision;
            hiddenItem.MutationId=Guid.NewGuid().ToString("N");hiddenItem.MutationCreatedAtUtc=DateTime.UtcNow;
            using(var savedItem=await client.PutAsJsonAsync($"items/{itemId}",hiddenItem))
            {
                Assert.True(savedItem.IsSuccessStatusCode,await savedItem.Content.ReadAsStringAsync());
                Assert.Null((await savedItem.Content.ReadFromJsonAsync<ItemDto>())!.SalePrice);
            }
            using(var pulled=await client.GetAsync("sync/pull?sinceRev=0"))
            {
                Assert.True(pulled.IsSuccessStatusCode,await pulled.Content.ReadAsStringAsync());
                var response=(await pulled.Content.ReadFromJsonAsync<SyncPullResponse>())!;
                Assert.Null(Assert.Single(response.Invoices,x=>x.Id==invoiceId).TotalAmount);
                Assert.Null(Assert.Single(response.Items,x=>x.Id==itemId).SalePrice);
                var hiddenTransaction=Assert.Single(response.Transactions,x=>x.Id==transactionId);
                Assert.Null(hiddenTransaction.ReceiptTotal);Assert.Null(hiddenTransaction.CashReceipt);
                Assert.Equal("수금 비고 보존",hiddenTransaction.Memo);
                var hiddenAsset=Assert.Single(response.RentalAssets,x=>x.Id==assetId);
                Assert.Null(hiddenAsset.PurchasePrice);Assert.Null(hiddenAsset.SalePrice);Assert.Null(hiddenAsset.MonthlyFee);
                Assert.Null(hiddenAsset.DepositText);Assert.Null(hiddenAsset.BlackOverageUnitPrice);
                Assert.True(hiddenAsset.PurchaseAmountsHidden);Assert.True(hiddenAsset.SalesAmountsHidden);
                Assert.Equal("자산 비고 보존",hiddenAsset.Notes);
                Assert.Null(Assert.Single(response.RentalAssetAssignmentHistories,x=>x.Id==historyId).MonthlyFee);
                Assert.Null(Assert.Single(response.RentalBillingLogs,x=>x.Id==logId).BilledAmount);
                var hiddenHistory=Assert.Single(response.RentalAssetAssignmentHistories,x=>x.Id==historyId);
                var hiddenLog=Assert.Single(response.RentalBillingLogs,x=>x.Id==logId);
                hiddenHistory.ChangeReason="숨긴 이력 비고";hiddenLog.Note="숨긴 청구 비고";
                foreach(var command in new SyncEntityDto[] {hiddenHistory,hiddenLog})
                {
                    command.ExpectedRevision=command.Revision;command.MutationId=Guid.NewGuid().ToString("N");
                    command.UpdatedAtUtc=DateTime.UtcNow.AddSeconds(1);
                }
                var historyCommand=JsonSerializer.Serialize(new SyncPushRequest {DeviceId="hosted-history-privacy",
                    RentalAssetAssignmentHistories=[hiddenHistory],RentalBillingLogs=[hiddenLog]});
                for(var retry=0;retry<2;retry++)
                {
                    using var savedHistory=await client.PostAsJsonAsync("sync/push",JsonSerializer.Deserialize<SyncPushRequest>(historyCommand));
                    Assert.True(savedHistory.IsSuccessStatusCode,await savedHistory.Content.ReadAsStringAsync());
                    Assert.Empty((await savedHistory.Content.ReadFromJsonAsync<SyncPushResult>())!.Conflicts);
                }
                hiddenAsset.Notes="금액 비공개 자산 비고 저장";hiddenAsset.ExpectedRevision=hiddenAsset.Revision;
                hiddenAsset.MutationId=Guid.NewGuid().ToString("N");hiddenAsset.UpdatedAtUtc=DateTime.UtcNow.AddSeconds(1);
                var assetCommand=JsonSerializer.Serialize(hiddenAsset);
                for(var retry=0;retry<2;retry++)
                {
                    using var savedAsset=await client.PostAsJsonAsync("sync/push",new SyncPushRequest {
                        DeviceId="hosted-rental-response-privacy",RentalAssets=[JsonSerializer.Deserialize<RentalAssetDto>(assetCommand)!]});
                    Assert.True(savedAsset.IsSuccessStatusCode,await savedAsset.Content.ReadAsStringAsync());
                    Assert.Empty((await savedAsset.Content.ReadFromJsonAsync<SyncPushResult>())!.Conflicts);
                }
                var hiddenRental=Assert.Single(response.RentalBillingProfiles,x=>x.Id==rentalId);
                Assert.Null(hiddenRental.MonthlyAmount);
                var rentalRows=System.Text.Json.Nodes.JsonNode.Parse(hiddenRental.BillingTemplateJson)!.AsArray();
                Assert.Null(rentalRows[0]!["UnitPrice"]);
                rentalRows[0]!["Quantity"]=3m;rentalRows[0]!["Note"]="비공개 수량·비고 변경";
                hiddenRental.BillingTemplateJson=rentalRows.ToJsonString();
                hiddenRental.ExpectedRevision=hiddenRental.Revision;
                hiddenRental.MutationId=Guid.NewGuid().ToString("N");
                hiddenRental.UpdatedAtUtc=DateTime.UtcNow.AddSeconds(1);
                var rentalCommand=JsonSerializer.Serialize(hiddenRental);
                for(var retry=0;retry<2;retry++)
                {
                    using var saved=await client.PostAsJsonAsync("sync/push",new SyncPushRequest {
                        DeviceId="hosted-rental-privacy",RentalBillingProfiles=[JsonSerializer.Deserialize<RentalBillingProfileDto>(rentalCommand)!]});
                    Assert.True(saved.IsSuccessStatusCode,await saved.Content.ReadAsStringAsync());
                    Assert.Empty((await saved.Content.ReadFromJsonAsync<SyncPushResult>())!.Conflicts);
                }
            }
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",adminToken);
            var stored=(await client.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!;
            Assert.Equal(4400m,stored.TotalAmount);Assert.Equal(1100m,stored.Lines[0].UnitPrice);
            Assert.Equal("수정 비고",stored.Memo);Assert.Equal("수량·비고 변경",stored.Lines[0].Remark);
            var storedItem=(await client.GetFromJsonAsync<ItemDto>($"items/{itemId}"))!;
            Assert.Equal(1100m,storedItem.SalePrice);Assert.Equal("제한 계정 품목 비고",storedItem.SimpleMemo);
            var adminPull=(await client.GetFromJsonAsync<SyncPullResponse>("sync/pull?sinceRev=0"))!;
            Assert.Equal(77m,Assert.Single(adminPull.Transactions,x=>x.Id==transactionId).ReceiptTotal);
            var storedAsset=Assert.Single(adminPull.RentalAssets,x=>x.Id==assetId);
            Assert.Equal(123456m,storedAsset.PurchasePrice);Assert.Equal(654321m,storedAsset.MonthlyFee);
            Assert.Equal("금액 비공개 자산 비고 저장",storedAsset.Notes);Assert.Equal(12.5m,storedAsset.BlackOverageUnitPrice);
            Assert.Equal(234567m,Assert.Single(adminPull.RentalAssetAssignmentHistories,x=>x.Id==historyId).MonthlyFee);
            Assert.Equal(345678m,Assert.Single(adminPull.RentalBillingLogs,x=>x.Id==logId).BilledAmount);
            Assert.Equal("숨긴 이력 비고",Assert.Single(adminPull.RentalAssetAssignmentHistories,x=>x.Id==historyId).ChangeReason);
            Assert.Equal("숨긴 청구 비고",Assert.Single(adminPull.RentalBillingLogs,x=>x.Id==logId).Note);
            var storedRental=Assert.Single(adminPull.RentalBillingProfiles,x=>x.Id==rentalId);
            Assert.Equal(150000m,storedRental.MonthlyAmount);
            Assert.Equal("비공개 수량·비고 변경",System.Text.Json.Nodes.JsonNode.Parse(storedRental.BillingTemplateJson)![0]!["Note"]!.GetValue<string>());
        }
        catch(Exception ex) {throw new Xunit.Sdk.XunitException(ex.Message+Environment.NewLine+server.Diagnostics);}
        finally {await server.StopAsync();await DeleteDirectoryWithRetriesAsync(root);}
    }
}
