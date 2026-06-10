using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityIssueViewModelTests
{
    [Fact]
    public async Task LoadAsync_WithInitialScanResult_DoesNotRescanAndLimitsDisplayedRows()
    {
        const int totalIssueCount = 620;
        const string issueCode = "slow_issue";
        var summary = new DataIntegrityIssueSummary
        {
            Code = issueCode,
            Title = "Slow integrity check",
            Severity = "Error",
            Area = "Operations",
            Count = totalIssueCount,
            HasDirectAction = true
        };
        var issues = Enumerable.Range(0, totalIssueCount)
            .Select(index => new DataIntegrityIssueDetail
            {
                Id = $"issue-{index:D4}",
                Code = issueCode,
                Title = summary.Title,
                Severity = index % 2 == 0 ? "Error" : "Warning",
                Area = summary.Area,
                CustomerName = $"Customer {index:D4}",
                Message = $"Integrity issue {index:D4}",
                DirectActionKind = DataIntegrityDirectActionKind.OpenRentalAsset
            })
            .ToList();
        var scanResult = new DataIntegrityScanResult(
            new DateTime(2026, 6, 11, 10, 0, 0),
            [summary],
            issues);
        var viewModel = new DataIntegrityIssueViewModel(null!, new SessionState(), issueCode, scanResult);

        await viewModel.LoadAsync();

        Assert.Equal(500, viewModel.Issues.Count);
        Assert.All(viewModel.Issues, issue => Assert.Equal(issueCode, issue.Code));
        Assert.Contains("620", viewModel.StatusMessage);
        Assert.Contains("500", viewModel.StatusMessage);
    }
}
