using System.Security.Cryptography;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("payments")]
public sealed class PaymentsController : ControllerBase
{
    private static readonly HashSet<string> AllowedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif"
    };

    private static readonly HashSet<string> AllowedAttachmentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/bmp",
        "image/gif",
        "image/webp",
        "image/tiff",
        "image/heic",
        "image/heif"
    };

    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public PaymentsController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    public async Task<ActionResult<List<PaymentDto>>> GetByInvoice([FromQuery] Guid invoiceId, CancellationToken cancellationToken)
        => Ok(await _officeScopeService.ApplyPaymentScope(_dbContext.Payments
            .AsNoTracking()
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .Include(x => x.Attachments))
            .Where(x => x.InvoiceId == invoiceId)
            .OrderByDescending(x => x.PaymentDate)
            .Select(x => x.ToDto())
            .ToListAsync(cancellationToken));

    [HttpGet("{paymentId:guid}/attachments")]
    public async Task<ActionResult<List<PaymentAttachmentDto>>> GetAttachments(Guid paymentId, CancellationToken cancellationToken)
    {
        var paymentExists = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments.AsNoTracking()
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer))
            .AnyAsync(x => x.Id == paymentId, cancellationToken);
        if (!paymentExists)
            return NotFound();

        var attachments = await _dbContext.PaymentAttachments
            .AsNoTracking()
            .Where(x => x.PaymentId == paymentId)
            .OrderByDescending(x => x.UploadedAtUtc)
            .Select(x => x.ToDto(false))
            .ToListAsync(cancellationToken);

        return Ok(attachments);
    }

    [HttpGet("attachments/{attachmentId:guid}/content")]
    public async Task<IActionResult> GetAttachmentContent(Guid attachmentId, CancellationToken cancellationToken)
    {
        var attachment = await _dbContext.PaymentAttachments
            .AsNoTracking()
            .Include(x => x.Payment)
            .ThenInclude(payment => payment!.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstOrDefaultAsync(x => x.Id == attachmentId, cancellationToken);
        if (attachment is not null && !_officeScopeService.CanReadOfficeForPayments(attachment.Payment?.Invoice?.OfficeCode, attachment.Payment?.Invoice?.TenantCode))
            attachment = null;
        if (attachment is null)
            return NotFound();

        var fileName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? $"payment-attachment-{attachment.Id:N}"
            : Path.GetFileName(attachment.FileName);

        var contentType = NormalizeContentType(attachment.MimeType, fileName);
        if (!IsAllowedAttachment(fileName, contentType))
            contentType = "application/octet-stream";

        return File(attachment.FileContent ?? [], contentType, fileName);
    }

    [HttpPost("{paymentId:guid}/attachments")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<PaymentAttachmentDto>> UploadAttachment(
        Guid paymentId,
        [FromForm] IFormFile file,
        [FromForm] string? attachmentType,
        [FromForm] string? description,
        CancellationToken cancellationToken)
    {
        var payment = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);
        if (payment is not null && !_officeScopeService.CanWriteOfficeForPayments(payment.Invoice?.OfficeCode, payment.Invoice?.TenantCode))
            return Forbid();
        if (payment is null)
            return NotFound();

        if (file is null || file.Length <= 0)
            return BadRequest(new { error = "empty_file", message = "업로드할 파일을 선택하세요." });

        if (file.Length > 15 * 1024 * 1024)
            return BadRequest(new { error = "file_too_large", message = "첨부 파일은 15MB 이하만 업로드할 수 있습니다." });

        var safeFileName = Path.GetFileName(file.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName))
            return BadRequest(new { error = "invalid_file_name", message = "유효한 첨부 파일명을 확인할 수 없습니다." });

        var normalizedContentType = NormalizeContentType(file.ContentType, safeFileName);
        if (!IsAllowedAttachment(safeFileName, normalizedContentType))
        {
            return BadRequest(new
            {
                error = "unsupported_file_type",
                message = "첨부 파일은 PDF 또는 이미지 파일만 업로드할 수 있습니다."
            });
        }

        await using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();

        var entity = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            AttachmentType = string.IsNullOrWhiteSpace(attachmentType) ? "내역첨부" : attachmentType.Trim(),
            Description = description?.Trim() ?? string.Empty,
            FileName = safeFileName,
            MimeType = normalizedContentType,
            FileSize = bytes.LongLength,
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            UploadedAtUtc = DateTime.UtcNow,
            FileContent = bytes
        };

        _dbContext.PaymentAttachments.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto(false));
    }

    private static string NormalizeContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType.Split(';', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            _ => "application/octet-stream"
        };
    }

    private static bool IsAllowedAttachment(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (!AllowedAttachmentExtensions.Contains(extension))
            return false;

        if (AllowedAttachmentContentTypes.Contains(contentType))
            return true;

        return string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost]
    public async Task<ActionResult<PaymentDto>> Create([FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
        if (invoice is null)
            return BadRequest("Referenced invoice was not found.");
        if (!_officeScopeService.CanWriteOfficeForPayments(invoice.OfficeCode, invoice.TenantCode))
            return Forbid();

        var entity = new Payment { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        _dbContext.Payments.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var saved = await _dbContext.Payments
            .AsNoTracking()
            .Include(x => x.Attachments)
            .FirstAsync(x => x.Id == entity.Id, cancellationToken);
        return Ok(saved.ToDto());
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PaymentDto>> Update(Guid id, [FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (entity.Invoice is null || !_officeScopeService.CanWriteOfficeForPayments(entity.Invoice.OfficeCode, entity.Invoice.TenantCode))
            return Forbid();

        entity.Apply(dto);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (entity.Invoice is null || !_officeScopeService.CanWriteOfficeForPayments(entity.Invoice.OfficeCode, entity.Invoice.TenantCode))
            return Forbid();

        entity.IsDeleted = true;
        var attachments = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .Where(x => x.PaymentId == id && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
            attachment.IsDeleted = true;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}

