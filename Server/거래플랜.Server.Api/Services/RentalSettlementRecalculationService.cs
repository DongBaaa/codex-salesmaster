using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class RentalSettlementRecalculationService
{
    private const string BillingStatusPlanned = "\uC608\uC815";
    private const string SettlementStatusUnpaid = "\uBBF8\uC785\uAE08";
    private const string CompletionPending = "\uBBF8\uC644\uB8CC";

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
                !transaction.IsDeleted &&
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
            .Where(payment =>
                !payment.IsDeleted &&
                invoiceIds.Contains(payment.InvoiceId))
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
        var targetsByProfile = (targets ?? Enumerable.Empty<(Guid ProfileId, Guid? RunId)>())
            .Where(target => target.ProfileId != Guid.Empty)
            .Select(target => (
                target.ProfileId,
                RunId: !target.RunId.HasValue || target.RunId.Value == Guid.Empty
                    ? (Guid?)null
                    : target.RunId.Value))
            .Distinct()
            .GroupBy(target => target.ProfileId)
            .OrderBy(group => group.Key)
            .ToList();

        foreach (var profileTargets in targetsByProfile)
        {
            var recalculationPlan = await ResolveRentalSettlementRecalculationPlanAsync(
                profileTargets.Key,
                profileTargets.Select(target => target.RunId).ToList(),
                cancellationToken);
            if (!recalculationPlan.HasValidBillingRunsJson)
                continue;

            if (recalculationPlan.ActiveRunIds.Count > 0)
            {
                await RecalculateActiveRentalSettlementRunsAsync(
                    profileTargets.Key,
                    recalculationPlan.ActiveRunIds,
                    recalculationPlan.InactiveRequestedRunIds,
                    recalculationPlan.HasProfileLevelTarget,
                    cancellationToken);
            }
            else
            {
                await RemoveRequestedInactiveRentalBillingRunsAsync(
                    profileTargets.Key,
                    recalculationPlan.InactiveRequestedRunIds,
                    cancellationToken);
                if (await HasUnscopedRentalBillingFinancialEvidenceAsync(
                        profileTargets.Key,
                        cancellationToken))
                {
                    await RecalculateRentalSettlementAsync(
                        profileTargets.Key,
                        billingRunId: null,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    var refreshedTombstoneOnlyProfile =
                        await TryRefreshTombstoneOnlyRentalProfileAsync(
                            profileTargets.Key,
                            cancellationToken);
                    if (!refreshedTombstoneOnlyProfile)
                    {
                        await RecalculateRentalSettlementAsync(
                            profileTargets.Key,
                            recalculationPlan.RepresentativeRunId,
                            cancellationToken);
                    }
                }
            }
        }
    }

    private async Task<bool> HasUnscopedRentalBillingFinancialEvidenceAsync(
        Guid billingProfileId,
        CancellationToken cancellationToken)
    {
        if (await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().AnyAsync(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.TotalAmount > 0m &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                (!invoice.LinkedRentalBillingRunId.HasValue ||
                 invoice.LinkedRentalBillingRunId.Value == Guid.Empty),
                cancellationToken))
        {
            return true;
        }

        if (await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().AnyAsync(transaction =>
                !transaction.IsDeleted &&
                transaction.SettlementAmount > 0m &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                (!transaction.LinkedRentalBillingRunId.HasValue ||
                 transaction.LinkedRentalBillingRunId.Value == Guid.Empty),
                cancellationToken))
        {
            return true;
        }

        return await (
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  payment.Amount > 0m &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  (!invoice.LinkedRentalBillingRunId.HasValue ||
                   invoice.LinkedRentalBillingRunId.Value == Guid.Empty)
            select payment.Id).AnyAsync(cancellationToken);
    }

    private async Task<bool> TryRefreshTombstoneOnlyRentalProfileAsync(
        Guid billingProfileId,
        CancellationToken cancellationToken)
    {
        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return false;

        if (!TryDeserializeBillingRuns(profile.BillingRunsJson, out var runs))
            return false;
        if (!runs.Any(run => run.IsTombstoned) ||
            runs.Any(run => !run.IsTombstoned && run.RunId != Guid.Empty))
        {
            return false;
        }

        await RefreshRentalProfileAfterBillingRunEvidenceRemovalAsync(
            profile,
            runs,
            cancellationToken);
        return true;
    }

    private async Task<RentalSettlementRecalculationPlan> ResolveRentalSettlementRecalculationPlanAsync(
        Guid billingProfileId,
        IReadOnlyCollection<Guid?> requestedRunIds,
        CancellationToken cancellationToken)
    {
        var hasProfileLevelTarget = requestedRunIds.Any(runId => !runId.HasValue || runId.Value == Guid.Empty);
        var specificRunIds = requestedRunIds
            .Where(runId => runId.HasValue && runId.Value != Guid.Empty)
            .Select(runId => runId!.Value)
            .Distinct()
            .ToList();

        var billingRunsJson = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.Id == billingProfileId)
            .Select(profile => profile.BillingRunsJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (!TryDeserializeBillingRuns(billingRunsJson, out var billingRuns))
        {
            return new RentalSettlementRecalculationPlan(
                null,
                [],
                [],
                hasProfileLevelTarget,
                HasValidBillingRunsJson: false);
        }

        var canonicalRunMetadata = billingRuns
            .Where(run => run.RunId != Guid.Empty && !run.IsTombstoned)
            .GroupBy(run => run.RunId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(run => run.ScheduledDate)
                    .ThenByDescending(run => run.PeriodEndDate)
                    .ThenByDescending(run => run.RunId)
                    .First());

        var activeRunIds = await LoadActiveRentalBillingRunIdsAsync(
            billingProfileId,
            cancellationToken);
        var tombstonedRunIds = billingRuns
            .Where(run => run.IsTombstoned && run.RunId != Guid.Empty)
            .Select(run => run.RunId)
            .ToHashSet();
        activeRunIds.RemoveAll(tombstonedRunIds.Contains);
        var activeRunIdSet = activeRunIds.ToHashSet();
        var activeSpecificRuns = activeRunIds
            .Select(runId =>
            {
                canonicalRunMetadata.TryGetValue(runId, out var runMetadata);
                return new RentalSettlementTargetCandidate(
                    runId,
                    true,
                    runMetadata?.ScheduledDate ?? DateOnly.MinValue,
                    runMetadata?.PeriodEndDate ?? DateOnly.MinValue);
            })
            .ToList();
        var inactiveRequestedRunIds = specificRunIds
            .Where(runId => !activeRunIdSet.Contains(runId))
            .OrderBy(runId => runId)
            .ToList();
        if (activeSpecificRuns.Count > 0)
        {
            return new RentalSettlementRecalculationPlan(
                SelectCanonicalRentalSettlementRun(activeSpecificRuns).RunId,
                activeSpecificRuns
                    .Select(candidate => candidate.RunId)
                    .Distinct()
                    .OrderBy(runId => runId)
                    .ToList(),
                inactiveRequestedRunIds,
                hasProfileLevelTarget,
                HasValidBillingRunsJson: true);
        }

        if (hasProfileLevelTarget)
            return new RentalSettlementRecalculationPlan(null, [], inactiveRequestedRunIds, true, true);

        if (specificRunIds.Count == 0)
            return new RentalSettlementRecalculationPlan(null, [], [], false, true);

        var requestedCandidates = specificRunIds
            .Select(runId =>
            {
                canonicalRunMetadata.TryGetValue(runId, out var runMetadata);
                return new RentalSettlementTargetCandidate(
                    runId,
                    false,
                    runMetadata?.ScheduledDate ?? DateOnly.MinValue,
                    runMetadata?.PeriodEndDate ?? DateOnly.MinValue);
            })
            .ToList();

        return new RentalSettlementRecalculationPlan(
            SelectCanonicalRentalSettlementRun(requestedCandidates).RunId,
            [],
            inactiveRequestedRunIds,
            false,
            HasValidBillingRunsJson: true);
    }

    private async Task<List<Guid>> LoadActiveRentalBillingRunIdsAsync(
        Guid billingProfileId,
        CancellationToken cancellationToken)
    {
        var invoiceRunIds = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId.HasValue &&
                invoice.LinkedRentalBillingRunId.Value != Guid.Empty)
            .Select(invoice => invoice.LinkedRentalBillingRunId!.Value)
            .ToListAsync(cancellationToken);
        var transactionRunIds = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                transaction.LinkedRentalBillingRunId.HasValue &&
                transaction.LinkedRentalBillingRunId.Value != Guid.Empty)
            .Select(transaction => transaction.LinkedRentalBillingRunId!.Value)
            .ToListAsync(cancellationToken);
        var directPaymentRunIds = await (
            from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                on payment.InvoiceId equals invoice.Id
            where !payment.IsDeleted &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  invoice.LinkedRentalBillingRunId.HasValue &&
                  invoice.LinkedRentalBillingRunId.Value != Guid.Empty &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId &&
                      transaction.LinkedRentalBillingRunId == invoice.LinkedRentalBillingRunId)
            select invoice.LinkedRentalBillingRunId!.Value).ToListAsync(cancellationToken);

        return invoiceRunIds
            .Concat(transactionRunIds)
            .Concat(directPaymentRunIds)
            .Distinct()
            .OrderBy(runId => runId)
            .ToList();
    }

    private async Task RemoveRequestedInactiveRentalBillingRunsAsync(
        Guid billingProfileId,
        IReadOnlyCollection<Guid> inactiveRequestedRunIds,
        CancellationToken cancellationToken)
    {
        if (inactiveRequestedRunIds.Count == 0)
            return;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return;

        var inactiveRunIdSet = inactiveRequestedRunIds.ToHashSet();
        if (!TryDeserializeBillingRuns(profile.BillingRunsJson, out var runs))
            return;
        if (!ApplyRequestedInactiveRentalBillingRuns(profile, runs, inactiveRunIdSet))
            return;

        profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalBillingJsonOptions);
    }

    private static bool ApplyRequestedInactiveRentalBillingRuns(
        RentalBillingProfile profile,
        List<RentalBillingRunSnapshot> runs,
        IReadOnlySet<Guid> inactiveRunIds)
    {
        var inactiveNormalizedRunKeys = runs
            .Where(run => inactiveRunIds.Contains(run.RunId))
            .Select(run => RentalDuplicateNormalizer.NormalizeProfileKeyPart(run.RunKey))
            .Where(runKey => !string.IsNullOrWhiteSpace(runKey))
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        for (var index = runs.Count - 1; index >= 0; index--)
        {
            var run = runs[index];
            var belongsToInactiveIdentityGroup = inactiveRunIds.Contains(run.RunId) ||
                                                 (run.RunId == Guid.Empty &&
                                                  inactiveNormalizedRunKeys.Contains(
                                                      RentalDuplicateNormalizer.NormalizeProfileKeyPart(run.RunKey)));
            if (!belongsToInactiveIdentityGroup)
                continue;

            if (run.IsTombstoned)
            {
                changed |= ApplyCanonicalTombstoneState(run);
                continue;
            }

            if (RentalBillingEvidenceStatusResolver.IsManualStopStatus(run.Status))
            {
                run.BilledAmount = 0m;
                run.SettledAmount = 0m;
                run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, 0m, 0m);
                run.SettledDate = null;
            }
            else
            {
                runs.RemoveAt(index);
            }

            changed = true;
        }

        return changed;
    }

    private static RentalSettlementTargetCandidate SelectCanonicalRentalSettlementRun(
        IEnumerable<RentalSettlementTargetCandidate> candidates)
        => candidates
            .OrderByDescending(candidate => candidate.ScheduledDate)
            .ThenByDescending(candidate => candidate.PeriodEndDate)
            .ThenByDescending(candidate => candidate.RunId)
            .First();

    private async Task RecalculateActiveRentalSettlementRunsAsync(
        Guid billingProfileId,
        IReadOnlyCollection<Guid> activeRunIds,
        IReadOnlyCollection<Guid> inactiveRequestedRunIds,
        bool hasProfileLevelTarget,
        CancellationToken cancellationToken)
    {
        var distinctActiveRunIds = activeRunIds
            .Where(runId => runId != Guid.Empty)
            .Distinct()
            .OrderBy(runId => runId)
            .ToList();
        if (distinctActiveRunIds.Count == 0)
            return;

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return;
        var originalBillingStatus = profile.BillingStatus;
        var originalRequiresFollowUp = profile.RequiresFollowUp;
        if (!TryDeserializeBillingRuns(profile.BillingRunsJson, out var runs))
            return;
        ApplyRequestedInactiveRentalBillingRuns(
            profile,
            runs,
            inactiveRequestedRunIds.ToHashSet());
        var recalculatedRuns = new List<ActiveRentalSettlementResult>(distinctActiveRunIds.Count);
        DateOnly? resolvedLastBilledDate = null;
        foreach (var runId in distinctActiveRunIds)
        {
            if (runs.Any(current => current.RunId == runId && current.IsTombstoned))
                continue;

            var settledAmount = await GetRentalSettledAmountCoreAsync(
                billingProfileId,
                runId,
                cancellationToken);
            var billedAmount = await ResolveBillingRunAmountAsync(
                profile,
                runId,
                cancellationToken);
            var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
            var run = runs.FirstOrDefault(current => current.RunId == runId);
            if (run is null)
            {
                run = await BuildSupplementalBillingRunAsync(
                    profile,
                    runId,
                    billedAmount,
                    settledAmount,
                    cancellationToken);
                if (run is not null)
                    runs.Add(run);
            }

            if (run is null)
                continue;

            if (RequiresAuthoritativeScheduleRepair(run))
            {
                var repairedSchedule = await BuildSupplementalBillingRunAsync(
                    profile,
                    runId,
                    billedAmount,
                    settledAmount,
                    cancellationToken);
                if (repairedSchedule is not null)
                    ApplyAuthoritativeSchedule(run, repairedSchedule);
            }

            run.BilledAmount = billedAmount;
            run.SettledAmount = settledAmount;
            run.SettlementStatus = DetermineRentalSettlementStatus(
                profile.BillingMethod,
                settledAmount,
                billedAmount);
            run.Status = ResolveRecalculatedRunStatus(
                run.Status,
                outstandingAmount,
                billedAmount);
            run.SettledDate = settledAmount > 0m
                ? await GetRentalLastSettledDateCoreAsync(
                    billingProfileId,
                    runId,
                    cancellationToken)
                : null;
            resolvedLastBilledDate = await ResolveAuthoritativeLastBilledDateAsync(
                profile.Id,
                runId,
                run.ScheduledDate,
                resolvedLastBilledDate,
                cancellationToken);
            recalculatedRuns.Add(new ActiveRentalSettlementResult(
                run,
                billedAmount,
                settledAmount,
                outstandingAmount));
        }

        if (recalculatedRuns.Count == 0)
        {
            await RefreshRentalProfileAfterBillingRunEvidenceRemovalAsync(
                profile,
                runs,
                cancellationToken);
            return;
        }

        var representative = recalculatedRuns
            .OrderByDescending(result => result.Run.ScheduledDate)
            .ThenByDescending(result => result.Run.PeriodEndDate)
            .ThenByDescending(result => result.Run.RunId)
            .First();
        profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalBillingJsonOptions);
        var topSettledAmount = representative.SettledAmount;
        var topBilledAmount = representative.BilledAmount;
        var topOutstandingAmount = representative.OutstandingAmount;
        var topSettlementStatus = representative.Run.SettlementStatus;
        var topLastSettledDate = representative.Run.SettledDate;
        if (hasProfileLevelTarget)
        {
            topSettledAmount = await GetRentalSettledAmountCoreAsync(
                billingProfileId,
                null,
                cancellationToken);
            topBilledAmount = await ResolveBillingRunAmountAsync(
                profile,
                null,
                cancellationToken);
            topOutstandingAmount = Math.Max(0m, topBilledAmount - topSettledAmount);
            topSettlementStatus = DetermineRentalSettlementStatus(
                profile.BillingMethod,
                topSettledAmount,
                topBilledAmount);
            topLastSettledDate = topSettledAmount > 0m
                ? await GetRentalLastSettledDateCoreAsync(
                    billingProfileId,
                    null,
                    cancellationToken)
                : null;
        }

        profile.SettledAmount = topSettledAmount;
        profile.OutstandingAmount = topOutstandingAmount;
        profile.SettlementStatus = topSettlementStatus;
        profile.CompletionStatus = topOutstandingAmount <= 0m
            ? RentalBillingEvidenceStatusResolver.Completed
            : CompletionPending;
        profile.LastBilledDate = resolvedLastBilledDate;
        profile.LastSettledDate = topLastSettledDate;
        if (RentalBillingEvidenceStatusResolver.IsManualStopStatus(originalBillingStatus))
            profile.BillingStatus = originalBillingStatus.Trim();
        else if (profile.CompletionStatus == RentalBillingEvidenceStatusResolver.Completed)
            profile.BillingStatus = RentalBillingEvidenceStatusResolver.Completed;
        else
            profile.BillingStatus = RentalBillingEvidenceStatusResolver.InProgress;

        var derivedRequiresFollowUp = recalculatedRuns.Any(result => result.OutstandingAmount > 0m) ||
                                      (hasProfileLevelTarget && topOutstandingAmount > 0m);
        profile.RequiresFollowUp = ResolveManualStopFollowUp(
            profile.BillingStatus,
            originalRequiresFollowUp,
            derivedRequiresFollowUp);
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
        if (!TryDeserializeBillingRuns(profile.BillingRunsJson, out var runs))
            return;

        var originalBillingStatus = profile.BillingStatus;
        var originalRequiresFollowUp = profile.RequiresFollowUp;

        if (billingRunId.HasValue &&
            billingRunId.Value != Guid.Empty &&
            !await HasActiveRentalBillingRunFinancialEvidenceAsync(billingProfileId, billingRunId.Value, cancellationToken))
        {
            ApplyRequestedInactiveRentalBillingRuns(
                profile,
                runs,
                new HashSet<Guid> { billingRunId.Value });
            await RefreshRentalProfileAfterBillingRunEvidenceRemovalAsync(profile, runs, cancellationToken);
            return;
        }

        var settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, billingRunId, cancellationToken);
        var billedAmount = await ResolveBillingRunAmountAsync(profile, billingRunId, cancellationToken);
        profile.SettledAmount = settledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        profile.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
        profile.CompletionStatus = profile.OutstandingAmount <= 0m ? "완료" : "미완료";

        if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
        {
            var run = runs.FirstOrDefault(current => current.RunId == billingRunId.Value);
            if (run?.IsTombstoned == true)
            {
                ApplyCanonicalTombstoneState(run);
                await RefreshRentalProfileAfterBillingRunEvidenceRemovalAsync(
                    profile,
                    runs,
                    cancellationToken);
                return;
            }

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
                if (RequiresAuthoritativeScheduleRepair(run))
                {
                    var repairedSchedule = await BuildSupplementalBillingRunAsync(
                        profile,
                        billingRunId.Value,
                        billedAmount,
                        settledAmount,
                        cancellationToken);
                    if (repairedSchedule is not null)
                        ApplyAuthoritativeSchedule(run, repairedSchedule);
                }

                run.BilledAmount = billedAmount;
                run.SettledAmount = settledAmount;
                run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
                run.Status = ResolveRecalculatedRunStatus(
                    run.Status,
                    profile.OutstandingAmount,
                    billedAmount);
                run.SettledDate = settledAmount > 0m
                    ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
                    : null;
                profile.LastBilledDate = await ResolveAuthoritativeLastBilledDateAsync(
                    profile.Id,
                    billingRunId.Value,
                    run.ScheduledDate,
                    profile.LastBilledDate,
                    cancellationToken);
                profile.BillingRunsJson = JsonSerializer.Serialize(runs, RentalBillingJsonOptions);
            }
        }

        profile.LastSettledDate = settledAmount > 0m
            ? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, cancellationToken)
            : null;
        if (RentalBillingEvidenceStatusResolver.IsManualStopStatus(originalBillingStatus))
            profile.BillingStatus = originalBillingStatus.Trim();
        else if (profile.CompletionStatus == RentalBillingEvidenceStatusResolver.Completed)
            profile.BillingStatus = RentalBillingEvidenceStatusResolver.Completed;
        else
            profile.BillingStatus = RentalBillingEvidenceStatusResolver.InProgress;
        profile.RequiresFollowUp = ResolveManualStopFollowUp(
            profile.BillingStatus,
            originalRequiresFollowUp,
            profile.OutstandingAmount > 0m);
    }

    private async Task<bool> HasActiveRentalBillingRunFinancialEvidenceAsync(
        Guid billingProfileId,
        Guid billingRunId,
        CancellationToken cancellationToken)
    {
        if (billingProfileId == Guid.Empty || billingRunId == Guid.Empty)
            return false;

        if (await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().AnyAsync(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId == billingRunId,
                cancellationToken))
        {
            return true;
        }

        if (await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().AnyAsync(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId &&
                transaction.LinkedRentalBillingRunId == billingRunId,
                cancellationToken))
        {
            return true;
        }

        return await (
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
                      transaction.LinkedRentalBillingProfileId == billingProfileId &&
                      transaction.LinkedRentalBillingRunId == billingRunId)
            select payment.Id).AnyAsync(cancellationToken);
    }

    private async Task RefreshRentalProfileAfterBillingRunEvidenceRemovalAsync(
        RentalBillingProfile profile,
        List<RentalBillingRunSnapshot> remainingRuns,
        CancellationToken cancellationToken)
    {
        var originalBillingStatus = profile.BillingStatus;
        var originalRequiresFollowUp = profile.RequiresFollowUp;
        var activeRuns = remainingRuns
            .Where(run => run.RunId != Guid.Empty && !run.IsTombstoned)
            .OrderByDescending(run => run.ScheduledDate)
            .ThenByDescending(run => run.PeriodEndDate)
            .ThenByDescending(run => run.RunId)
            .ToList();

        if (activeRuns.Count == 0)
        {
            foreach (var tombstone in remainingRuns.Where(run => run.IsTombstoned))
                ApplyCanonicalTombstoneState(tombstone);
            profile.BillingRunsJson = JsonSerializer.Serialize(remainingRuns, RentalBillingJsonOptions);
            profile.BillingStatus = RentalBillingEvidenceStatusResolver.IsManualStopStatus(originalBillingStatus)
                ? originalBillingStatus.Trim()
                : BillingStatusPlanned;
            profile.SettlementStatus = SettlementStatusUnpaid;
            profile.CompletionStatus = CompletionPending;
            profile.SettledAmount = 0m;
            profile.OutstandingAmount = 0m;
            profile.LastBilledDate = null;
            profile.LastSettledDate = null;
            profile.RequiresFollowUp = ResolveManualStopFollowUp(
                profile.BillingStatus,
                originalRequiresFollowUp,
                derivedRequiresFollowUp: false);
            return;
        }

        var activeRunIds = new HashSet<Guid>();
        foreach (var run in activeRuns)
        {
            var hasEvidence = await HasActiveRentalBillingRunFinancialEvidenceAsync(profile.Id, run.RunId, cancellationToken);
            if (hasEvidence)
                activeRunIds.Add(run.RunId);

            var billedAmount = hasEvidence
                ? await ResolveBillingRunAmountAsync(profile, run.RunId, cancellationToken)
                : 0m;
            var settledAmount = await GetRentalSettledAmountCoreAsync(profile.Id, run.RunId, cancellationToken);
            var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
            run.BilledAmount = billedAmount;
            run.SettledAmount = settledAmount;
            run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
            run.Status = ResolveRecalculatedRunStatus(
                run.Status,
                outstandingAmount,
                billedAmount);
            run.SettledDate = settledAmount > 0m
                ? await GetRentalLastSettledDateCoreAsync(profile.Id, run.RunId, cancellationToken)
                : null;
        }

        var representativeRun = activeRuns.FirstOrDefault(run => activeRunIds.Contains(run.RunId)) ?? activeRuns.First();
        var representativeBilledAmount = Math.Max(0m, representativeRun.BilledAmount);
        var representativeSettledAmount = Math.Max(0m, representativeRun.SettledAmount);
        var representativeOutstandingAmount = Math.Max(0m, representativeBilledAmount - representativeSettledAmount);
        foreach (var tombstone in remainingRuns.Where(run => run.IsTombstoned))
            ApplyCanonicalTombstoneState(tombstone);
        profile.BillingRunsJson = JsonSerializer.Serialize(remainingRuns, RentalBillingJsonOptions);
        var profileStatusSeed = RentalBillingEvidenceStatusResolver.IsManualStopStatus(originalBillingStatus)
            ? originalBillingStatus
            : representativeRun.Status;
        profile.BillingStatus = ResolveRecalculatedRunStatus(
            profileStatusSeed,
            representativeOutstandingAmount,
            representativeBilledAmount);
        profile.SettlementStatus = representativeRun.SettlementStatus;
        profile.CompletionStatus = representativeOutstandingAmount <= 0m && representativeBilledAmount > 0m
            ? RentalBillingEvidenceStatusResolver.Completed
            : CompletionPending;
        profile.SettledAmount = representativeSettledAmount;
        profile.OutstandingAmount = representativeOutstandingAmount;
        profile.LastBilledDate = activeRuns
            .Where(run => activeRunIds.Contains(run.RunId))
            .Select(run => (DateOnly?)run.ScheduledDate)
            .OrderByDescending(date => date)
            .FirstOrDefault();
        profile.LastSettledDate = activeRuns
            .Select(run => run.SettledDate)
            .OrderByDescending(date => date)
            .FirstOrDefault();
        var derivedRequiresFollowUp = activeRuns.Any(run =>
            Math.Max(0m, run.BilledAmount - Math.Max(0m, run.SettledAmount)) > 0m &&
            (activeRunIds.Contains(run.RunId) ||
             string.Equals(run.Status, RentalBillingEvidenceStatusResolver.InProgress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(run.Status, RentalBillingEvidenceStatusResolver.PartiallySettled, StringComparison.OrdinalIgnoreCase)));
        profile.RequiresFollowUp = ResolveManualStopFollowUp(
            profile.BillingStatus,
            originalRequiresFollowUp,
            derivedRequiresFollowUp);
    }

    private static bool ResolveManualStopFollowUp(
        string? billingStatus,
        bool originalRequiresFollowUp,
        bool derivedRequiresFollowUp)
    {
        if (string.Equals(
                billingStatus?.Trim(),
                RentalBillingEvidenceStatusResolver.OnHold,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(
                billingStatus?.Trim(),
                RentalBillingEvidenceStatusResolver.Cancelled,
                StringComparison.OrdinalIgnoreCase))
        {
            return originalRequiresFollowUp;
        }

        return derivedRequiresFollowUp;
    }

    private static string ResolveRecalculatedRunStatus(
        string? currentStatus,
        decimal outstandingAmount,
        decimal billedAmount)
    {
        if (billedAmount > 0m && outstandingAmount <= 0m)
            return RentalBillingEvidenceStatusResolver.Completed;

        if (billedAmount <= 0m &&
            string.Equals(
                currentStatus?.Trim(),
                BillingStatusPlanned,
                StringComparison.OrdinalIgnoreCase))
        {
            return BillingStatusPlanned;
        }

        return RentalBillingEvidenceStatusResolver.IsManualStopStatus(currentStatus)
            ? currentStatus!.Trim()
            : RentalBillingEvidenceStatusResolver.InProgress;
    }

    private async Task<decimal> GetRentalSettledAmountCoreAsync(
        Guid billingProfileId,
        Guid? billingRunId,
        CancellationToken cancellationToken)
    {
        var transactionQuery = _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.SettlementAmount > 0m &&
                transaction.LinkedRentalBillingProfileId == billingProfileId);
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
                  payment.Amount > 0m &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.SettlementAmount > 0m &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId &&
                      (transaction.LinkedRentalBillingRunId == invoice.LinkedRentalBillingRunId ||
                       ((!transaction.LinkedRentalBillingRunId.HasValue ||
                         transaction.LinkedRentalBillingRunId.Value == Guid.Empty) &&
                        (!invoice.LinkedRentalBillingRunId.HasValue ||
                         invoice.LinkedRentalBillingRunId.Value == Guid.Empty))))
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
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.SettlementAmount > 0m &&
                transaction.LinkedRentalBillingProfileId == billingProfileId);
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
                  payment.Amount > 0m &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.SettlementAmount > 0m &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId &&
                      (transaction.LinkedRentalBillingRunId == invoice.LinkedRentalBillingRunId ||
                       ((!transaction.LinkedRentalBillingRunId.HasValue ||
                         transaction.LinkedRentalBillingRunId.Value == Guid.Empty) &&
                        (!invoice.LinkedRentalBillingRunId.HasValue ||
                         invoice.LinkedRentalBillingRunId.Value == Guid.Empty))))
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

        if (RentalBillingRunTombstonePolicy.LookupForFinancialRecalculation(
                profile.BillingRunsJson,
                billingRunId.Value).IsTombstoned)
        {
            return 0m;
        }

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

        if (!TryDeserializeBillingRuns(profile.BillingRunsJson, out var runs))
            return 0m;
        var run = runs.FirstOrDefault(current => current.RunId == billingRunId.Value);
        return run is null ? Math.Max(0m, profile.MonthlyAmount) : Math.Max(0m, run.BilledAmount);
    }

    private async Task<RentalBillingRunSnapshot?> BuildSupplementalBillingRunAsync(
        RentalBillingProfile profile,
        Guid billingRunId,
        decimal billedAmount,
        decimal settledAmount,
        CancellationToken cancellationToken)
    {
        var lookup = RentalBillingRunTombstonePolicy.LookupForFinancialRecalculation(
            profile.BillingRunsJson,
            billingRunId);
        if (!lookup.IsValid || lookup.IsTombstoned)
            return null;

        var evidence = await LoadRentalBillingRunEvidenceAsync(profile.Id, billingRunId, cancellationToken);
        if (evidence is null)
            return null;

        var referenceDate = evidence.InvoiceDate
                            ?? evidence.LastSettlementDate
                            ?? profile.LastSettledDate
                            ?? profile.LastBilledDate
                            ?? profile.BillingStartDate
                            ?? profile.BillingAnchorDate
                            ?? profile.ContractStartDate
                            ?? profile.ContractDate
                            ?? DateOnly.FromDateTime(DateTime.Today);
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
        var anchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            cycleMonths,
            profile.BillingAnchorMonth,
            profile.BillingAnchorDate,
            profile.BillingStartDate,
            profile.ContractStartDate,
            profile.ContractDate,
            profile.LastBilledDate,
            referenceDate);
        var scheduledDate = RentalBillingScheduleRules.ResolveConfiguredBillingDate(
            profile.BillingDay,
            profile.BillingDayMode,
            cycleMonths,
            anchorMonth,
            referenceDate,
            firstBillingDate: null,
            cycleAnchorDate: RentalBillingScheduleRules.ResolveCycleAnchorDate(
                anchorMonth,
                referenceDate,
                profile.BillingAnchorDate,
                profile.BillingStartDate,
                profile.ContractStartDate,
                profile.ContractDate));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, profile.BillingAdvanceMode, scheduledDate);

        return new RentalBillingRunSnapshot
        {
            RunId = billingRunId,
            RunKey = $"{period.StartDate:yyyyMMdd}-{period.EndDate:yyyyMMdd}",
            ScheduledDate = scheduledDate,
            PeriodStartDate = period.StartDate,
            PeriodEndDate = period.EndDate,
            CycleMonths = cycleMonths,
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

    private static bool RequiresAuthoritativeScheduleRepair(RentalBillingRunSnapshot run)
        => run.ScheduledDate == DateOnly.MinValue ||
           run.PeriodStartDate == DateOnly.MinValue ||
           run.PeriodEndDate == DateOnly.MinValue ||
           run.PeriodEndDate < run.PeriodStartDate ||
           run.CycleMonths <= 0 ||
           string.IsNullOrWhiteSpace(run.RunKey) ||
           string.IsNullOrWhiteSpace(run.PeriodLabel);

    private static void ApplyAuthoritativeSchedule(
        RentalBillingRunSnapshot target,
        RentalBillingRunSnapshot source)
    {
        target.RunKey = source.RunKey;
        target.ScheduledDate = source.ScheduledDate;
        target.PeriodStartDate = source.PeriodStartDate;
        target.PeriodEndDate = source.PeriodEndDate;
        target.CycleMonths = source.CycleMonths;
        target.PeriodLabel = source.PeriodLabel;
    }

    private async Task<DateOnly?> ResolveAuthoritativeLastBilledDateAsync(
        Guid billingProfileId,
        Guid billingRunId,
        DateOnly scheduledDate,
        DateOnly? currentLastBilledDate,
        CancellationToken cancellationToken)
    {
        var activeInvoiceDates = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.TotalAmount > 0m &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId == billingRunId)
            .Select(invoice => invoice.InvoiceDate)
            .ToListAsync(cancellationToken);

        var candidates = activeInvoiceDates
            .Select(date => (DateOnly?)date)
            .ToList();
        if (scheduledDate != DateOnly.MinValue)
            candidates.Add(scheduledDate);
        if (currentLastBilledDate.HasValue)
            candidates.Add(currentLastBilledDate.Value);

        return candidates
            .OrderByDescending(date => date)
            .FirstOrDefault();
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
                invoice.TotalAmount > 0m &&
                invoice.LinkedRentalBillingProfileId == billingProfileId &&
                invoice.LinkedRentalBillingRunId == billingRunId)
            .OrderByDescending(invoice => invoice.UpdatedAtUtc)
            .ThenByDescending(invoice => invoice.Revision)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.InvoiceDate
            })
            .FirstOrDefaultAsync(cancellationToken);

        DateOnly? invoiceBillingReferenceDate = null;
        if (invoiceEvidence is not null)
        {
            var activeLineItemNames = await _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                .Where(line =>
                    !line.IsDeleted &&
                    line.InvoiceId == invoiceEvidence.Id)
                .Select(line => line.ItemNameOriginal)
                .ToListAsync(cancellationToken);
            invoiceBillingReferenceDate = ResolveInvoiceBillingReferenceDate(
                invoiceEvidence.InvoiceDate,
                activeLineItemNames);
        }

        var transactionRows = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.SettlementAmount > 0m &&
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
                  payment.Amount > 0m &&
                  !invoice.IsDeleted &&
                  invoice.IsLatestVersion &&
                  invoice.LinkedRentalBillingProfileId == billingProfileId &&
                  invoice.LinkedRentalBillingRunId == billingRunId &&
                  !_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
                      !transaction.IsDeleted &&
                      transaction.SettlementAmount > 0m &&
                      transaction.Id == payment.Id &&
                      transaction.LinkedRentalBillingProfileId == billingProfileId &&
                      transaction.LinkedRentalBillingRunId == billingRunId)
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

        return new RentalBillingRunEvidence(
            invoiceBillingReferenceDate ?? invoiceEvidence?.InvoiceDate,
            lastSettlementDate);
    }

    private static DateOnly? ResolveInvoiceBillingReferenceDate(
        DateOnly invoiceDate,
        IEnumerable<string?> lineItemNames)
    {
        var billingMonths = (lineItemNames ?? Enumerable.Empty<string?>())
            .Select(lineItemName =>
                TryExtractBracketedBillingMonth(lineItemName, out var billingMonth)
                    ? billingMonth
                    : 0)
            .Where(billingMonth => billingMonth is >= 1 and <= 12)
            .Distinct()
            .ToList();
        if (billingMonths.Count != 1)
            return null;

        var billingMonth = billingMonths[0];
        var billingYear = billingMonth > invoiceDate.Month
            ? invoiceDate.Year - 1
            : invoiceDate.Year;
        return new DateOnly(billingYear, billingMonth, 1);
    }

    private static bool TryExtractBracketedBillingMonth(
        string? text,
        out int billingMonth)
    {
        billingMonth = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var openIndex = text.IndexOf('[', searchStart);
            if (openIndex < 0)
                return false;

            var suffixIndex = text.IndexOf("월]", openIndex, StringComparison.Ordinal);
            if (suffixIndex < 0)
                return false;

            var rawMonth = text.Substring(openIndex + 1, suffixIndex - openIndex - 1).Trim();
            if (int.TryParse(
                    rawMonth,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedMonth) &&
                parsedMonth is >= 1 and <= 12)
            {
                billingMonth = parsedMonth;
                return true;
            }

            searchStart = suffixIndex + 2;
        }

        return false;
    }

    private static bool TryDeserializeBillingRuns(
        string? json,
        out List<RentalBillingRunSnapshot> runs)
    {
        runs = [];
        var validation = RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(json);
        if (!validation.IsValid)
            return false;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            runs = JsonSerializer.Deserialize<List<RentalBillingRunSnapshot>>(
                       json,
                       RentalBillingJsonOptions) ?? [];
            return true;
        }
        catch
        {
            runs = [];
            return false;
        }
    }

    private static string BuildBillingPeriodLabel(DateOnly startDate, DateOnly endDate)
        => startDate == endDate || (startDate.Year == endDate.Year && startDate.Month == endDate.Month)
            ? $"{startDate:yyyy-MM}"
            : $"{startDate:yyyy-MM} ~ {endDate:yyyy-MM}";

    private static bool ApplyCanonicalTombstoneState(RentalBillingRunSnapshot run)
    {
        if (!run.IsTombstoned)
            return false;

        var changed =
            !string.Equals(
                run.Status,
                RentalBillingEvidenceStatusResolver.Cancelled,
                StringComparison.OrdinalIgnoreCase) ||
            run.BilledAmount != 0m ||
            run.SettledAmount != 0m ||
            !string.Equals(
                run.SettlementStatus,
                SettlementStatusUnpaid,
                StringComparison.OrdinalIgnoreCase) ||
            run.SettledDate.HasValue;
        run.Status = RentalBillingEvidenceStatusResolver.Cancelled;
        run.BilledAmount = 0m;
        run.SettledAmount = 0m;
        run.SettlementStatus = SettlementStatusUnpaid;
        run.SettledDate = null;
        return changed;
    }

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
        public int CycleMonths { get; set; } = 1;
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
        public bool IsTombstoned { get; set; }
        public DateTime? TombstonedAtUtc { get; set; }
        public string TombstonedByUsername { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed record RentalBillingRunEvidence(DateOnly? InvoiceDate, DateOnly? LastSettlementDate);

    private sealed record RentalSettlementTargetCandidate(
        Guid RunId,
        bool HasActiveEvidence,
        DateOnly ScheduledDate,
        DateOnly PeriodEndDate);

    private sealed record RentalSettlementRecalculationPlan(
        Guid? RepresentativeRunId,
        IReadOnlyCollection<Guid> ActiveRunIds,
        IReadOnlyCollection<Guid> InactiveRequestedRunIds,
        bool HasProfileLevelTarget,
        bool HasValidBillingRunsJson);

    private sealed record ActiveRentalSettlementResult(
        RentalBillingRunSnapshot Run,
        decimal BilledAmount,
        decimal SettledAmount,
        decimal OutstandingAmount);
}
