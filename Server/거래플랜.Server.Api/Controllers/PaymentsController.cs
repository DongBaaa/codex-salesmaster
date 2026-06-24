using System.Security.Cryptography;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
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
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ICentralFileStorage _fileStorage;
    private readonly RentalSettlementRecalculationService _rentalSettlementRecalculationService;

    public PaymentsController(
        AppDbContext dbContext,
        OfficeScopeService officeScopeService,
        ICentralFileStorage fileStorage,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet]
    public async Task<ActionResult<List<PaymentDto>>> GetByInvoice([FromQuery] Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!await CanReadInvoiceForPaymentApiAsync(invoiceId, cancellationToken))
            return Ok(new List<PaymentDto>());

        return Ok(await _officeScopeService.ApplyPaymentScope(_dbContext.Payments
                .AsNoTracking()
                .Include(x => x.Invoice)
                .ThenInclude(invoice => invoice!.Customer)
                .Include(x => x.Attachments))
            .Where(x => x.InvoiceId == invoiceId)
            .OrderByDescending(x => x.PaymentDate)
            .Select(x => x.ToDto())
            .ToListAsync(cancellationToken));
    }

    [HttpGet("{paymentId:guid}/attachments")]
    public async Task<ActionResult<List<PaymentAttachmentDto>>> GetAttachments(Guid paymentId, CancellationToken cancellationToken)
    {
        var paymentExists = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments.AsNoTracking()
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer))
            .Where(x => x.Id == paymentId)
            .Select(x => new
            {
                x.InvoiceId,
                Invoice = x.Invoice
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (paymentExists is null || !CanReadInvoiceForPaymentApi(paymentExists.Invoice))
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
        if (attachment is not null &&
            (!_officeScopeService.CanReadOfficeForPayments(
                 attachment.Payment?.Invoice?.ResponsibleOfficeCode,
                 attachment.Payment?.Invoice?.TenantCode,
                 attachment.Payment?.Invoice?.OfficeCode) ||
             !CanReadInvoiceForPaymentApi(attachment.Payment?.Invoice)))
        {
            attachment = null;
        }
        if (attachment is null)
            return NotFound();

        var fileName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? $"payment-attachment-{attachment.Id:N}"
            : Path.GetFileName(attachment.FileName);

        var contentType = EvidenceAttachmentFilePolicy.NormalizeContentType(attachment.MimeType, fileName);
        if (!EvidenceAttachmentFilePolicy.IsAllowedFileType(fileName, contentType))
            contentType = "application/octet-stream";

        var bytes = _fileStorage.ReadBytes(attachment.StoragePath, attachment.FileContent);
        if (attachment.FileSize > 0 && bytes.LongLength != attachment.FileSize)
        {
            return NotFound(new
            {
                error = "attachment_content_unavailable",
                message = "첨부 파일 내용을 찾을 수 없습니다."
            });
        }

        return File(bytes, contentType, fileName);
    }

    private async Task<bool> CanReadInvoiceForPaymentApiAsync(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        if (invoiceId == Guid.Empty)
            return false;

        return await _officeScopeService
            .ApplySyncInvoiceScope(_dbContext.Invoices.AsNoTracking())
            .AnyAsync(invoice => invoice.Id == invoiceId, cancellationToken);
    }

    private bool CanReadInvoiceForPaymentApi(Invoice? invoice)
        => invoice is not null &&
           _officeScopeService.CanReadOfficeForSyncInvoices(
               invoice.ResponsibleOfficeCode,
               invoice.TenantCode,
               invoice.OfficeCode);

    [HttpPost("{paymentId:guid}/attachments")]
    [Authorize(Policy = PermissionNames.PaymentEdit)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<PaymentAttachmentDto>> UploadAttachment(
        Guid paymentId,
        [FromForm] IFormFile file,
        [FromForm] string? attachmentType,
        [FromForm] string? description,
        [FromForm] Guid? clientAttachmentId,
        CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditPayments())
            return Forbid();

        var payment = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstOrDefaultAsync(x => x.Id == paymentId, cancellationToken);
        if (payment is not null && !_officeScopeService.CanWriteOfficeForPayments(payment.Invoice?.ResponsibleOfficeCode, payment.Invoice?.TenantCode, payment.Invoice?.OfficeCode))
            return Forbid();
        if (payment is null)
            return NotFound();

        if (file is null || file.Length <= 0)
            return BadRequest(new { error = "empty_file", message = "업로드할 파일을 선택하세요." });

        if (file.Length > 15 * 1024 * 1024)
            return BadRequest(new { error = "file_too_large", message = "첨부 파일은 15MB 이하만 업로드할 수 있습니다." });

        var attachmentId = clientAttachmentId.GetValueOrDefault();
        if (attachmentId != Guid.Empty)
        {
            var existingAttachment = await _dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == attachmentId, cancellationToken);
            if (existingAttachment is not null)
            {
                if (existingAttachment.PaymentId != paymentId)
                {
                    return Conflict(new
                    {
                        error = "client_attachment_id_conflict",
                        message = "이미 다른 수금/지급 내역에 사용된 모바일 첨부 식별자입니다."
                    });
                }

                if (existingAttachment.IsDeleted)
                {
                    return Conflict(new
                    {
                        error = "client_attachment_deleted",
                        message = "이미 삭제된 첨부 식별자입니다. 첨부를 새로 선택한 뒤 다시 시도하세요."
                    });
                }

                return Ok(existingAttachment.ToDto(false));
            }
        }
        else
        {
            attachmentId = Guid.NewGuid();
        }

        var safeFileName = Path.GetFileName(file.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName))
            return BadRequest(new { error = "invalid_file_name", message = "유효한 첨부 파일명을 확인할 수 없습니다." });

        var normalizedContentType = EvidenceAttachmentFilePolicy.NormalizeContentType(file.ContentType, safeFileName);
        if (!EvidenceAttachmentFilePolicy.IsAllowedFileType(safeFileName, normalizedContentType))
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
        if (!EvidenceAttachmentFilePolicy.ContentMatchesFileType(safeFileName, normalizedContentType, bytes))
        {
            return BadRequest(new
            {
                error = "file_content_mismatch",
                message = "첨부 파일 내용이 PDF 또는 이미지 형식과 일치하지 않습니다."
            });
        }

        var entity = new PaymentAttachment
        {
            Id = attachmentId,
            PaymentId = paymentId,
            AttachmentType = string.IsNullOrWhiteSpace(attachmentType) ? "내역첨부" : attachmentType.Trim(),
            Description = description?.Trim() ?? string.Empty,
            FileName = safeFileName,
            MimeType = normalizedContentType,
            FileSize = bytes.LongLength,
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            UploadedAtUtc = DateTime.UtcNow,
            FileContent = []
        };

        var storedPath = await _fileStorage.SaveBytesAsync(
            "payment-attachments",
            paymentId.ToString("N"),
            entity.Id,
            safeFileName,
            bytes,
            cancellationToken);
        entity.StoragePath = storedPath;

        _dbContext.PaymentAttachments.Add(entity);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _fileStorage.DeleteIfExists(storedPath);
            _dbContext.Entry(entity).State = EntityState.Detached;
            throw;
        }

        return Ok(entity.ToDto(false));
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.PaymentEdit)]
    public async Task<ActionResult<PaymentDto>> Create([FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditPayments())
            return Forbid();

        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
        if (invoice is null)
            return BadRequest("Referenced invoice was not found.");
        if (!_officeScopeService.CanWriteOfficeForPayments(invoice.ResponsibleOfficeCode, invoice.TenantCode, invoice.OfficeCode))
            return Forbid();
        if (await ValidateWritableInvoiceRentalBillingProfileAsync(invoice, allowMissingOrDeleted: false, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;
        if (dto.ExpectedRevision > 0 && invoice.Revision != dto.ExpectedRevision)
            return Conflict($"Referenced invoice revision mismatch. client={dto.ExpectedRevision}, server={invoice.Revision}");
        if (await ValidatePaymentAmountAsync(dto, currentPaymentId: null, cancellationToken) is { } paymentValidationError)
            return paymentValidationError;

        var entity = new Payment { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        dto.Id = entity.Id;
        entity.Apply(dto);
        _dbContext.Payments.Add(entity);
        await ProcessedSyncMutationRecorder.RecordAsync(_dbContext, dto, nameof(Payment), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForPaymentInvoicesAsync([invoice], cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var saved = await _dbContext.Payments
            .AsNoTracking()
            .Include(x => x.Attachments)
            .FirstAsync(x => x.Id == entity.Id, cancellationToken);
        return Ok(saved.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.PaymentEdit)]
    public async Task<ActionResult<PaymentDto>> Update(Guid id, [FromBody] PaymentDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditPayments())
            return Forbid();

        var entity = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (entity.Invoice is null || !_officeScopeService.CanWriteOfficeForPayments(entity.Invoice.ResponsibleOfficeCode, entity.Invoice.TenantCode, entity.Invoice.OfficeCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Payment)) is { } conflict)
            return conflict;
        if (await ValidateWritableInvoiceRentalBillingProfileAsync(entity.Invoice, allowMissingOrDeleted: true, cancellationToken) is { } existingRentalProfileScopeError)
            return existingRentalProfileScopeError;

        var targetInvoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
        if (targetInvoice is not null &&
            !_officeScopeService.CanWriteOfficeForPayments(targetInvoice.ResponsibleOfficeCode, targetInvoice.TenantCode, targetInvoice.OfficeCode))
        {
            return Forbid();
        }
        if (await ValidateWritableInvoiceRentalBillingProfileAsync(targetInvoice, allowMissingOrDeleted: false, cancellationToken) is { } targetRentalProfileScopeError)
            return targetRentalProfileScopeError;

        if (await ValidatePaymentAmountAsync(dto, id, cancellationToken) is { } paymentValidationError)
            return paymentValidationError;

        var linkedTransaction = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);
        var linkedTransactionRentalTargets = new List<(Guid ProfileId, Guid? RunId)>();
        if (linkedTransaction is not null && !linkedTransaction.IsDeleted)
        {
            if (linkedTransaction.LinkedInvoiceId.HasValue &&
                linkedTransaction.LinkedInvoiceId.Value != entity.InvoiceId)
            {
                return Conflict("Linked transaction invoice does not match the payment invoice.");
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(
                    linkedTransaction.ResponsibleOfficeCode,
                    linkedTransaction.TenantCode,
                    linkedTransaction.OfficeCode))
            {
                return Forbid();
            }

            if (await ValidateWritableRentalBillingProfileAsync(
                    linkedTransaction.LinkedRentalBillingProfileId,
                    allowMissingOrDeleted: true,
                    cancellationToken) is { } linkedTransactionRentalProfileScopeError)
            {
                return linkedTransactionRentalProfileScopeError;
            }

            AddRentalSettlementTarget(
                linkedTransactionRentalTargets,
                linkedTransaction.LinkedRentalBillingProfileId,
                linkedTransaction.LinkedRentalBillingRunId);
        }

        var previousInvoice = entity.Invoice;
        entity.Apply(dto);
        if (linkedTransaction is not null && !linkedTransaction.IsDeleted && targetInvoice is not null)
        {
            SynchronizeLinkedTransactionFromPayment(linkedTransaction, entity, targetInvoice);
            AddRentalSettlementTarget(
                linkedTransactionRentalTargets,
                linkedTransaction.LinkedRentalBillingProfileId,
                linkedTransaction.LinkedRentalBillingRunId);
        }

        await ProcessedSyncMutationRecorder.RecordAsync(_dbContext, dto, nameof(Payment), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForPaymentInvoicesAsync([previousInvoice, targetInvoice], cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(linkedTransactionRentalTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.PaymentEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditPayments())
            return Forbid();

        var entity = await _dbContext.Payments
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();
        if (entity.Invoice is null || !_officeScopeService.CanWriteOfficeForPayments(entity.Invoice.ResponsibleOfficeCode, entity.Invoice.TenantCode, entity.Invoice.OfficeCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Payment)) is { } conflict)
            return conflict;
        if (await ValidateWritableInvoiceRentalBillingProfileAsync(entity.Invoice, allowMissingOrDeleted: true, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;

        var linkedTransaction = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(transaction => transaction.Id == id, cancellationToken);
        if (linkedTransaction is not null && !linkedTransaction.IsDeleted)
        {
            if (linkedTransaction.LinkedInvoiceId.HasValue &&
                linkedTransaction.LinkedInvoiceId.Value != entity.InvoiceId)
            {
                return Conflict("Linked transaction invoice does not match the payment invoice.");
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(
                    linkedTransaction.ResponsibleOfficeCode,
                    linkedTransaction.TenantCode,
                    linkedTransaction.OfficeCode))
            {
                return Forbid();
            }

            if (await ValidateWritableRentalBillingProfileAsync(
                    linkedTransaction.LinkedRentalBillingProfileId,
                    allowMissingOrDeleted: true,
                    cancellationToken) is { } linkedTransactionRentalProfileScopeError)
            {
                return linkedTransactionRentalProfileScopeError;
            }

            linkedTransaction.IsDeleted = true;
            var transactionAttachments = await _dbContext.TransactionAttachments
                .IgnoreQueryFilters()
                .Where(attachment => attachment.TransactionId == id && !attachment.IsDeleted)
                .ToListAsync(cancellationToken);
            foreach (var attachment in transactionAttachments)
                attachment.IsDeleted = true;
        }

        entity.IsDeleted = true;
        var attachments = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .Where(x => x.PaymentId == id && !x.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
            attachment.IsDeleted = true;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForPaymentInvoicesAsync([entity.Invoice], cancellationToken);
        if (linkedTransaction?.LinkedRentalBillingProfileId is Guid linkedRentalProfileId &&
            linkedRentalProfileId != Guid.Empty)
        {
            await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(
                [(linkedRentalProfileId, linkedTransaction.LinkedRentalBillingRunId)],
                cancellationToken);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task RecalculateRentalSettlementsForPaymentInvoicesAsync(
        IEnumerable<Invoice?> invoices,
        CancellationToken cancellationToken)
    {
        var targets = (invoices ?? Enumerable.Empty<Invoice?>())
            .Where(invoice => invoice?.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty)
            .Select(invoice => (ProfileId: invoice!.LinkedRentalBillingProfileId!.Value, RunId: invoice.LinkedRentalBillingRunId))
            .Distinct()
            .ToList();
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(targets, cancellationToken);
    }

    private static void AddRentalSettlementTarget(
        ICollection<(Guid ProfileId, Guid? RunId)> targets,
        Guid? profileId,
        Guid? runId)
    {
        if (profileId.HasValue && profileId.Value != Guid.Empty)
            targets.Add((profileId.Value, runId));
    }

    private static void SynchronizeLinkedTransactionFromPayment(
        TransactionRecord transaction,
        Payment payment,
        Invoice invoice)
    {
        transaction.CustomerId = invoice.CustomerId;
        transaction.TenantCode = invoice.TenantCode;
        transaction.OfficeCode = invoice.OfficeCode;
        transaction.ResponsibleOfficeCode = invoice.ResponsibleOfficeCode;
        transaction.TransactionDate = payment.PaymentDate;
        transaction.LinkedInvoiceId = payment.InvoiceId;
        transaction.LinkedInvoiceNumber = invoice.InvoiceNumber;
        transaction.LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId;
        transaction.LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId;
        transaction.SettlementAmount = payment.Amount;
        transaction.TransactionKind = ResolveLinkedTransactionKind(invoice);
        ApplyLinkedTransactionTotals(transaction, payment.Amount, IsPaymentVoucher(invoice.VoucherType));
    }

    private static string ResolveLinkedTransactionKind(Invoice invoice)
    {
        if (invoice.LinkedRentalBillingProfileId is Guid rentalProfileId && rentalProfileId != Guid.Empty)
            return "렌탈수금";

        return IsPaymentVoucher(invoice.VoucherType)
            ? "전표지급"
            : "전표수금";
    }

    private static bool IsPaymentVoucher(VoucherType voucherType)
        => voucherType is VoucherType.Purchase or VoucherType.Procurement;

    private static void ApplyLinkedTransactionTotals(
        TransactionRecord transaction,
        decimal amount,
        bool isPayment)
    {
        if (isPayment)
        {
            transaction.CashReceipt = 0m;
            transaction.CardReceipt = 0m;
            transaction.BankReceipt = 0m;
            transaction.DiscountApplied = 0m;
            transaction.ReceiptTotal = 0m;
            transaction.PaymentTotal = amount;
            ApplySinglePaymentChannel(transaction, amount);
            return;
        }

        transaction.CashPayment = 0m;
        transaction.CardPayment = 0m;
        transaction.BankPayment = 0m;
        transaction.DiscountReceived = 0m;
        transaction.PaymentTotal = 0m;
        transaction.ReceiptTotal = amount;
        ApplySingleReceiptChannel(transaction, amount);
    }

    private static void ApplySingleReceiptChannel(TransactionRecord transaction, decimal amount)
    {
        var useCash = transaction.CashReceipt != 0m &&
                      transaction.CardReceipt == 0m &&
                      transaction.BankReceipt == 0m &&
                      transaction.DiscountApplied == 0m;
        var useCard = transaction.CardReceipt != 0m &&
                      transaction.CashReceipt == 0m &&
                      transaction.BankReceipt == 0m &&
                      transaction.DiscountApplied == 0m;

        transaction.CashReceipt = useCash ? amount : 0m;
        transaction.CardReceipt = useCard ? amount : 0m;
        transaction.BankReceipt = !useCash && !useCard ? amount : 0m;
        transaction.DiscountApplied = 0m;
    }

    private static void ApplySinglePaymentChannel(TransactionRecord transaction, decimal amount)
    {
        var useCash = transaction.CashPayment != 0m &&
                      transaction.CardPayment == 0m &&
                      transaction.BankPayment == 0m &&
                      transaction.DiscountReceived == 0m;
        var useCard = transaction.CardPayment != 0m &&
                      transaction.CashPayment == 0m &&
                      transaction.BankPayment == 0m &&
                      transaction.DiscountReceived == 0m;

        transaction.CashPayment = useCash ? amount : 0m;
        transaction.CardPayment = useCard ? amount : 0m;
        transaction.BankPayment = !useCash && !useCard ? amount : 0m;
        transaction.DiscountReceived = 0m;
    }

    private async Task<ActionResult?> ValidateWritableInvoiceRentalBillingProfileAsync(
        Invoice? invoice,
        bool allowMissingOrDeleted,
        CancellationToken cancellationToken)
    {
        if (invoice is null ||
            !invoice.LinkedRentalBillingProfileId.HasValue ||
            invoice.LinkedRentalBillingProfileId.Value == Guid.Empty)
        {
            return null;
        }

        return await ValidateWritableRentalBillingProfileAsync(
            invoice.LinkedRentalBillingProfileId,
            allowMissingOrDeleted,
            cancellationToken);
    }

    private async Task<ActionResult?> ValidateWritableRentalBillingProfileAsync(
        Guid? profileId,
        bool allowMissingOrDeleted,
        CancellationToken cancellationToken)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return null;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == profileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode,
                current.OfficeCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
        {
            if (allowMissingOrDeleted)
                return null;

            return BadRequest("Referenced rental billing profile was not found.");
        }

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
            return Forbid();

        return null;
    }

    private async Task<ActionResult?> ValidatePaymentAmountAsync(
        PaymentDto dto,
        Guid? currentPaymentId,
        CancellationToken cancellationToken)
    {
        if (dto.Amount <= 0m)
        {
            return BadRequest(new
            {
                error = "invalid_payment_amount",
                message = "수금/지급 금액은 0보다 커야 합니다."
            });
        }

        var invoice = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Payments)
            .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
        if (invoice is null || invoice.IsDeleted)
        {
            return BadRequest(new
            {
                error = "invoice_not_found",
                message = "연결 전표를 찾을 수 없습니다. 최신 전표를 다시 조회한 뒤 저장하세요."
            });
        }

        if (invoice.Customer is null || invoice.Customer.IsDeleted)
        {
            return BadRequest(new
            {
                error = "invoice_customer_not_found",
                message = "연결 전표의 거래처를 찾을 수 없습니다. 최신 전표/거래처를 다시 조회한 뒤 저장하세요."
            });
        }

        var settledAmount = invoice.Payments
            .Where(payment => !payment.IsDeleted && (!currentPaymentId.HasValue || payment.Id != currentPaymentId.Value))
            .Sum(payment => payment.Amount);
        var outstandingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount);
        if (dto.Amount <= outstandingAmount)
            return null;

        return Conflict(new
        {
            error = "payment_amount_exceeds_outstanding",
            message = $"입력 금액이 현재 잔액보다 {dto.Amount - outstandingAmount:N0}원 많습니다. 최신 전표를 다시 조회한 뒤 금액을 확인하세요.",
            invoiceId = dto.InvoiceId,
            outstandingAmount,
            enteredAmount = dto.Amount
        });
    }

}
