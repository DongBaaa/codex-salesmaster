using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerContractSummaryTests
{
    [Theory]
    [InlineData("contract.pdf", 24, false, false, true)]
    [InlineData("contract.pdf", 24, true, false, true)]
    [InlineData("contract.pdf", 0, false, false, false)]
    [InlineData("PDF 미등록", 24, false, false, false)]
    [InlineData(" ", 24, false, false, false)]
    [InlineData("contract.pdf", 24, true, true, false)]
    public void RegistrationStatus_UsesRegisteredMetadataIndependentlyOfLocalCache(
        string fileName, long size, bool cached, bool deleted, bool registered)
    {
        var contract = new LocalCustomerContract
        {
            FileName = fileName, FileSize = size, FileContent = cached ? new byte[24] : [], IsDeleted = deleted
        };
        Assert.Equal(registered, contract.HasRegisteredPdf);
        Assert.Equal(registered ? "파일등록" : "초안", contract.FileRegistrationStatus);
    }

    [Fact]
    public async Task Summary_DistinguishesDraftsRemoteFilesAndMissingDatesWithinOfficeScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var own = Customer(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup);
        var empty = Customer(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup);
        var other = Customer(OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld);
        db.Customers.AddRange(own, empty, other);
        var today = DateOnly.FromDateTime(DateTime.Today);
        db.CustomerContracts.AddRange(
            Contract(own.Id, "PDF 미등록", 0, null),
            Contract(own.Id, "remote.pdf", 24, today.AddDays(90)),
            Contract(own.Id, "expired.pdf", 24, today.AddDays(-1)),
            Contract(own.Id, "undated.pdf", 24, null),
            Contract(own.Id, "deleted.pdf", 24, today.AddDays(5), deleted:true),
            Contract(other.Id, "other-office.pdf", 24, today.AddDays(-1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var session = OfficeSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

        var summaries = await local.GetCustomerContractSummaryMapAsync(session);
        var alerts = await local.GetCustomerContractAlertsAsync(session);
        Assert.Equal(2, summaries.Count);
        Assert.False(summaries.ContainsKey(other.Id));
        Assert.Equal(0, summaries[empty.Id].ContractCount);
        var summary = summaries[own.Id];
        Assert.Equal(4, summary.ContractCount);
        Assert.Equal(3, summary.RegisteredFileCount);
        Assert.Equal(1, summary.DraftCount);
        Assert.Equal(2, summary.MissingExpireDateCount);
        Assert.True(summary.HasExpiredContract);
        Assert.Equal(today.AddDays(-1), summary.NearestExpireDate);
        Assert.Equal(own.Id, Assert.Single(alerts).CustomerId);
        Assert.Empty(db.ChangeTracker.Entries());

        var row = new EnvironmentCustomerRow(own, contractSummary:summary);
        Assert.True(row.HasContract);
        Assert.Equal("PDF 3건 · 초안 1건", row.ContractPresenceText);
        Assert.Equal("만료 계약 있음 · 만료일 미입력 2건", row.ContractStatusText);
        var text = CustomerContractSummaryFormatter.Format(summaries.Values, alerts, 30);
        Assert.Equal("PDF 등록 1곳 · 초안 1건 · 만료일 미입력 2건 · 만료 1건 · 30일 내 0건", text);
    }

    [Fact]
    public void DraftOnlyWithoutExpiry_DoesNotClaimFilePossessionOrNoUpcomingAlerts()
    {
        var summary = new CustomerContractSummaryItem { ContractCount = 1, RegisteredFileCount = 0, MissingExpireDateCount = 1 };
        var row = new EnvironmentCustomerRow(new LocalCustomer(), contractSummary:summary);
        Assert.False(row.HasContract);
        Assert.Equal("초안 1건", row.ContractPresenceText);
        Assert.Equal("만료일 미입력", row.ContractStatusText);
        Assert.Equal("PDF 등록 0곳 · 초안 1건 · 만료일 미입력 1건",
            CustomerContractSummaryFormatter.Format([summary], [], 30));
    }

    [Fact]
    public void RegisteredFilesWithKnownFutureExpiry_CanReportNoUpcomingAlerts()
    {
        var summaries = new[] { new CustomerContractSummaryItem { ContractCount = 2, RegisteredFileCount = 2, NearestExpireDate = DateOnly.FromDateTime(DateTime.Today).AddDays(90) } };
        Assert.Equal("PDF 등록 1곳 · 임박 알림 없음", CustomerContractSummaryFormatter.Format(summaries, [], 30));
        Assert.Equal("등록된 계약 정보가 없습니다.", CustomerContractSummaryFormatter.Format([], [], 30));
    }

    private static LocalCustomer Customer(string office, string tenant) => new()
    {
        Id = Guid.NewGuid(), NameOriginal = "계약 요약 검증 " + office, NameMatchKey = Guid.NewGuid().ToString("N"),
        TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, IsDirty = false
    };

    private static LocalCustomerContract Contract(Guid customer, string name, long size, DateOnly? expiry, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(), CustomerId = customer, FileName = name, FileSize = size, ExpireDate = expiry,
        IsDeleted = deleted, IsDirty = false, FileContent = []
    };

    private static SessionState OfficeSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "contract-summary-office", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }
}
