using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RentalBillingRunTombstoneSafeguardTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RentalBillingRunTombstoneSafeguardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var dbContext = CreateDbContext(CreateAdminUser());
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public void Lookup_DistinguishesEmptyActiveTombstonedAndInvalidMarkers()
    {
        var runId = Guid.NewGuid();
        var tombstonedAtUtc = new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc);

        Assert.Equal(
            RentalBillingRunLookupStatus.NotFound,
            RentalBillingRunTombstonePolicy.Lookup(null, runId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.NotFound,
            RentalBillingRunTombstonePolicy.Lookup("[]", runId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.Active,
            RentalBillingRunTombstonePolicy.Lookup(
                JsonSerializer.Serialize(new[] { new { RunId = runId } }),
                runId).Status);

        var tombstoned = RentalBillingRunTombstonePolicy.Lookup(
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    RunId = runId,
                    IsTombstoned = true,
                    TombstonedAtUtc = (DateTime?)tombstonedAtUtc,
                    TombstonedByUsername = "operator"
                }
            }),
            runId);
        Assert.Equal(RentalBillingRunLookupStatus.Tombstoned, tombstoned.Status);
        Assert.Equal(tombstonedAtUtc, tombstoned.TombstonedAtUtc);
        Assert.Equal("operator", tombstoned.TombstonedByUsername);

        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.Lookup("{}", runId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidMarker,
            RentalBillingRunTombstonePolicy.Lookup(
                JsonSerializer.Serialize(new[] { new { RunId = runId, IsTombstoned = true } }),
                runId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidMarker,
            RentalBillingRunTombstonePolicy.Lookup(
                JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        RunId = runId,
                        IsTombstoned = true,
                        TombstonedAtUtc = "2026-08-04T10:00:00+09:00",
                        TombstonedByUsername = "operator"
                    }
                }),
                runId).Status);

        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.Validate("[1]").Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidMarker,
            RentalBillingRunTombstonePolicy.Validate(
                $$"""[{"RunId":"{{runId}}","IsTombstoned":true,"isTombstoned":false,"TombstonedAtUtc":"{{tombstonedAtUtc:O}}","TombstonedByUsername":"operator"}]""").Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidMarker,
            RentalBillingRunTombstonePolicy.Validate(
                $$"""[{"RunKey":"2026-08","IsTombstoned":true,"TombstonedAtUtc":"{{tombstonedAtUtc:O}}","TombstonedByUsername":"operator"}]""").Status);

        var lowercaseMarker = RentalBillingRunTombstonePolicy.Lookup(
            $$"""[{"runId":"{{runId}}","isTombstoned":true,"tombstonedAtUtc":"{{tombstonedAtUtc:O}}","tombstonedByUsername":"operator"}]""",
            runId);
        Assert.Equal(RentalBillingRunLookupStatus.Tombstoned, lowercaseMarker.Status);
        Assert.Equal(tombstonedAtUtc, lowercaseMarker.TombstonedAtUtc);

        var otherRunId = Guid.NewGuid();
        var duplicateRunKeyResult = RentalBillingRunTombstonePolicy.Validate(
            JsonSerializer.Serialize(new[]
            {
                new { RunId = runId, RunKey = "2026-08" },
                new { RunId = otherRunId, RunKey = " 2026-08 " }
            }));
        Assert.Equal(RentalBillingRunLookupStatus.InvalidJson, duplicateRunKeyResult.Status);
        Assert.Contains("duplicate RunId or RunKey", duplicateRunKeyResult.Error);
        Assert.Equal(
            RentalBillingRunLookupStatus.NotFound,
            RentalBillingRunTombstonePolicy.Validate(
                JsonSerializer.Serialize(new[]
                {
                    new { RunId = runId, RunKey = (string?)null }
                })).Status);
    }

    [Fact]
    public void Lookup_RejectsMalformedOrUnknownCoreValuesButPreservesBlankStatus()
    {
        var runId = Guid.NewGuid();
        string CreatePayload(string propertyName, object? value)
            => JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>
                {
                    ["RunId"] = runId,
                    [propertyName] = value
                }
            });
        var legacyStatusJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                Status = "   ",
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.Active,
            RentalBillingRunTombstonePolicy.Lookup(legacyStatusJson, runId).Status);

        var malformedPayloads = new[]
        {
            CreatePayload("Status", new { Invalid = true }),
            CreatePayload("Status", Array.Empty<object>()),
            CreatePayload("Status", 1),
            CreatePayload("Status", null),
            CreatePayload("Status", "legacy custom status"),
            CreatePayload("ScheduledDate", "not-a-date"),
            CreatePayload("PeriodStartDate", 1),
            CreatePayload("PeriodEndDate", null),
            CreatePayload("CycleMonths", 1.5m),
            CreatePayload("PeriodLabel", Array.Empty<object>()),
            CreatePayload("BilledAmount", "100"),
            CreatePayload("SettledAmount", -1m),
            CreatePayload("SettlementStatus", new { Invalid = true }),
            CreatePayload("SettlementStatus", null),
            CreatePayload("SettlementStatus", "   "),
            CreatePayload("SettlementStatus", "legacy custom settlement"),
            CreatePayload("SettledDate", 1),
            CreatePayload("Note", new { Invalid = true })
        };

        foreach (var payload in malformedPayloads)
        {
            Assert.Equal(
                RentalBillingRunLookupStatus.InvalidJson,
                RentalBillingRunTombstonePolicy.Lookup(payload, runId).Status);
        }
    }

    [Fact]
    public void FinancialRecalculationLookup_AllowsLegacyStatusValuesButRejectsNonTextShapes()
    {
        var runId = Guid.NewGuid();
        var legacyJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                RunId = runId,
                Status = "legacy custom status",
                SettlementStatus = "legacy custom settlement",
                ScheduledDate = DateOnly.MinValue,
                PeriodStartDate = new DateOnly(2026, 8, 31),
                PeriodEndDate = new DateOnly(2026, 8, 1),
                CycleMonths = 0,
                Items = Array.Empty<object>()
            },
            new
            {
                RunId = Guid.NewGuid(),
                Status = (string?)null,
                SettlementStatus = (string?)null,
                Items = Array.Empty<object>()
            }
        });

        Assert.Equal(
            RentalBillingRunLookupStatus.Active,
            RentalBillingRunTombstonePolicy.LookupForFinancialRecalculation(
                legacyJson,
                runId).Status);

        var malformedJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                Status = new { Invalid = true },
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(
                malformedJson).Status);

        var malformedSettlementStatusJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                SettlementStatus = new { Invalid = true },
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(
                malformedSettlementStatusJson).Status);

        var malformedMarkerJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                IsTombstoned = true,
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidMarker,
            RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(
                malformedMarkerJson).Status);

        var duplicateIdentityJson = JsonSerializer.Serialize(new[]
        {
            new { RunId = Guid.NewGuid(), RunKey = "2026-[08]" },
            new { RunId = Guid.NewGuid(), RunKey = "2026-08" }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(
                duplicateIdentityJson).Status);

        var duplicateActiveRunId = Guid.NewGuid();
        var duplicateActiveJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = duplicateActiveRunId,
                RunKey = "duplicate-active",
                Status = "예정",
                SettlementStatus = "미입금",
                Items = Array.Empty<object>()
            },
            new
            {
                RunId = duplicateActiveRunId,
                RunKey = "duplicate-active",
                Status = "예정",
                SettlementStatus = "미입금",
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.Active,
            RentalBillingRunTombstonePolicy.Lookup(
                duplicateActiveJson,
                duplicateActiveRunId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.LookupForServerMutation(
                duplicateActiveJson,
                duplicateActiveRunId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.LookupForFinancialRecalculation(
                duplicateActiveJson,
                duplicateActiveRunId).Status);

        var tombstonedAtUtc = new DateTime(2026, 8, 4, 5, 0, 0, DateTimeKind.Utc);
        var duplicateTombstoneJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = duplicateActiveRunId,
                RunKey = "duplicate-active",
                Status = "취소",
                BilledAmount = 0m,
                SettledAmount = 0m,
                SettlementStatus = "미입금",
                SettledDate = (DateOnly?)null,
                IsTombstoned = true,
                TombstonedAtUtc = tombstonedAtUtc,
                TombstonedByUsername = "operator",
                Items = Array.Empty<object>()
            },
            new
            {
                RunId = duplicateActiveRunId,
                RunKey = "duplicate-active",
                Status = "취소",
                BilledAmount = 0m,
                SettledAmount = 0m,
                SettlementStatus = "미입금",
                SettledDate = (DateOnly?)null,
                IsTombstoned = true,
                TombstonedAtUtc = tombstonedAtUtc,
                TombstonedByUsername = "operator",
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.Tombstoned,
            RentalBillingRunTombstonePolicy.LookupForServerMutation(
                duplicateTombstoneJson,
                duplicateActiveRunId).Status);
        Assert.Equal(
            RentalBillingRunLookupStatus.Tombstoned,
            RentalBillingRunTombstonePolicy.LookupForFinancialRecalculation(
                duplicateTombstoneJson,
                duplicateActiveRunId).Status);

        var unrepairableCycleJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                CycleMonths = 1201,
                Items = Array.Empty<object>()
            }
        });
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(
                unrepairableCycleJson).Status);
    }

    [Fact]
    public void MergeBillingRunsJson_TombstoneWinsRegardlessOfInputOrder()
    {
        var runId = Guid.NewGuid();
        var activeJson =
            $$"""[{"RunId":"{{runId}}","RunKey":"2026-08","Status":"planned","Items":[{"DisplayItemName":"active-item"}]}]""";
        var tombstonedAtUtc = new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc);
        var tombstoneJson =
            $$"""[{"runId":"{{runId}}","runKey":"2026-08","Status":"completed","BilledAmount":100,"SettledAmount":100,"SettlementStatus":"settled","SettledDate":"2026-08-04","items":[{"DisplayItemName":"tombstone-item"}],"isTombstoned":true,"tombstonedAtUtc":"{{tombstonedAtUtc:O}}","tombstonedByUsername":"operator"}]""";

        foreach (var merged in new[]
                 {
                     RentalDuplicateNormalizer.MergeBillingRunsJson(activeJson, tombstoneJson),
                     RentalDuplicateNormalizer.MergeBillingRunsJson(tombstoneJson, activeJson)
                 })
        {
            var lookup = RentalBillingRunTombstonePolicy.Lookup(merged, runId);
            Assert.Equal(RentalBillingRunLookupStatus.Tombstoned, lookup.Status);
            Assert.Equal("operator", lookup.TombstonedByUsername);
            using var document = JsonDocument.Parse(merged);
            var run = Assert.Single(document.RootElement.EnumerateArray());
            Assert.True(run.GetProperty("IsTombstoned").GetBoolean());
            Assert.Equal(tombstonedAtUtc, run.GetProperty("TombstonedAtUtc").GetDateTime());
            Assert.Equal("취소", run.GetProperty("Status").GetString());
            Assert.Equal(0m, run.GetProperty("BilledAmount").GetDecimal());
            Assert.Equal(0m, run.GetProperty("SettledAmount").GetDecimal());
            Assert.Equal("미입금", run.GetProperty("SettlementStatus").GetString());
            Assert.Equal(JsonValueKind.Null, run.GetProperty("SettledDate").ValueKind);
            Assert.Equal(2, run.GetProperty("Items").GetArrayLength());
            Assert.False(run.TryGetProperty("isTombstoned", out _));
            var propertyNames = run.EnumerateObject()
                .Select(property => property.Name)
                .ToList();
            Assert.Single(propertyNames, name => string.Equals(name, "RunId", StringComparison.OrdinalIgnoreCase));
            Assert.Single(propertyNames, name => string.Equals(name, "RunKey", StringComparison.OrdinalIgnoreCase));
            Assert.Single(propertyNames, name => string.Equals(name, "Items", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("RunId", propertyNames);
            Assert.Contains("RunKey", propertyNames);
            Assert.Contains("Items", propertyNames);
        }
    }

    [Fact]
    public void MergeBillingRunsJson_ConflictingIdentityCasingRemainsFailClosed()
    {
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var conflictingJson =
            $$"""[{"RunId":"{{firstRunId}}","runId":"{{secondRunId}}","RunKey":"2026-08","Items":[]}]""";

        var merged = RentalDuplicateNormalizer.MergeBillingRunsJson(conflictingJson, "[]");

        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.Validate(merged).Status);
    }

    [Fact]
    public void MergeBillingRunsJson_SameRunIdWithDifferentRunKeysPreservesConflict()
    {
        var runId = Guid.NewGuid();
        var primaryJson = JsonSerializer.Serialize(new[]
        {
            new { RunId = runId, RunKey = "2026-08", Items = Array.Empty<object>() }
        });
        var secondaryJson = JsonSerializer.Serialize(new[]
        {
            new { RunId = runId, RunKey = "2026-09", Items = Array.Empty<object>() }
        });

        var merged = RentalDuplicateNormalizer.MergeBillingRunsJson(primaryJson, secondaryJson);

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal(
            RentalBillingRunLookupStatus.InvalidJson,
            RentalBillingRunTombstonePolicy.Validate(merged).Status);
    }

    [Fact]
    public void MergeBillingRunsJson_NullRunKeyPrimaryRetainsRunKeyEnrichment()
    {
        var runId = Guid.NewGuid();
        var primaryJson = JsonSerializer.Serialize(new[]
        {
            new { RunId = runId, RunKey = (string?)null, Items = Array.Empty<object>() }
        });
        var secondaryJson = JsonSerializer.Serialize(new[]
        {
            new { RunId = runId, RunKey = "2026-08", Items = Array.Empty<object>() }
        });

        var merged = RentalDuplicateNormalizer.MergeBillingRunsJson(primaryJson, secondaryJson);

        Assert.Equal(
            RentalBillingRunLookupStatus.Active,
            RentalBillingRunTombstonePolicy.Lookup(merged, runId).Status);
        using var document = JsonDocument.Parse(merged);
        var run = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("2026-08", run.GetProperty("RunKey").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceCreateOrUpdate_RejectsTombstonedRunWithoutMutation(bool updateExisting)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: true, seedInvoice: updateExisting);
        await dbContext.SaveChangesAsync();

        var dto = updateExisting
            ? (await dbContext.Invoices.AsNoTracking().SingleAsync()).ToDto()
            : CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId);
        dto.LinkedRentalBillingProfileId = scenario.ProfileId;
        dto.LinkedRentalBillingRunId = scenario.RunId;
        dto.Memo = "must-not-save";
        dto.ExpectedRevision = updateExisting
            ? await dbContext.Invoices.Select(current => current.Revision).SingleAsync()
            : 0;

        var controller = CreateInvoicesController(dbContext, user);
        var response = updateExisting
            ? await controller.Update(dto.Id, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Invoices.CountAsync());
        if (updateExisting)
            Assert.Equal(string.Empty, await dbContext.Invoices.Select(current => current.Memo).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceCreateOrUpdate_RejectsMalformedProfileWhenRunIdMissing(bool updateExisting)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: updateExisting);
        var profile = dbContext.RentalBillingProfiles.Local.Single();
        profile.BillingRunsJson = CreateUnidentifiableTombstoneJson();
        if (updateExisting)
            dbContext.Invoices.Local.Single().LinkedRentalBillingRunId = null;
        await dbContext.SaveChangesAsync();

        var dto = updateExisting
            ? (await dbContext.Invoices.AsNoTracking().SingleAsync()).ToDto()
            : CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId);
        dto.LinkedRentalBillingRunId = null;
        dto.Memo = "must-not-save";
        dto.ExpectedRevision = updateExisting
            ? await dbContext.Invoices.Select(current => current.Revision).SingleAsync()
            : 0;
        var controller = CreateInvoicesController(dbContext, user);
        var response = updateExisting
            ? await controller.Update(dto.Id, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Invoices.CountAsync());
        if (updateExisting)
            Assert.Equal(string.Empty, await dbContext.Invoices.Select(current => current.Memo).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentCreateOrUpdate_RejectsInvoiceLinkedToTombstonedRunWithoutMutation(bool updateExisting)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: true, seedInvoice: true);
        await dbContext.SaveChangesAsync();
        var invoice = await dbContext.Invoices.SingleAsync();
        Payment? payment = null;
        if (updateExisting)
        {
            payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m
            };
            dbContext.Payments.Add(payment);
            dbContext.Transactions.Add(CreateLinkedTransaction(payment.Id, invoice, scenario.ProfileId, scenario.RunId));
        }
        await dbContext.SaveChangesAsync();

        var dto = payment is null
            ? new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m
            }
            : (await dbContext.Payments.AsNoTracking().SingleAsync()).ToDto();
        dto.Note = "must-not-save";
        dto.ExpectedRevision = payment is null ? invoice.Revision : payment.Revision;

        var controller = CreatePaymentsController(dbContext, user);
        var response = updateExisting
            ? await controller.Update(dto.Id, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Payments.CountAsync());
        if (updateExisting)
            Assert.Equal(string.Empty, await dbContext.Payments.Select(current => current.Note).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentCreateOrUpdate_RejectsMalformedProfileWhenRunIdMissing(bool updateExisting)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: true);
        var profile = dbContext.RentalBillingProfiles.Local.Single();
        profile.BillingRunsJson = CreateUnidentifiableTombstoneJson();
        var invoice = dbContext.Invoices.Local.Single();
        invoice.LinkedRentalBillingRunId = null;
        Payment? payment = null;
        if (updateExisting)
        {
            payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m
            };
            var linkedTransaction = CreateLinkedTransaction(
                payment.Id,
                invoice,
                scenario.ProfileId,
                scenario.RunId);
            linkedTransaction.LinkedRentalBillingRunId = null;
            dbContext.Payments.Add(payment);
            dbContext.Transactions.Add(linkedTransaction);
        }
        await dbContext.SaveChangesAsync();

        var dto = payment is null
            ? new PaymentDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m,
                ExpectedRevision = invoice.Revision
            }
            : (await dbContext.Payments.AsNoTracking().SingleAsync()).ToDto();
        dto.Note = "must-not-save";
        if (payment is not null)
            dto.ExpectedRevision = payment.Revision;

        var controller = CreatePaymentsController(dbContext, user);
        var response = updateExisting
            ? await controller.Update(dto.Id, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Payments.CountAsync());
        if (updateExisting)
            Assert.Equal(string.Empty, await dbContext.Payments.Select(current => current.Note).SingleAsync());
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("payment")]
    [InlineData("restore")]
    public async Task FinanceMutation_RejectsMalformedActiveRunCoreWithoutChangingRows(string operation)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(
            dbContext,
            tombstoned: false,
            seedInvoice: operation is "payment" or "restore");
        var profile = dbContext.RentalBillingProfiles.Local.Single();
        var malformedRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                Status = new { Invalid = true },
                Items = Array.Empty<object>()
            }
        });
        profile.BillingRunsJson = malformedRunsJson;
        if (operation == "restore")
            dbContext.Invoices.Local.Single().IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var invoiceCountBefore = await dbContext.Invoices.IgnoreQueryFilters().CountAsync();
        var paymentCountBefore = await dbContext.Payments.IgnoreQueryFilters().CountAsync();
        var transactionCountBefore = await dbContext.Transactions.IgnoreQueryFilters().CountAsync();
        if (operation == "invoice")
        {
            var response = await CreateInvoicesController(dbContext, user).Create(
                CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId),
                CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(response.Result);
        }
        else if (operation == "payment")
        {
            var invoice = await dbContext.Invoices.AsNoTracking().SingleAsync();
            var response = await CreatePaymentsController(dbContext, user).Create(
                new PaymentDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    PaymentDate = new DateOnly(2026, 8, 4),
                    Amount = 10m,
                    ExpectedRevision = invoice.Revision
                },
                CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(response.Result);
        }
        else
        {
            var invoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            var response = await CreateRecycleBinController(dbContext, user).Restore(
                new RecycleBinMutationRequest
                {
                    Items =
                    [
                        new RecycleBinMutationTargetDto
                        {
                            EntityId = invoice.Id,
                            Kind = "invoice",
                            ExpectedRevision = invoice.Revision
                        }
                    ]
                },
                CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var result = Assert.Single(Assert.IsType<RecycleBinMutationResultDto>(ok.Value).Results);
            Assert.False(result.Success);
        }

        dbContext.ChangeTracker.Clear();
        Assert.Equal(malformedRunsJson, await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Select(current => current.BillingRunsJson)
            .SingleAsync());
        Assert.Equal(invoiceCountBefore, await dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(paymentCountBefore, await dbContext.Payments.IgnoreQueryFilters().CountAsync());
        Assert.Equal(transactionCountBefore, await dbContext.Transactions.IgnoreQueryFilters().CountAsync());
        if (operation == "restore")
            Assert.True(await dbContext.Invoices.IgnoreQueryFilters().Select(current => current.IsDeleted).SingleAsync());
    }

    [Theory]
    [InlineData("unknown-status")]
    [InlineData("blank-settlement-status")]
    [InlineData("unknown-settlement-status")]
    public async Task InvoiceCreate_RejectsUnknownOrBlankRunStatusesWithoutChangingRows(string invalidKind)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: false);
        var run = new Dictionary<string, object?>
        {
            ["RunId"] = scenario.RunId,
            ["Items"] = Array.Empty<object>()
        };
        if (invalidKind == "unknown-status")
            run["Status"] = "legacy custom status";
        else
            run["SettlementStatus"] = invalidKind == "blank-settlement-status"
                ? "   "
                : "legacy custom settlement";
        var malformedRunsJson = JsonSerializer.Serialize(new[] { run });
        dbContext.RentalBillingProfiles.Local.Single().BillingRunsJson = malformedRunsJson;
        await dbContext.SaveChangesAsync();

        var response = await CreateInvoicesController(dbContext, user).Create(
            CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(malformedRunsJson, await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Select(current => current.BillingRunsJson)
            .SingleAsync());
        Assert.Empty(await dbContext.Invoices.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task InvoiceCreate_AcceptsBlankRunStatusAsLegacyUnspecified()
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: false);
        dbContext.RentalBillingProfiles.Local.Single().BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                Status = "   ",
                Items = Array.Empty<object>()
            }
        });
        await dbContext.SaveChangesAsync();

        var response = await CreateInvoicesController(dbContext, user).Create(
            CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.Single(await dbContext.Invoices.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task InvoiceCreate_RejectsDuplicateActivePhysicalIdentityWithoutChangingRows()
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: false);
        var duplicateRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                RunKey = "duplicate-active",
                Status = "예정",
                SettlementStatus = "미입금",
                Items = Array.Empty<object>()
            },
            new
            {
                RunId = scenario.RunId,
                RunKey = "duplicate-active",
                Status = "예정",
                SettlementStatus = "미입금",
                Items = Array.Empty<object>()
            }
        });
        dbContext.RentalBillingProfiles.Local.Single().BillingRunsJson = duplicateRunsJson;
        await dbContext.SaveChangesAsync();

        var response = await CreateInvoicesController(dbContext, user).Create(
            CreateInvoiceDto(scenario.CustomerId, scenario.ProfileId, scenario.RunId),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            duplicateRunsJson,
            await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .Select(current => current.BillingRunsJson)
                .SingleAsync());
        Assert.Empty(await dbContext.Invoices.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("payment")]
    [InlineData("transaction")]
    public async Task RecycleBinRestore_RejectsTombstonedRunWithoutRestoring(string kind)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: true, seedInvoice: true);
        await dbContext.SaveChangesAsync();
        var invoice = await dbContext.Invoices.SingleAsync();
        Guid entityId;
        if (kind == "invoice")
        {
            invoice.IsDeleted = true;
            entityId = invoice.Id;
        }
        else if (kind == "transaction")
        {
            var transaction = CreateLinkedTransaction(
                Guid.NewGuid(),
                invoice,
                scenario.ProfileId,
                scenario.RunId);
            transaction.IsDeleted = true;
            dbContext.Transactions.Add(transaction);
            entityId = transaction.Id;
        }
        else
        {
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m,
                IsDeleted = true
            };
            dbContext.Payments.Add(payment);
            entityId = payment.Id;
        }
        await dbContext.SaveChangesAsync();
        var revision = kind == "invoice"
            ? await dbContext.Invoices.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync()
            : kind == "payment"
                ? await dbContext.Payments.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync()
                : await dbContext.Transactions.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync();

        var response = await CreateRecycleBinController(dbContext, user).Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = entityId,
                        Kind = kind,
                        ExpectedRevision = revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.Single(Assert.IsType<RecycleBinMutationResultDto>(ok.Value).Results);
        Assert.False(result.Success);
        dbContext.ChangeTracker.Clear();
        var stillDeleted = kind == "invoice"
            ? await dbContext.Invoices.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync()
            : kind == "payment"
                ? await dbContext.Payments.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync()
                : await dbContext.Transactions.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync();
        Assert.True(stillDeleted);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("payment")]
    [InlineData("transaction")]
    public async Task RecycleBinRestore_RejectsMalformedProfileWhenRunIdMissing(string kind)
    {
        var user = CreateAdminUser();
        await using var dbContext = CreateDbContext(user);
        var scenario = SeedScenario(dbContext, tombstoned: false, seedInvoice: true);
        var profile = dbContext.RentalBillingProfiles.Local.Single();
        profile.BillingRunsJson = CreateUnidentifiableTombstoneJson();
        var invoice = dbContext.Invoices.Local.Single();
        invoice.LinkedRentalBillingRunId = null;
        Guid entityId;
        if (kind == "invoice")
        {
            invoice.IsDeleted = true;
            entityId = invoice.Id;
        }
        else if (kind == "transaction")
        {
            var transaction = CreateLinkedTransaction(
                Guid.NewGuid(),
                invoice,
                scenario.ProfileId,
                scenario.RunId);
            transaction.LinkedRentalBillingRunId = null;
            transaction.IsDeleted = true;
            dbContext.Transactions.Add(transaction);
            entityId = transaction.Id;
        }
        else
        {
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 8, 4),
                Amount = 10m,
                IsDeleted = true
            };
            dbContext.Payments.Add(payment);
            entityId = payment.Id;
        }
        await dbContext.SaveChangesAsync();
        var revision = kind == "invoice"
            ? await dbContext.Invoices.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync()
            : kind == "payment"
                ? await dbContext.Payments.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync()
                : await dbContext.Transactions.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.Revision).SingleAsync();

        var response = await CreateRecycleBinController(dbContext, user).Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = entityId,
                        Kind = kind,
                        ExpectedRevision = revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.Single(Assert.IsType<RecycleBinMutationResultDto>(ok.Value).Results);
        Assert.False(result.Success);
        dbContext.ChangeTracker.Clear();
        var stillDeleted = kind == "invoice"
            ? await dbContext.Invoices.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync()
            : kind == "payment"
                ? await dbContext.Payments.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync()
                : await dbContext.Transactions.IgnoreQueryFilters().Where(current => current.Id == entityId).Select(current => current.IsDeleted).SingleAsync();
        Assert.True(stillDeleted);
    }

    private Scenario SeedScenario(AppDbContext dbContext, bool tombstoned, bool seedInvoice)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"Customer-{Guid.NewGuid():N}",
            NameMatchKey = $"CUSTOMER{Guid.NewGuid():N}",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var profile = new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingRunsJson = tombstoned
                ? JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        RunId = runId,
                        IsTombstoned = true,
                        TombstonedAtUtc = (DateTime?)new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc),
                        TombstonedByUsername = "operator"
                    }
                })
                : JsonSerializer.Serialize(new[] { new { RunId = runId } }),
            IsActive = true
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(profile);

        if (seedInvoice)
        {
            var dto = CreateInvoiceDto(customer.Id, profileId, runId);
            dbContext.Invoices.Add(new Invoice
            {
                Id = dto.Id,
                CustomerId = dto.CustomerId,
                TenantCode = dto.TenantCode,
                OfficeCode = dto.OfficeCode,
                ResponsibleOfficeCode = dto.ResponsibleOfficeCode,
                InvoiceNumber = dto.InvoiceNumber,
                VersionGroupId = dto.Id,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = dto.VoucherType,
                InvoiceDate = dto.InvoiceDate,
                TotalAmount = dto.TotalAmount,
                LinkedRentalBillingProfileId = profileId,
                LinkedRentalBillingRunId = runId
            });
        }

        return new Scenario(customer.Id, profileId, runId);
    }

    private static string CreateUnidentifiableTombstoneJson()
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                RunKey = "2026-08",
                IsTombstoned = true,
                TombstonedAtUtc = (DateTime?)new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc),
                TombstonedByUsername = "operator"
            }
        });

    private static InvoiceDto CreateInvoiceDto(Guid customerId, Guid profileId, Guid runId)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20],
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 4),
            TotalAmount = 100m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        };

    private static TransactionRecord CreateLinkedTransaction(
        Guid id,
        Invoice invoice,
        Guid profileId,
        Guid runId)
        => new()
        {
            Id = id,
            CustomerId = invoice.CustomerId,
            TenantCode = invoice.TenantCode,
            OfficeCode = invoice.OfficeCode,
            ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
            TransactionDate = invoice.InvoiceDate,
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            SettlementAmount = 10m
        };

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
    }

    private static InvoicesController CreateInvoicesController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static PaymentsController CreatePaymentsController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            NoOpStoredFileReferenceReconciler.Instance,
            new RentalSettlementRecalculationService(dbContext));

    private static RecycleBinController CreateRecycleBinController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new RecycleBinController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "tombstone-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    public void Dispose() => _connection.Dispose();

    private readonly record struct Scenario(Guid CustomerId, Guid ProfileId, Guid RunId);

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];
        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{Guid.NewGuid():N}"[..20]);
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();
        public Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));
        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) => fallback ?? [];
        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
