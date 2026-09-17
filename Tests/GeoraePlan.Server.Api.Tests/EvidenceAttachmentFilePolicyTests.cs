using System.Text;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class EvidenceAttachmentFilePolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Pdf_TruncatedHeaderIsRejected(int length)
    {
        var content = Encoding.ASCII.GetBytes("%PDF-")[..length];

        Assert.False(EvidenceAttachmentFilePolicy.ContentMatchesFileType(
            "receipt.pdf", "application/pdf", content));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    public void Pdf_TruncatedHeaderAfterPrefixIsRejected(int prefixLength)
    {
        var content = Encoding.ASCII.GetBytes(new string(' ', prefixLength) + "%PDF");

        Assert.False(EvidenceAttachmentFilePolicy.ContentMatchesFileType(
            "receipt.pdf", "application/pdf", content));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    public void Pdf_CompleteSignatureWithinExistingSearchWindowIsRecognized(int prefixLength)
    {
        var content = Encoding.ASCII.GetBytes(new string(' ', prefixLength) + "%PDF-1.7\n");

        Assert.True(EvidenceAttachmentFilePolicy.ContentMatchesFileType(
            "receipt.pdf", "application/pdf", content));
    }

    [Fact]
    public void Pdf_SignaturePastExistingSearchWindowIsRejected()
    {
        var content = Encoding.ASCII.GetBytes(new string(' ', 1025) + "%PDF-1.7\n");

        Assert.False(EvidenceAttachmentFilePolicy.ContentMatchesFileType(
            "receipt.pdf", "application/pdf", content));
    }
}
