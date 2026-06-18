using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerContractContentDownloadTests
{
    [Fact]
    public async Task DownloadCustomerContractContentAsync_MapsMissingContentErrorToActionableMessage()
    {
        var contractId = Guid.NewGuid();
        var handler = new FileContentHandler((request, _) =>
        {
            Assert.Equal($"/customers/contracts/{contractId:D}/content", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new
                {
                    error = "contract_content_unavailable",
                    message = "계약서 파일 내용을 찾을 수 없습니다."
                })
            };
        });
        var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new SessionState());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.DownloadCustomerContractContentAsync(contractId));

        Assert.Contains("계약서 파일 내용을 찾을 수 없습니다", ex.Message);
        Assert.Contains("파일 저장소 무결성", ex.Message);
    }

    [Fact]
    public async Task DownloadPaymentAttachmentContentAsync_MapsMissingContentErrorToActionableMessage()
    {
        var attachmentId = Guid.NewGuid();
        var handler = new FileContentHandler((request, _) =>
        {
            Assert.Equal($"/payments/attachments/{attachmentId:D}/content", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new
                {
                    error = "attachment_content_unavailable",
                    message = "첨부 파일 내용을 찾을 수 없습니다."
                })
            };
        });
        var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new SessionState());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => api.DownloadPaymentAttachmentContentAsync(attachmentId));

        Assert.Contains("첨부 파일 내용을 찾을 수 없습니다", ex.Message);
        Assert.Contains("파일 저장소 무결성", ex.Message);
    }

    [Fact]
    public async Task EnsureContentAsync_DownloadsAndCachesMissingContractContentWithoutDirtyingRow()
    {
        PrepareAppRoot("georaeplan-contract-content-cache");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var content = "%PDF-1.7\ncontract evidence"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(content));
            var now = DateTime.UtcNow;

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "계약서 다운로드 거래처",
                NameMatchKey = "계약서 다운로드 거래처",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 10,
                IsDirty = false
            });
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = customerId,
                FileName = "contract.pdf",
                MimeType = "application/pdf",
                FileSize = content.LongLength,
                FileHash = hash,
                IsPrimary = true,
                UploadedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 20,
                IsDirty = false,
                FileContent = []
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new FileContentHandler((request, _) =>
            {
                Assert.Equal($"/customers/contracts/{contractId:D}/content", request.RequestUri?.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
            });
            var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, session);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var contract = await db.CustomerContracts.AsNoTracking().SingleAsync(current => current.Id == contractId);

            var resolved = await CustomerContractContentService.EnsureContentAsync(contract, local, session, api);

            Assert.Equal(content, resolved.FileContent);
            db.ChangeTracker.Clear();
            var stored = await db.CustomerContracts.AsNoTracking().SingleAsync(current => current.Id == contractId);
            Assert.Equal(content, stored.FileContent);
            Assert.False(stored.IsDirty);
            Assert.Equal(20, stored.Revision);
            Assert.Single(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullCustomerContractMetadata_PreservesCachedLocalPdfContent()
    {
        PrepareAppRoot("georaeplan-contract-content-preserve");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var content = "%PDF-1.7\ncached contract evidence"u8.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(content));
            var now = DateTime.UtcNow;

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "계약서 캐시 보존 거래처",
                NameMatchKey = "계약서 캐시 보존 거래처",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 10,
                IsDirty = false
            });
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = customerId,
                FileName = "contract.pdf",
                MimeType = "application/pdf",
                FileSize = content.LongLength,
                FileHash = hash,
                Description = "old",
                IsPrimary = true,
                UploadedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 20,
                IsDirty = false,
                FileContent = content
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    CurrentServerRevision = 21,
                    CustomerContracts =
                    {
                        new CustomerContractDto
                        {
                            Id = contractId,
                            CustomerId = customerId,
                            ContractType = "거래계약서",
                            FileName = "contract.pdf",
                            MimeType = "application/pdf",
                            FileSize = content.LongLength,
                            FileHash = hash,
                            Description = "metadata updated",
                            IsPrimary = true,
                            UploadedAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now.AddMinutes(1),
                            Revision = 21,
                            IsDeleted = false,
                            FileContent = []
                        }
                    }
                });

            db.ChangeTracker.Clear();
            var stored = await db.CustomerContracts.AsNoTracking().SingleAsync(current => current.Id == contractId);
            Assert.Equal(content, stored.FileContent);
            Assert.Equal("metadata updated", stored.Description);
            Assert.False(stored.IsDirty);
            Assert.Equal(21, stored.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "contract-content-admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session)
    {
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private static Task InvokeApplyPullAsync(SyncService sync, SyncPullResponse pull)
    {
        var method = typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull,
                0L,
                CancellationToken.None,
                false
            ])!;
    }

    private sealed class FileContentHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public FileContentHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request, cancellationToken));
        }
    }
}
