using System.Security.Cryptography;
using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

internal static class PaymentAttachmentUploadIntegrity
{
    public static async Task ValidateAndRewindAsync(
        Stream stream,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(attachment);
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException(
                "Attachment upload requires a readable seekable stream.");
        if (attachment.FileSize < 0 ||
            stream.Length != attachment.FileSize)
        {
            throw new InvalidDataException(
                $"Attachment size changed before upload: {attachment.FileName}");
        }

        stream.Position = 0;
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, ct));
        if (string.IsNullOrWhiteSpace(attachment.FileHash) ||
            !string.Equals(
                actualHash,
                attachment.FileHash.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Attachment content changed before upload: {attachment.FileName}");
        }

        stream.Position = 0;
    }
}
