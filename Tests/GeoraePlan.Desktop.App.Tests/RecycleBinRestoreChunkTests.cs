using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RecycleBinRestoreChunkTests
{
    [Theory]
    [InlineData("cascade")]
    [InlineData("stale")]
    [InlineData("other-database")]
    [InlineData("wrong-kind")]
    [InlineData("older-server")]
    [InlineData("duplicate-receipt")]
    [InlineData("invalid-revision")]
    [InlineData("partial-success")]
    public async Task Restore_SelectionCrossingFortyItemBoundary_UsesOnlyMatchingCommittedEffects(string mode)
    {
        var session = new SessionState();
        session.SetSession("isolated-test", new UserSessionDto
        {
            Username = "admin", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        }, DateTime.UtcNow.AddHours(1));
        var entries = Enumerable.Range(0, 40).Select(i => new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(), Kind = RecycleBinEntityKind.Customer, Revision = 100 + i,
            BusinessDatabaseName = "georaeplan_usenet"
        }).ToList();
        var contract = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(), Kind = RecycleBinEntityKind.CustomerContract, Revision = 150,
            BusinessDatabaseName = mode == "other-database" ? "georaeplan_itworld" : "georaeplan_usenet"
        };
        entries.Add(contract);
        using var handler = new RestoreHandler(contract.EntityId, mode);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new ErpApiClient(http, session);
        // Mirror uses only the session and HTTP client for restores; no DB or UI is accessed.
        var vm = new EnvironmentSettingsViewModel(null!, session, api, null!, null!, null!, null!, null!, null!, null!, null!);
        var result = await vm.MirrorRecycleBinMutationToServerAsync("복원", entries);

        var covered = mode is "cascade" or "partial-success";
        Assert.Equal(covered ? 1 : 2, handler.RequestSizes.Count);
        Assert.Equal(40, handler.RequestSizes[0]);
        Assert.Equal(covered, result.SucceededEntries.Contains(contract));
        Assert.Equal(mode == "partial-success" ? 39 : covered ? 41 : 40, result.SucceededEntries.Count);
        Assert.Equal(mode == "partial-success" ? 2 : covered ? 0 : 1, result.Failures.Count);
        if (covered)
            Assert.True(result.RequiresAuthoritativeRefresh);

        // Starting another user operation must never reuse a previous operation's receipt.
        var retry = await vm.MirrorRecycleBinMutationToServerAsync("복원", [contract]);
        Assert.Empty(retry.SucceededEntries);
        Assert.Single(retry.Failures);
        Assert.Equal(1, handler.RequestSizes[^1]);
    }

    private sealed class RestoreHandler(Guid contractId, string mode) : HttpMessageHandler
    {
        public List<int> RequestSizes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/recycle-bin/restore", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<RecycleBinMutationRequest>(ct);
            Assert.NotNull(body);
            RequestSizes.Add(body.Items.Count);
            var result = new RecycleBinMutationResultDto { RequestedCount = body.Items.Count };
            foreach (var (target, index) in body.Items.Select((target, index) => (target, index)))
            {
                var success = target.EntityId != contractId && !(mode == "partial-success" && index is 2 or 3);
                result.Results.Add(new RecycleBinMutationItemResultDto
                {
                    EntityId = target.EntityId, Kind = target.Kind, Success = success,
                    Message = success ? "restored" : "revision conflict"
                });
                result.Messages.Add(result.Results[^1].Message);
                if (success) result.SucceededCount++;
            }
            if (RequestSizes.Count == 1 && mode != "older-server")
            {
                result.CommittedRestores.Add(new RecycleBinCommittedRestoreDto
                {
                    EntityId = contractId, Kind = mode == "wrong-kind" ? "item" : "contract",
                    PreviousRevision = mode == "stale" ? 149 : 150,
                    Revision = mode == "invalid-revision" ? 150 : 200
                });
                if (mode == "duplicate-receipt")
                    result.CommittedRestores.Add(result.CommittedRestores[0]);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
