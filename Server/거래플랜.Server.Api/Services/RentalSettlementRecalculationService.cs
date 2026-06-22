using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class RentalSettlementRecalculationService
{
    private readonly AppDbContext _dbContext;

    public RentalSettlementRecalculationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<(Guid ProfileId, Guid? RunId)>> LoadRentalSettlementTargetsForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return [];

        var invoiceTargets = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                invoiceIds.Contains(invoice.Id) &&
                invoice.LinkedRentalBillingProfileId.HasValue &&
                invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(invoice => new
            {
                ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                RunId = invoice.LinkedRentalBillingRunId
            })
            .ToListAsync(cancellationToken);

        var transactionTargets = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value) &&
                transaction.LinkedRentalBillingProfileId.HasValue &&
                transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(transaction => new
            {
                ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
                RunId = transaction.LinkedRentalBillingRunId
            })
            .ToListAsync(cancellationToken);

        return invoiceTargets
            .Concat(transactionTargets)
            .Select(target => (target.ProfileId, target.RunId))
            .Where(target => target.ProfileId != Guid.Empty)
            .Distinct()
            .ToList();
    }

    public async Task DetachTransactionsFromInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return;

        var transactions = await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction =>
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .ToListAsync(cancellationToken);

        foreach (var transaction in transactions)
        {
            transaction.LinkedInvoiceId = null;
            transaction.LinkedInvoiceNumber = string.Empty;
            transaction.LinkedRentalBillingProfileId = null;
            transaction.LinkedRentalBillingRunId = null;
            transaction.SettlementAmount = 0m;
            if (string.Equals(transaction.TransactionKind, "전표수금", StringComparison.OrdinalIgnoreCase))
                transaction.TransactionKind = "일반수금";
            else if (string.Equals(transaction.TransactionKind, "전표지급", StringComparison.OrdinalIgnoreCase))
                transaction.TransactionKind = "일반지급";
            else if (string.Equals(transaction.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
                transaction.TransactionKind = "일반수금";
        }
    }

    public async Task MarkPaymentsDeletedForInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return;

        var payments = await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => invoiceIds.Contains(payment.InvoiceId))
            .ToListAsync(cancellationToken);
        foreach (var payment in payments)
        {
            payment.IsDeleted = true;
        }

        var paymentIds = payments
            .Select(payment => payment.Id)
            .Distinct()
            .ToList();
        if (paymentIds.Count == 0)
            return;

        var attachments = await _dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(attachment => paymentIds.Contains(attachment.PaymentId) && !attachment.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
        {
            attachment.IsDeleted = true;
        }
    }

    public async Task RecalculateRentalSettlementsAsync(
        IEnumerable<(Guid ProfileId, Guid? RunId)> targets,
        CancellationToken cancellationToken)
    {
        var distinctTargets = (targets ?? Enumerable.Empty<(Guid ProfileId, Guid? RunId)>())
            .Where(target => target.ProfileId != Guid.Empty)
            .Distinct()
            .ToList();

        foreach (var target in distinctTargets)
        {
            await RecalculateRentalSettlementAsync(target.ProfileId, target.RunId, cancellationToken);
        }
    }

    private async Task RecalculateRentalSettlementAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return;

        var settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, billingRunId, cancellationToken);
        var billedAmount = await ResolveBillingRunAmountAsync(profile, billingRunId, cancellationToken);
        profile.SettledAmount = settledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        profile.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
        profile.CompletionStatus = profile.OutstandingAmount <= 0m ? "완료" : "미완료";

        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
        {
            var runs = DeserializeBillingRuns(profile.BillingRunsJson);
            var run = runs.FirstOrDefault(current => current.RunId == billingRunId.Value);
            if (run is null)
            {
                run = await BuildSupplementalBillingRunAsync(
                    profile,
                    billingRunId.Value,
                    billedAmount,
                    settledAmount,
                    cancellationToken);
                if (run is not null)
                    runs.Add(run);
            }

            if (run is not null)
            {
                run.BilledAmount = billedAmount;
                run.SettledAmount = settledAmount;
                run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
                run.Status = profile.OutstandingAmount <= 0m
                    ? RentalBillingEvidenceStatusResolver.Completed
                    : RentalBillingEvidenceStatusResolver.IsManualStopStatus(run.Status)
                        ? run.Status.Trim()
                        : "청구중";
                run.SettledDate = settledAmount > 0m
                    ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
                    : null;
                if (profile.OutstandingAmount <= 0m)
                    profile.LastBilledDate = run.ScheduledDate;
                profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalBillingJsonOptions);
            }
        }

        if (profile.CompletionStatus == "완료")
        {
            profile.BillingStatus = "완료";
            profile.LastSettledDate = await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken);
        }
        else if (!string.Equals(profile.BillingStatus, "보류", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(profile.BillingStatus, "취소", StringComparison.OrdinalIgnoreCase))
        {
            profile.BillingStatus = "청구중";
            profile.LastSettledDate = settledAmount > 0m
                ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
                : null;
        }
    }

    private async Task<decimal> GetRentalSettledAmountCoreAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var transactionQuery = _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            transactionQuery = transactionQuery.Where(transaction => transaction.LinkedRentalBillingRunId == billingRunId.Value);

        var transactionSettledAmount = (await transactionQuery
            .Select(transaction => transaction.SettlementAmount)
            .ToListAsync(cancellationToken)).Sum();

        var directPaymentQuery =
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId)
            select new
            {
                payment.Amount,
                invoice.LinkedRentalBillingRunId
            };
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);

        var directPaymentSettledAmount = (await directPaymentQuery
            .Select(row => row.Amount)
            .ToListAsync(cancellationToken)).Sum();

        return transactionSettledAmount + directPaymentSettledAmount;
    }

    private async Task<DateOnly?> GetRentalLastSettledDateCoreAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var transactionQuery = _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId);
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            transactionQuery = transactionQuery.Where(transaction => transaction.LinkedRentalBillingRunId == billingRunId.Value);

        var transactionDates = await transactionQuery
            .Select(transaction => transaction.TransactionDate)
            .ToListAsync(cancellationToken);

        var directPaymentQuery =
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId)
            select new
            {
                payment.PaymentDate,
                invoice.LinkedRentalBillingRunId
            };
        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
            directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);

        var directPaymentDates = await directPaymentQuery
            .Select(row => row.PaymentDate)
            .ToListAsync(cancellationToken);

        return transactionDates
            .Concat(directPaymentDates)
            .OrderByDescending(date => date)
            .Cast<DateOnly?>()
            .FirstOrDefault();
    }

    private async Task<decimal> ResolveBillingRunAmountAsync(
        RentalBillingProfile profile,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        if (!billingRunId.HasValue || billingRunId.Value == Guid.Empty)
            return Math.Max(0m, profile.MonthlyAmount);

        var activeInvoiceAmount = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId == profile.Id &&
                invoice.LinkedRentalBillingRunId == billingRunId.Value)
            .OrderByDescending(invoice => invoice.UpdatedAtUtc)
            .ThenByDescending(invoice => invoice.Revision)
            .Select(invoice => (decimal?)invoice.TotalAmount)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeInvoiceAmount.HasValue && activeInvoiceAmount.Value > 0m)
            return activeInvoiceAmount.Value;

        var run = DeserializeBillingRuns(profile.BillingRunsJson)
            .FirstOrDefault(current => current.RunId == billingRunId.Value);
        return run is null ? Math.Max(0m, profile.MonthlyAmount) : Math.Max(0m, run.BilledAmount);
    }

    private async Task<RentalBillingRunSnapshot?> BuildSupplementalBillingRunAsync(
        RentalBillingProfile profile,
        Guid billingRunId,
        decimal billedAmount,
        decimal settledAmount,
        CancellationToken cancellationToken)
    {
        var evidence = await LoadRentalBillingRunEvidenceAsync(profile.Id, billingRunId, cancellationToken);
        if (evidence is null)
            return null;

        var scheduledDate = evidence.InvoiceDate
                            ?? evidence.LastSettlementDate
                            ?? profile.LastSettledDate
                            ?? profile.LastBilledDate
                            ?? profile.BillingStartDate
                            ?? profile.BillingAnchorDate
                            ?? profile.ContractStartDate
                            ?? profile.ContractDate
                            ?? DateOnly.FromDateTime(DateTime.Today);
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, profile.BillingAdvanceMode, scheduledDate);

        return new RentalBillingRunSnapshot
        {
            RunId = billingRunId,
            RunKey = $"{period.StartDate:yyyyMMdd}-{period.EndDate:yyyyMMdd}",
            ScheduledDate = scheduledDate,
            PeriodStartDate = period.StartDate,
            PeriodEndDate = period.EndDate,
            PeriodLabel = BuildBillingPeriodLabel(period.StartDate, period.EndDate),
            Status = settledAmount > 0m && Math.Max(0m, billedAmount - settledAmount) <= 0m
                ? RentalBillingEvidenceStatusResolver.Completed
                : "청구중",
            BilledAmount = Math.Max(0m, billedAmount),
            SettledAmount = Math.Max(0m, settledAmount),
            SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount),
            SettledDate = evidence.LastSettlementDate
        };
    }

    private async Task<RentalBillingRunEvidence?> LoadRentalBillingRunEvidenceAsync(
        Guid billingProfileId,
        Guid billingRunId,
        CancellationToken cancellationToken)
    {
        var invoiceEvidence = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId == billingRunId)
            .OrderByDescending(invoice => invoice.UpdatedAtUtc)
            .ThenByDescending(invoice => invoice.Revision)
            .Select(invoice => new
            {
                invoice.InvoiceDate
            })
            .FirstOrDefaultAsync(cancellationToken);

        var transactionRows = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                transaction.LinkedRentalBillingRunId == billingRunId)
            .Select(transaction => new
            {
                transaction.TransactionDate
            })
            .ToListAsync(cancellationToken);

        var directPaymentRows = await (
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  invoice.LinkedRentalBillingRunId == billingRunId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId)
            select new
            {
                payment.PaymentDate
            }).ToListAsync(cancellationToken);

        if (invoiceEvidence is null && transactionRows.Count == 0 && directPaymentRows.Count == 0)
            return null;

        var lastSettlementDate = transactionRows
            .Select(row => (DateOnly?)row.TransactionDate)
            .Concat(directPaymentRows.Select(row => (DateOnly?)row.PaymentDate))
            .OrderByDescending(date => date)
            .FirstOrDefault();

        return new RentalBillingRunEvidence(invoiceEvidence?.InvoiceDate, lastSettlementDate);
    }

    private static List<RentalBillingRunSnapshot> DeserializeBillingRuns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingRunSnapshot>>(json, RentalBillingJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildBillingPeriodLabel(DateOnly startDate, DateOnly endDate)
        => startDate == endDate || (startDate.Year == endDate.Year && startDate.Month == endDate.Month)
            ? $"{startDate:yyyy-MM}"
            : $"{startDate:yyyy-MM} ~ {endDate:yyyy-MM}";

    private static string DetermineRentalSettlementStatus(string? billingMethod, decimal settledAmount, decimal billedAmount)
    {
        if (settledAmount <= 0m)
            return GetPendingSettlementStatus(billingMethod);
        if (settledAmount < billedAmount)
            return "부분입금";
        return GetDisplaySettlementCompleteStatus(billingMethod);
    }

    private static string GetPendingSettlementStatus(string? billingMethod)
        => (billingMethod ?? string.Empty).Trim() switch
        {
            "카드" => "카드결제대기",
            "CMS" => "CMS대기",
            _ => "확인대기"
        };

    private static string GetDisplaySettlementCompleteStatus(string? billingMethod)
        => (billingMethod ?? string.Empty).Trim() switch
        {
            "카드" => "카드승인완료",
            _ => "입금확인"
        };

    private static readonly JsonSerializerOptions RentalBillingJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class RentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
    }

    private sealed record RentalBillingRunEvidence(DateOnly? InvoiceDate, DateOnly? LastSettlementDate);
}
