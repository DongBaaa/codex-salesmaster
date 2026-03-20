namespace GeoraePlan.Mobile.App.Models;

public sealed class PendingPaymentAttachmentRecord
{
    public Guid LocalId { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public string AttachmentType { get; set; } = "내역첨부";
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
