using System.Text.Json;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class AttachmentUploadUtcMappingTests
{
    [Theory]
    [InlineData("contract", DateTimeKind.Unspecified, false)]
    [InlineData("transaction", DateTimeKind.Unspecified, false)]
    [InlineData("payment", DateTimeKind.Unspecified, false)]
    [InlineData("contract", DateTimeKind.Utc, false)]
    [InlineData("transaction", DateTimeKind.Utc, false)]
    [InlineData("payment", DateTimeKind.Utc, false)]
    [InlineData("contract", DateTimeKind.Local, false)]
    [InlineData("transaction", DateTimeKind.Local, false)]
    [InlineData("payment", DateTimeKind.Local, false)]
    [InlineData("contract", DateTimeKind.Unspecified, true)]
    [InlineData("transaction", DateTimeKind.Unspecified, true)]
    [InlineData("payment", DateTimeKind.Unspecified, true)]
    public void UploadTimestamp_RetainsInstantAndPrecisionWithUtcWireFormat(string entityKind, DateTimeKind kind, bool isDefault)
    {
        var stored = isDefault ? default : new DateTime(2026, 9, 14, 7, 9, 9, kind).AddTicks(7223738);
        var expected = kind == DateTimeKind.Local ? stored.ToUniversalTime() : DateTime.SpecifyKind(stored, DateTimeKind.Utc);
        var actual = entityKind switch
        {
            "contract" => new CustomerContract { UploadedAtUtc = stored }.ToDto().UploadedAtUtc,
            "transaction" => new TransactionAttachment { UploadedAtUtc = stored }.ToDto(includeContent: false).UploadedAtUtc,
            "payment" => new PaymentAttachment { UploadedAtUtc = stored }.ToDto().UploadedAtUtc,
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(expected.Ticks, actual.Ticks);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }
}
