using System.Net;
using System.Net.Http.Json;
using System.Text;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TenantPolicyMutationRecoveryTests
{
    public static TheoryData<TenantMutationKind, AmbiguousFailureKind> AmbiguousFailures()
    {
        var data = new TheoryData<TenantMutationKind, AmbiguousFailureKind>();
        foreach (var mutation in Enum.GetValues<TenantMutationKind>())
        foreach (var failure in Enum.GetValues<AmbiguousFailureKind>())
            data.Add(mutation, failure);
        return data;
    }

    [Theory]
    [MemberData(nameof(AmbiguousFailures))]
    public async Task TenantMutation_AmbiguousFailure_IsSingleDispatch(
        TenantMutationKind mutation,
        AmbiguousFailureKind failure)
    {
        var handler = new TenantMutationHandler(mutation, failure);
        var api = CreateApi(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() =>
            InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.MutationSendCount);
    }

    [Theory]
    [InlineData(TenantMutationKind.UpdateTenant)]
    [InlineData(TenantMutationKind.UpdateOffice)]
    [InlineData(TenantMutationKind.CreateSharing)]
    [InlineData(TenantMutationKind.UpdateSharing)]
    [InlineData(TenantMutationKind.DeleteSharing)]
    public async Task TenantMutation_FirstConflict_RemainsDefinitive(
        TenantMutationKind mutation)
    {
        var handler = new TenantMutationHandler(mutation, HttpStatusCode.Conflict);
        var api = CreateApi(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            InvokeMutationAsync(api, mutation));

        Assert.IsNotType<AmbiguousMutationOutcomeException>(exception);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal(1, handler.MutationSendCount);
    }

    [Theory]
    [InlineData(TenantMutationKind.UpdateTenant)]
    [InlineData(TenantMutationKind.UpdateOffice)]
    [InlineData(TenantMutationKind.CreateSharing)]
    [InlineData(TenantMutationKind.UpdateSharing)]
    [InlineData(TenantMutationKind.DeleteSharing)]
    public async Task TenantMutation_InvalidSuccessPayload_IsAmbiguousAndNotAccepted(
        TenantMutationKind mutation)
    {
        var handler = new TenantMutationHandler(mutation, invalidSuccessPayload: true);
        var api = CreateApi(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() =>
            InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.MutationSendCount);
    }

    [Theory]
    [InlineData(TenantMutationKind.UpdateTenant)]
    [InlineData(TenantMutationKind.UpdateOffice)]
    [InlineData(TenantMutationKind.CreateSharing)]
    [InlineData(TenantMutationKind.UpdateSharing)]
    [InlineData(TenantMutationKind.DeleteSharing)]
    public async Task TenantMutation_ValidCanonicalSuccess_IsAccepted(
        TenantMutationKind mutation)
    {
        var handler = new TenantMutationHandler(mutation);
        var api = CreateApi(handler);

        await InvokeMutationAsync(api, mutation);

        Assert.Equal(1, handler.MutationSendCount);
    }

    [Theory]
    [InlineData(TenantMutationKind.UpdateTenant)]
    [InlineData(TenantMutationKind.UpdateOffice)]
    [InlineData(TenantMutationKind.UpdateSharing)]
    [InlineData(TenantMutationKind.DeleteSharing)]
    public async Task TenantMutation_NoOpSuccessMustStillAdvanceRevision(
        TenantMutationKind mutation)
    {
        var handler = new TenantMutationHandler(mutation, staleSuccessRevision: true);
        var api = CreateApi(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() =>
            InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.MutationSendCount);
    }

    [Fact]
    public async Task AmbiguousViewModelWorkflow_ReloadsOnce_WithoutRepeatingWrite()
    {
        var handler = new TenantMutationHandler(
            TenantMutationKind.UpdateTenant,
            AmbiguousFailureKind.Transport);
        var api = CreateApi(handler);

        var result = await EnvironmentSettingsViewModel.ExecuteTenantMutationWithRecoveryAsync(
            async () =>
            {
                await api.UpdateTenantDefinitionAsync(
                    TenantScopeCatalog.UsenetGroup,
                    new UpdateTenantDefinitionRequest
                    {
                        ExpectedRevision = 7,
                        DisplayName = "USENET",
                        StorageMode = TenantScopeCatalog.StorageSharedDatabase,
                        Description = "description",
                        IsActive = true
                    });
            },
            async () =>
            {
                await api.GetTenantConfigurationAsync(includeInactive: true);
            },
            currentStateMatchesRequest: () => true);

        Assert.True(result.IsAmbiguous);
        Assert.True(result.CurrentStateMatchesRequest);
        Assert.Equal(1, handler.MutationSendCount);
        Assert.Equal(1, handler.SnapshotReadCount);
        Assert.True(handler.LastSnapshotIncludedInactive);
        Assert.Contains("반복하지 마세요", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("식별할 수 없습니다", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousViewModelWorkflow_FailedReload_IsNotReportedAsReconciled()
    {
        var writeCount = 0;
        var reloadCount = 0;
        var matchCheckCount = 0;

        var result = await EnvironmentSettingsViewModel.ExecuteTenantMutationWithRecoveryAsync(
            () =>
            {
                writeCount++;
                return Task.FromException(
                    new AmbiguousMutationOutcomeException(
                        "tenant update",
                        new HttpRequestException("response lost")));
            },
            () =>
            {
                reloadCount++;
                return Task.FromException(new HttpRequestException("reload failed"));
            },
            () =>
            {
                matchCheckCount++;
                return true;
            });

        Assert.True(result.IsAmbiguous);
        Assert.False(result.CurrentStateMatchesRequest);
        Assert.Equal(1, writeCount);
        Assert.Equal(1, reloadCount);
        Assert.Equal(0, matchCheckCount);
        Assert.Contains("다시 불러오지도 못했습니다", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("반복하지 마세요", result.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenantConfiguration_InactiveReload_UsesExplicitQueryFlag()
    {
        var handler = new TenantMutationHandler(TenantMutationKind.UpdateTenant);
        var api = CreateApi(handler);

        await api.GetTenantConfigurationAsync(includeInactive: true);

        Assert.Equal(1, handler.SnapshotReadCount);
        Assert.True(handler.LastSnapshotIncludedInactive);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("forbidden")]
    [InlineData("null")]
    public async Task TenantConfigurationReload_FailedFetch_DoesNotApplyOrReplaceExistingSnapshot(
        string failure)
    {
        var existing = new List<string> { "existing" };
        var applyCount = 0;
        Func<Task<TenantConfigurationSnapshotDto?>> fetchAsync;
        if (failure == "transport")
        {
            fetchAsync = () => Task.FromException<TenantConfigurationSnapshotDto?>(
                new HttpRequestException("offline"));
        }
        else
        {
            var api = CreateApi(new SnapshotFailureHandler(failure));
            fetchAsync = () => api.GetTenantConfigurationAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnvironmentSettingsViewModel.FetchAndApplyTenantConfigurationSnapshotAsync(
                fetchAsync,
                snapshot =>
                {
                    applyCount++;
                    existing.Clear();
                }));

        Assert.Equal(0, applyCount);
        Assert.Equal(["existing"], existing);
    }

    [Fact]
    public async Task TenantConfigurationReload_ValidatedFetch_AppliesExactlyOnce()
    {
        var applyCount = 0;
        var snapshot = new TenantConfigurationSnapshotDto();

        await EnvironmentSettingsViewModel.FetchAndApplyTenantConfigurationSnapshotAsync(
            () => Task.FromResult<TenantConfigurationSnapshotDto?>(snapshot),
            applied =>
            {
                applyCount++;
                Assert.Same(snapshot, applied);
            });

        Assert.Equal(1, applyCount);
    }

    [Fact]
    public async Task ConfirmedWrite_ReloadFailure_IsReportedAsConfirmedNotWriteFailure()
    {
        var result = await EnvironmentSettingsViewModel.ReloadAfterConfirmedTenantMutationAsync(
            () => Task.FromException(new HttpRequestException("reload failed")),
            "연동 정책을 저장했습니다.");

        Assert.False(result.RefreshSucceeded);
        Assert.Contains("서버 저장은 확정", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("새로고침 실패", result.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("반복하지 마세요", result.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("저장 실패", result.StatusMessage, StringComparison.Ordinal);
    }

    private static ErpApiClient CreateApi(HttpMessageHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new SessionState());

    private static async Task InvokeMutationAsync(ErpApiClient api, TenantMutationKind mutation)
    {
        switch (mutation)
        {
            case TenantMutationKind.UpdateTenant:
                await api.UpdateTenantDefinitionAsync(
                    TenantScopeCatalog.UsenetGroup,
                    new UpdateTenantDefinitionRequest
                    {
                        ExpectedRevision = 7,
                        DisplayName = " USENET ",
                        StorageMode = TenantScopeCatalog.StorageSharedDatabase,
                        Description = " description ",
                        IsActive = true
                    });
                break;
            case TenantMutationKind.UpdateOffice:
                await api.UpdateTenantOfficeDefinitionAsync(
                    OfficeCodeCatalog.Usenet,
                    new UpdateTenantOfficeDefinitionRequest
                    {
                        ExpectedRevision = 7,
                        DisplayName = " USENET office ",
                        IsHeadOffice = true,
                        IsActive = true
                    });
                break;
            case TenantMutationKind.CreateSharing:
                await api.CreateSharingPolicyAsync(CreateSharingRequest(expectedRevision: 0));
                break;
            case TenantMutationKind.UpdateSharing:
                await api.UpdateSharingPolicyAsync(TenantMutationHandler.PolicyId, CreateSharingRequest(expectedRevision: 7));
                break;
            case TenantMutationKind.DeleteSharing:
                await api.DeleteSharingPolicyAsync(TenantMutationHandler.PolicyId, expectedRevision: 7);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static UpsertDataSharingPolicyRequest CreateSharingRequest(long expectedRevision)
        => new()
        {
            ExpectedRevision = expectedRevision,
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            ShareInvoices = true,
            SharePayments = true,
            ShareContracts = true,
            ShareReports = true,
            ShareRentals = true,
            ShareDeliveries = true,
            AllowTargetWrite = false,
            IsActive = true,
            Note = " note "
        };

    public enum TenantMutationKind
    {
        UpdateTenant,
        UpdateOffice,
        CreateSharing,
        UpdateSharing,
        DeleteSharing
    }

    public enum AmbiguousFailureKind
    {
        Transport,
        RequestTimeout,
        TooManyRequests,
        ServerError,
        BodyRead
    }

    private sealed class TenantMutationHandler : HttpMessageHandler
    {
        public static readonly Guid PolicyId = Guid.Parse("c756b057-a1f0-4cf9-ae87-2e62a1a8dd83");

        private readonly TenantMutationKind _mutation;
        private readonly AmbiguousFailureKind? _failure;
        private readonly HttpStatusCode? _statusCode;
        private readonly bool _invalidSuccessPayload;
        private readonly bool _staleSuccessRevision;

        public TenantMutationHandler(
            TenantMutationKind mutation,
            AmbiguousFailureKind failure)
        {
            _mutation = mutation;
            _failure = failure;
        }

        public TenantMutationHandler(
            TenantMutationKind mutation,
            HttpStatusCode statusCode)
        {
            _mutation = mutation;
            _statusCode = statusCode;
        }

        public TenantMutationHandler(
            TenantMutationKind mutation,
            bool invalidSuccessPayload = false,
            bool staleSuccessRevision = false)
        {
            _mutation = mutation;
            _invalidSuccessPayload = invalidSuccessPayload;
            _staleSuccessRevision = staleSuccessRevision;
        }

        public int MutationSendCount { get; private set; }
        public int SnapshotReadCount { get; private set; }
        public bool LastSnapshotIncludedInactive { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/tenant-settings")
            {
                SnapshotReadCount++;
                LastSnapshotIncludedInactive = string.Equals(
                    request.RequestUri.Query,
                    "?includeInactive=true",
                    StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(JsonResponse(new TenantConfigurationSnapshotDto()));
            }

            MutationSendCount++;
            if (_failure == AmbiguousFailureKind.Transport)
                throw new HttpRequestException("connection lost");
            if (_failure == AmbiguousFailureKind.RequestTimeout)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
            if (_failure == AmbiguousFailureKind.TooManyRequests)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            if (_failure == AmbiguousFailureKind.ServerError)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            if (_statusCode is { } statusCode)
                return Task.FromResult(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("conflict", Encoding.UTF8, "text/plain")
                });

            var response = CreateSuccessfulResponse(
                _mutation,
                _invalidSuccessPayload,
                _staleSuccessRevision);
            if (_failure == AmbiguousFailureKind.BodyRead)
                response.Content = new ThrowingContent();
            return Task.FromResult(response);
        }

        private static HttpResponseMessage CreateSuccessfulResponse(
            TenantMutationKind mutation,
            bool invalid,
            bool staleRevision)
        {
            object payload = mutation switch
            {
                TenantMutationKind.UpdateTenant => new TenantDefinitionDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = invalid ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
                    DisplayName = "USENET",
                    StorageMode = TenantScopeCatalog.StorageSharedDatabase,
                    Description = "description",
                    IsActive = true,
                    IsDeleted = false,
                    Revision = 8
                },
                TenantMutationKind.UpdateOffice => new TenantOfficeDefinitionDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = invalid ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Usenet,
                    DisplayName = "USENET office",
                    IsHeadOffice = true,
                    IsActive = true,
                    IsDeleted = false,
                    Revision = 8
                },
                TenantMutationKind.CreateSharing or TenantMutationKind.UpdateSharing => new DataSharingPolicyDto
                {
                    Id = mutation == TenantMutationKind.CreateSharing && invalid ? Guid.Empty : PolicyId,
                    SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ShareCustomers = true,
                    ShareItems = true,
                    ShareInvoices = true,
                    SharePayments = true,
                    ShareContracts = true,
                    ShareReports = true,
                    ShareRentals = true,
                    ShareDeliveries = true,
                    AllowTargetWrite = false,
                    Note = "note",
                    IsActive = true,
                    IsDeleted = false,
                    Revision = invalid && mutation == TenantMutationKind.UpdateSharing ? 7 : 8
                },
                TenantMutationKind.DeleteSharing => new DataSharingPolicyDto
                {
                    Id = invalid ? Guid.NewGuid() : PolicyId,
                    SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    IsActive = false,
                    IsDeleted = true,
                    Revision = 8
                },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
            };

            if (staleRevision && payload is SyncEntityDto trackedPayload)
                trackedPayload.Revision = 7;

            return JsonResponse(payload);
        }

        private static HttpResponseMessage JsonResponse<T>(T payload)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload)
            };
    }

    private sealed class SnapshotFailureHandler(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/tenant-settings", request.RequestUri?.AbsolutePath);
            if (failure == "forbidden")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => Task.FromException(new HttpRequestException("response body lost"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
