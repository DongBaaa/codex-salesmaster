using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
	private const string ManualStockAdjustmentMovementType = "StockAdjustmentManual";
	private const string InventoryResetToZeroMovementType = "StockResetToZero";
	private const int LocalQueryContainsBatchSize = 500;

	private sealed record CompanyProfileDefaultDefinition(string ProfileName, string OfficeCode, string TradeName, string Representative, string BusinessNumber, string Address, string ContactNumber);
	private readonly record struct LocalStockChangeKey(Guid ItemId, string WarehouseCode);
	private sealed record LocalStockShortage(
		Guid ItemId,
		string WarehouseCode,
		string ItemName,
		string Specification,
		decimal CurrentQuantity,
		decimal RequestedDecrease,
		decimal ShortageQuantity);

	public enum ServerWriteAwaitResult
	{
		Skipped,
		Synced,
		Pending,
		Failed
	}

	private sealed class SyncDispatchSuppressionScope : IDisposable
	{
		private LocalStateService? _owner;

		public SyncDispatchSuppressionScope(LocalStateService owner)
		{
			_owner = owner;
		}

		public void Dispose()
		{
			var localStateService = Interlocked.Exchange(ref _owner, null);
			if (localStateService != null)
			{
				Interlocked.Decrement(ref localStateService._suppressSyncDispatchCount);
			}
		}
	}

	public sealed class TransactionSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int ClearedMissingInvoiceLinkCount { get; set; }

		public int ClearedMissingRentalLinkCount { get; set; }

		public int ResolvedMissingCustomerCount { get; set; }
	}

	public sealed class PaymentSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int MarkedDeletedMissingInvoiceCount { get; set; }
	}

	public sealed class InvoiceSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int ResolvedMissingCustomerCount { get; set; }

		public int MarkedCleanOutOfScopeCount { get; set; }

		public int SkippedOutOfScopeCount { get; set; }
	}

	public sealed class TransactionAttachmentSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int MarkedDeletedMissingTransactionCount { get; set; }

		public int MarkedCleanStaleDeletedCount { get; set; }

		public int MarkedCleanOutOfScopeCount { get; set; }

		public int SkippedOutOfScopeCount { get; set; }
	}

	public sealed class CustomerSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int NormalizedScopeCount { get; set; }

		public int ClearedMissingCategoryCount { get; set; }

		public int ClearedMissingCustomerMasterCount { get; set; }

		public int MarkedCleanOutOfScopeCount { get; set; }

		public int SkippedOutOfScopeCount { get; set; }
	}

	public sealed class CustomerMasterSyncRepairResult
	{
		public int ScannedCount { get; set; }

		public int NormalizedScopeCount { get; set; }

		public int ClearedMissingCategoryCount { get; set; }

		public int MarkedCleanOutOfScopeCount { get; set; }

		public int SkippedOutOfScopeCount { get; set; }
	}

	private sealed record InventoryTimelineEntry(DateOnly OccurredDate, DateTime SortUtc, int Sequence, LocalInvoice? Invoice, LocalInventoryTransfer? Transfer, LocalInventoryMovement? ManualAdjustment);
	private const string CompanyProfileAssignmentPrefix = "CompanyProfile.Assigned.";

	private const long MaxCustomerContractFileSizeBytes = 15728640L;

	private const string CustomerContractDraftPlaceholderFileName = "PDF 미등록";

	private static readonly CompanyProfileDefaultDefinition[] RequiredCompanyProfileDefaults = new CompanyProfileDefaultDefinition[3]
	{
		new CompanyProfileDefaultDefinition("USENET 기본", "USENET", "USENET", "", "", "", ""),
		new CompanyProfileDefaultDefinition("ITWORLD 기본", "ITWORLD", "ITWORLD", "", "", "", ""),
		new CompanyProfileDefaultDefinition("YEONSU 기본", "YEONSU", "YEONSU", string.Empty, string.Empty, string.Empty, string.Empty)
	};

	private readonly LocalDbContext _db;

	private readonly OfficeAccessService _officeAccess;

	private readonly SyncRequestDispatcher _syncRequestDispatcher;

	private readonly SessionState _session;

	private bool _hasPendingSyncEntityChanges;

	private int _suppressSyncDispatchCount;

	private string _pendingSyncEntityTypeSummary = string.Empty;

	private static readonly JsonSerializerOptions AuditJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	private const string PendingMirrorRefreshSettingKey = "Sync.PendingFullMirrorRefresh";

	private static readonly TimeSpan StaleSyncOutboxSentThreshold = TimeSpan.FromMinutes(10.0);

	private static readonly string[] SharedOptionTables = new string[3] { "PriceGradeOptions", "TradeTypeOptions", "ItemCategoryOptions" };






	public event EventHandler? InventoryStateChanged;

public LocalStateService(LocalDbContext db, OfficeAccessService officeAccess, SyncRequestDispatcher syncRequestDispatcher, SessionState session)
	{
		_db = db;
		_officeAccess = officeAccess;
		_syncRequestDispatcher = syncRequestDispatcher;
		_session = session;
		_db.SavingChanges += HandleSavingChanges;
		_db.SavedChanges += HandleSavedChanges;
		_db.SaveChangesFailed += HandleSaveChangesFailed;
	}

	private void HandleSavingChanges(object? sender, SavingChangesEventArgs args)
	{
		string[] array = (from name in (from entry in _db.ChangeTracker.Entries().Where(delegate(EntityEntry entry)
				{
					EntityState state = entry.State;
					bool flag = (uint)(state - 2) <= 2u;
					return flag && entry.Entity is ILocalSyncEntity;
				})
				select entry.Entity.GetType().Name).Distinct()
			orderby name
			select name).ToArray();
		_hasPendingSyncEntityChanges = array.Length != 0;
		_pendingSyncEntityTypeSummary = ((array.Length == 0) ? string.Empty : string.Join(", ", array));
	}

	private void HandleSavedChanges(object? sender, SavedChangesEventArgs args)
	{
		if (_hasPendingSyncEntityChanges && _suppressSyncDispatchCount == 0)
		{
			AppLogger.Info("SYNC", "로컬 변경으로 동기화 예약: " + _pendingSyncEntityTypeSummary);
			_syncRequestDispatcher.RequestDebouncedSync();
		}
		_hasPendingSyncEntityChanges = false;
		_pendingSyncEntityTypeSummary = string.Empty;
	}

	private void HandleSaveChangesFailed(object? sender, SaveChangesFailedEventArgs args)
	{
		_hasPendingSyncEntityChanges = false;
		_pendingSyncEntityTypeSummary = string.Empty;
	}

	public Task<bool> WaitForServerWriteAsync(CancellationToken ct = default(CancellationToken))
	{
		if (!_session.IsLoggedIn || _session.IsOfflineMode)
		{
			return Task.FromResult(result: false);
		}
		_syncRequestDispatcher.RequestFlushSync();
		return _syncRequestDispatcher.WaitForSyncCompletionAsync(ct);
	}

	public async Task<ServerWriteAwaitResult> WaitForServerWriteWithTimeoutAsync(TimeSpan? timeout = null, CancellationToken ct = default(CancellationToken))
	{
		if (!_session.IsLoggedIn || _session.IsOfflineMode)
		{
			return ServerWriteAwaitResult.Skipped;
		}
		_syncRequestDispatcher.RequestFlushSync();
		Task<bool> waitTask = _syncRequestDispatcher.WaitForSyncCompletionAsync(ct);
		TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3.0);
		if (effectiveTimeout <= TimeSpan.Zero)
		{
			return (await waitTask.ConfigureAwait(continueOnCapturedContext: false)) ? ServerWriteAwaitResult.Synced : ServerWriteAwaitResult.Failed;
		}
		Task timeoutTask = Task.Delay(effectiveTimeout);
		if (await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(continueOnCapturedContext: false) != waitTask)
		{
			AppLogger.Warn("SYNC", $"서버 쓰기 확인이 {effectiveTimeout.TotalSeconds:N1}초 안에 끝나지 않아 백그라운드로 넘깁니다.");
			return ServerWriteAwaitResult.Pending;
		}
		bool succeeded = await waitTask.ConfigureAwait(continueOnCapturedContext: false);
		if (!succeeded)
		{
			AppLogger.Warn("SYNC", "서버 쓰기 확인이 실패로 끝나 자동 재시도 상태로 남았습니다.");
		}
		return succeeded ? ServerWriteAwaitResult.Synced : ServerWriteAwaitResult.Failed;
	}

	public static string ComposeServerWriteStatusMessage(string? baseMessage, ServerWriteAwaitResult result)
	{
		string text = (string.IsNullOrWhiteSpace(baseMessage) ? "로컬 저장을 완료했습니다." : baseMessage.Trim());
		if (1 == 0)
		{
		}
		string text2 = result switch
		{
			ServerWriteAwaitResult.Synced => "중앙 서버까지 반영되었습니다.",
			ServerWriteAwaitResult.Pending => "서버 동기화는 백그라운드에서 계속됩니다.",
			ServerWriteAwaitResult.Failed => "중앙 서버 동기화는 자동 재시도합니다.",
			_ => string.Empty,
		};
		if (1 == 0)
		{
		}
		string text3 = text2;
		return string.IsNullOrWhiteSpace(text3) ? text : (text + " " + text3).Trim();
	}

	public IDisposable SuppressSyncDispatch()
	{
		Interlocked.Increment(ref _suppressSyncDispatchCount);
		return new SyncDispatchSuppressionScope(this);
	}

	public async Task<T> RunInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(operation);
		await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct);
		try
		{
			T result = await operation(ct);
			await transaction.CommitAsync(ct);
			return result;
		}
		catch
		{
			await transaction.RollbackAsync(CancellationToken.None);
			throw;
		}
	}

	private static bool CanModifySharedBusinessData(SessionState? session)
	{
		return session?.HasAdministrativePrivileges ?? false;
	}

	private static bool CanManageCustomerContracts(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.CustomerEdit));
	}

	private static bool CanEditItems(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.ItemEdit));
	}

	private static bool CanResetInventoryValue(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.InventoryReset));
	}

	private static bool CanSaveInvoices(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.InvoiceEdit));
	}

	private static bool CanEditPayments(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.PaymentEdit));
	}

	private static bool CanEditDeliveries(SessionState? session)
	{
		return session != null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.DeliveryEdit));
	}

	private static bool CanEditRentalProfiles(SessionState? session)
	{
		return session != null &&
		       (session.HasAdministrativePrivileges ||
		        session.HasPermission(AppPermissionNames.RentalProfileEdit) ||
		        session.HasPermission(AppPermissionNames.RentalEditAll));
	}

	private static bool CanEditRentalAssets(SessionState? session)
	{
		return session != null &&
		       (session.HasAdministrativePrivileges ||
		        session.HasPermission(AppPermissionNames.RentalAssetEdit) ||
		        session.HasPermission(AppPermissionNames.RentalEditAll));
	}

	public Task<List<LocalCustomer>> GetCustomersAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from c in _db.Customers.AsNoTracking()
			orderby c.NameOriginal
			select c).ToListAsync(ct);
	}

	public Task<List<LocalCustomer>> GetCustomersAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalCustomer> query = _db.Customers.AsNoTracking();
		query = ApplyCustomerScope(query, session);
		return query.OrderBy((LocalCustomer c) => c.NameOriginal).ToListAsync(ct);
	}

	public Task<List<LocalCustomer>> GetCustomersForRentalScopeAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return GetCustomersForRentalScopeAsync(session, responsibleOfficeCode: null, ct);
	}

	public Task<List<LocalCustomer>> GetCustomersForRentalScopeAsync(SessionState session, string? responsibleOfficeCode, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalCustomer> source = ApplyRentalCustomerScope(_db.Customers.AsNoTracking(), session);
		if (OfficeCodeCatalog.TryNormalizeOfficeCode(responsibleOfficeCode, out string normalizedOfficeCode))
		{
			source = source.Where((LocalCustomer customer) =>
				customer.ResponsibleOfficeCode == normalizedOfficeCode ||
				((customer.ResponsibleOfficeCode == null ||
				  customer.ResponsibleOfficeCode == string.Empty ||
				  customer.ResponsibleOfficeCode == OfficeCodeCatalog.Shared) &&
				 customer.OfficeCode == normalizedOfficeCode));
		}
		return source.OrderBy((LocalCustomer c) => c.NameOriginal).ToListAsync(ct);
	}

	public Task<List<LocalCustomer>> GetCustomersForOperationalSelectionAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (CanViewAllTenantOperationalCustomers(session))
		{
			string tenantCode = ResolveCurrentTenantCode(session);
			return (from customer in _db.Customers.AsNoTracking()
				where customer.TenantCode == tenantCode
				orderby customer.NameOriginal
				select customer).ToListAsync(ct);
		}
		return GetCustomersAsync(session, ct);
	}

	public async Task<LocalCustomer?> GetCustomerForOperationalSelectionAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await GetCustomerAsync(customerId, ct);
		if (customer == null)
		{
			return null;
		}
		if (CanViewAllTenantOperationalCustomers(session))
		{
			return string.Equals(customer.TenantCode, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase) ? customer : null;
		}
		return CanAccessCustomer(customer, session) ? customer : null;
	}

	public Task<List<LocalCustomer>> GetCustomersForDeliveryScopeAsync(SessionState session, string? responsibleOfficeCode = null, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalCustomer> queryable = _db.Customers.AsNoTracking();
		if (!CanViewAllDeliveryScope(session))
		{
			queryable = ApplyCustomerScope(queryable, session);
		}
		string text = (responsibleOfficeCode ?? string.Empty).Trim();
		string normalizedOfficeCode = (string.IsNullOrWhiteSpace(text) ? string.Empty : NormalizeOfficeCode(text, DomainConstants.OfficeYeonsu));
		if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
		{
			queryable = queryable.Where((LocalCustomer customer) =>
				customer.ResponsibleOfficeCode == normalizedOfficeCode ||
				customer.ResponsibleOfficeCode == "ALL" ||
				((customer.ResponsibleOfficeCode == null ||
				  customer.ResponsibleOfficeCode == string.Empty ||
				  customer.ResponsibleOfficeCode == OfficeCodeCatalog.Shared) &&
				 customer.OfficeCode == normalizedOfficeCode));
		}
		return queryable.OrderBy((LocalCustomer c) => c.NameOriginal).ToListAsync(ct);
	}

	public async Task<Dictionary<Guid, string>> GetCustomerNameMapAsync(IEnumerable<Guid> customerIds, CancellationToken ct = default(CancellationToken))
	{
		List<Guid> ids = customerIds.Where((Guid id) => id != Guid.Empty).Distinct().ToList();
		if (ids.Count == 0)
		{
			return new Dictionary<Guid, string>();
		}
		return await (from customer in _db.Customers.IgnoreQueryFilters().AsNoTracking()
			where ids.Contains(customer.Id)
			select customer).ToDictionaryAsync((LocalCustomer customer) => customer.Id, (LocalCustomer customer) => customer.NameOriginal, EqualityComparer<Guid>.Default, ct);
	}

	public async Task<Dictionary<Guid, LocalItem>> GetItemMapAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default(CancellationToken))
	{
		List<Guid> ids = itemIds.Where((Guid id) => id != Guid.Empty).Distinct().ToList();
		if (ids.Count == 0)
		{
			return new Dictionary<Guid, LocalItem>();
		}
		return await (from item in _db.Items.IgnoreQueryFilters().AsNoTracking()
			where ids.Contains(item.Id)
			select item).ToDictionaryAsync((LocalItem item) => item.Id, (LocalItem item) => item, EqualityComparer<Guid>.Default, ct);
	}

	public Task<LocalCustomer?> GetCustomerAsync(Guid customerId, CancellationToken ct = default(CancellationToken))
	{
		return _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer customer) => customer.Id == customerId, ct);
	}

	public async Task<LocalCustomer?> GetCustomerAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await GetCustomerAsync(customerId, ct);
		return (customer != null && CanAccessCustomer(customer, session)) ? customer : null;
	}

	public async Task<LocalCustomer?> GetCustomerForRentalScopeAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await GetCustomerAsync(customerId, ct);
		if (customer == null)
		{
			return null;
		}
		return (CanReadRentalCustomerScope(session, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode) || CanAccessCustomer(customer, session)) ? customer : null;
	}

	public async Task<LocalCustomer> UpsertCustomerAsync(LocalCustomer customer, CancellationToken ct = default(CancellationToken))
	{
		NormalizeCustomerClassification(customer);
		string ownerOfficeFallback = NormalizeOfficeScope(customer.OfficeCode, DomainConstants.OfficeUsenet);
		customer.ResponsibleOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, ownerOfficeFallback);
		customer.OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(customer.OfficeCode, customer.ResponsibleOfficeCode, customer.ResponsibleOfficeCode);
		customer.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, customer.ResponsibleOfficeCode);
		var existing = await _db.Customers.FindAsync(new object[1] { customer.Id }, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		string previousCustomerName = existing?.NameOriginal ?? string.Empty;
		DateTime now = DateTime.UtcNow;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, customer, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(customer, existing, "거래처", now, out string conflictMessage))
		{
			throw new InvalidOperationException(conflictMessage);
		}
		if (existing == null)
		{
			_db.Customers.Add(customer);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(customer);
		}
		if (existing != null && !AreCustomerDisplayNamesEquivalent(previousCustomerName, customer.NameOriginal))
		{
			await SynchronizeLinkedRentalCustomerNamesForCustomerRenameAsync(customer, previousCustomerName, ct);
		}
		await _db.SaveChangesAsync(ct);
		return customer;
	}

	public async Task<OfficeMutationResult> UpsertCustomerAsync(LocalCustomer customer, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (customer == null)
		{
			throw new ArgumentNullException("customer");
		}
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("현재 계정은 거래처를 저장할 권한이 없습니다.");
		}
		NormalizeCustomerClassification(customer);
		string normalizedOwnerFallback = NormalizeOfficeScope(customer.OfficeCode, NormalizeOfficeScope(session.OfficeCode, DomainConstants.OfficeUsenet));
		string normalizedOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, normalizedOwnerFallback);
		string normalizedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(customer.OfficeCode, normalizedOfficeCode, normalizedOfficeCode);
		string normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, normalizedOfficeCode);
		var existing = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customer.Id, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		string previousCustomerName = existing?.NameOriginal ?? string.Empty;
		if (existing != null)
		{
			NormalizeOfficeScope(existing.ResponsibleOfficeCode, normalizedOfficeCode);
		}
		if ((existing != null && !CanWriteCustomerScope(session, existing.ResponsibleOfficeCode, existing.TenantCode, existing.OfficeCode)) || !CanWriteCustomerScope(session, normalizedOfficeCode, normalizedTenantCode, normalizedOwnerOfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 저장할 수 없습니다.");
		}
		customer.ResponsibleOfficeCode = normalizedOfficeCode;
		customer.OfficeCode = normalizedOwnerOfficeCode;
		customer.TenantCode = normalizedTenantCode;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, customer, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(now: DateTime.UtcNow, candidate: customer, existing: existing, entityDisplayName: "거래처", conflictMessage: out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (existing == null)
		{
			_db.Customers.Add(customer);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(customer);
		}
		if (existing != null && !AreCustomerDisplayNamesEquivalent(previousCustomerName, customer.NameOriginal))
		{
			await SynchronizeLinkedRentalCustomerNamesForCustomerRenameAsync(customer, previousCustomerName, ct);
		}
		await _db.SaveChangesAsync(ct);
		_officeAccess.RevokeTemporaryCustomerAccess(session, customer.Id);
		return OfficeMutationResult.Ok(customer.Id, "거래처를 저장했습니다.");
	}

	public async Task DeleteCustomerAsync(Guid id, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		var customer = await _db.Customers.FindAsync(new object[1] { id }, ct);
		customer = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, customer, ct);
		if (customer != null)
		{
			if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(customer, expectedRevision, "거래처", out string conflictMessage))
			{
				throw new InvalidOperationException(conflictMessage);
			}
			var referenceBlockMessage = await BuildCustomerDeletionReferenceBlockMessageAsync(id, ct);
			if (referenceBlockMessage != null)
			{
				throw new InvalidOperationException(referenceBlockMessage);
			}
			customer.IsDeleted = true;
			customer.IsDirty = true;
			customer.UpdatedAtUtc = DateTime.UtcNow;
			await SoftDeleteCustomerContractsAsync(id, ct);
			await _db.SaveChangesAsync(ct);
		}
	}

	public async Task<OfficeMutationResult> DeleteCustomerAsync(Guid id, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("현재 계정은 거래처를 삭제할 권한이 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == id, ct);
		customer = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, customer, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");
		}
		if (!CanWriteCustomerScope(session, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(customer, expectedRevision, "거래처", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		var referenceBlockMessage = await BuildCustomerDeletionReferenceBlockMessageAsync(id, ct);
		if (referenceBlockMessage != null)
		{
			return OfficeMutationResult.Denied(referenceBlockMessage);
		}
		customer.IsDeleted = true;
		customer.IsDirty = true;
		customer.UpdatedAtUtc = DateTime.UtcNow;
		await SoftDeleteCustomerContractsAsync(id, ct);
		await _db.SaveChangesAsync(ct);
		_officeAccess.RevokeTemporaryCustomerAccess(session, id);
		return OfficeMutationResult.Ok(id, "거래처를 삭제했습니다.");
	}

	public async Task<List<LocalCustomerContract>> GetCustomerContractsAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customerId, ct);
		if (customer == null || !CanAccessCustomer(customer, session))
		{
			return new List<LocalCustomerContract>();
		}
		return await (from current in _db.CustomerContracts.AsNoTracking()
			where current.CustomerId == customerId
			orderby current.IsPrimary descending, current.SignedDate descending, current.UploadedAtUtc descending
			select current).ToListAsync(ct);
	}

	public async Task<LocalCustomerContract?> GetPreferredCustomerContractAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		List<LocalCustomerContract> contracts = await GetCustomerContractsAsync(customerId, session, ct);
		return contracts.FirstOrDefault(contract => contract.FileSize > 0 && !string.IsNullOrWhiteSpace(contract.FileName))
			?? contracts.FirstOrDefault();
	}

	public async Task<bool> CacheCustomerContractContentAsync(Guid contractId, byte[] fileContent, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (contractId == Guid.Empty || fileContent.Length == 0)
		{
			return false;
		}
		var contract = await _db.CustomerContracts.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerContract current) => current.Id == contractId, ct);
		if (contract == null || contract.IsDeleted)
		{
			return false;
		}
		var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == contract.CustomerId, ct);
		if (customer == null || !CanAccessCustomer(customer, session))
		{
			return false;
		}
		if (contract.FileSize > 0 && fileContent.LongLength != contract.FileSize)
		{
			throw new InvalidOperationException($"계약서 파일 크기가 서버 기록과 일치하지 않습니다. 기록 {contract.FileSize:N0}바이트, 실제 {fileContent.LongLength:N0}바이트입니다.");
		}
		if (!string.IsNullOrWhiteSpace(contract.FileHash))
		{
			var actualHash = ComputeFileHash(fileContent);
			if (!string.Equals(contract.FileHash, actualHash, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("계약서 파일 해시가 서버 기록과 일치하지 않습니다. 운영 점검에서 파일 저장소 무결성을 확인하세요.");
			}
		}
		contract.FileContent = fileContent;
		contract.IsDirty = false;
		using (SuppressSyncDispatch())
		{
			await _db.SaveChangesAsync(ct);
		}
		return true;
	}

	public async Task<LocalCustomerContract?> GetRepresentativeCustomerContractAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		List<LocalCustomerContract> contracts = await GetCustomerContractsAsync(customerId, session, ct);
		return contracts.FirstOrDefault();
	}

	public async Task<Dictionary<Guid, CustomerContractSummaryItem>> GetCustomerContractSummaryMapAsync(SessionState session, int alertWindowDays = 30, CancellationToken ct = default(CancellationToken))
	{
		var customerRows = await (from customer in ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
			select new { customer.Id, customer.NameOriginal }).ToListAsync(ct);
		if (customerRows.Count == 0)
		{
			return new Dictionary<Guid, CustomerContractSummaryItem>();
		}
		List<Guid> customerIds = customerRows.Select(row => row.Id).ToList();
		List<LocalCustomerContract> contracts = await (from contract in _db.CustomerContracts.AsNoTracking()
			where customerIds.Contains(contract.CustomerId)
			select contract).ToListAsync(ct);
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly alertLimit = today.AddDays(Math.Max(alertWindowDays, 0));
		Dictionary<Guid, List<LocalCustomerContract>> contractLookup = (from contract in contracts
			group contract by contract.CustomerId).ToDictionary((IGrouping<Guid, LocalCustomerContract> group) => group.Key, (IGrouping<Guid, LocalCustomerContract> group) => group.ToList());
		Dictionary<Guid, CustomerContractSummaryItem> result = new Dictionary<Guid, CustomerContractSummaryItem>();
		foreach (var customerRow in customerRows)
		{
			contractLookup.TryGetValue(customerRow.Id, out var items);
			if (items == null)
			{
				items = new List<LocalCustomerContract>();
			}
			int expiringSoonCount = items.Count((LocalCustomerContract contract) => contract.ExpireDate.HasValue && contract.ExpireDate.Value >= today && contract.ExpireDate.Value <= alertLimit);
			DateOnly nearestExpireDate = (from contract in items
				where contract.ExpireDate.HasValue
				select contract.ExpireDate.GetValueOrDefault() into date
				orderby date
				select date).FirstOrDefault();
			result[customerRow.Id] = new CustomerContractSummaryItem
			{
				CustomerId = customerRow.Id,
				ContractCount = items.Count,
				NearestExpireDate = ((nearestExpireDate == default(DateOnly)) ? ((DateOnly?)null) : new DateOnly?(nearestExpireDate)),
				ExpiringSoonCount = expiringSoonCount,
				HasExpiredContract = items.Any((LocalCustomerContract contract) => contract.ExpireDate.HasValue && contract.ExpireDate.Value < today)
			};
			items = null;
		}
		return result;
	}

	public async Task<List<CustomerContractAlertItem>> GetCustomerContractAlertsAsync(SessionState session, int alertWindowDays = 30, CancellationToken ct = default(CancellationToken))
	{
		var customerRows = await (from customer in ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
			select new { customer.Id, customer.NameOriginal }).ToListAsync(ct);
		if (customerRows.Count == 0)
		{
			return new List<CustomerContractAlertItem>();
		}
		Dictionary<Guid, string> customerNameMap = customerRows.ToDictionary(row => row.Id, row => row.NameOriginal);
		List<Guid> customerIds = customerNameMap.Keys.ToList();
		DateOnly today = DateOnly.FromDateTime(DateTime.Today);
		DateOnly alertLimit = today.AddDays(Math.Max(alertWindowDays, 0));
		return (await (from contract in _db.CustomerContracts.AsNoTracking()
			where customerIds.Contains(contract.CustomerId) && contract.ExpireDate.HasValue && contract.ExpireDate.Value <= alertLimit
			orderby contract.ExpireDate
			select contract).ToListAsync(ct)).Select((LocalCustomerContract contract) => new CustomerContractAlertItem
		{
			CustomerId = contract.CustomerId,
			ContractId = contract.Id,
			CustomerName = customerNameMap[contract.CustomerId],
			ContractType = contract.ContractType,
			FileName = contract.FileName,
			ExpireDate = contract.ExpireDate.GetValueOrDefault(),
			DaysRemaining = contract.ExpireDate.GetValueOrDefault().DayNumber - today.DayNumber,
			AlertLevel = ((contract.ExpireDate.GetValueOrDefault() < today) ? "만료" : ((contract.ExpireDate.GetValueOrDefault() == today) ? "오늘" : "예정")),
			AlertText = ((contract.ExpireDate.GetValueOrDefault() < today) ? $"만료 {today.DayNumber - contract.ExpireDate.GetValueOrDefault().DayNumber:N0}일 경과" : ((contract.ExpireDate.GetValueOrDefault() == today) ? "오늘 만료" : $"{contract.ExpireDate.GetValueOrDefault().DayNumber - today.DayNumber:N0}일 남음"))
		}).ToList();
	}

	public async Task<OfficeMutationResult> SaveCustomerContractAsync(Guid customerId, string sourceFilePath, string contractType, DateOnly? signedDate, DateOnly? expireDate, string? description, bool isPrimary, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 등록할 수 있습니다.");
		}
		if (customerId == Guid.Empty)
		{
			return OfficeMutationResult.Denied("계약서를 등록할 거래처를 먼저 저장하세요.");
		}
		FileInfo? fileInfo;
		var fileValidationResult = TryValidateCustomerContractPdf(sourceFilePath, out fileInfo);
		if (fileValidationResult != null || fileInfo == null)
		{
			return fileValidationResult ?? OfficeMutationResult.Missing("등록할 PDF 파일을 찾을 수 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 등록할 수 없습니다.");
		}
		DateTime now = DateTime.UtcNow;
		byte[] fileBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
		LocalCustomerContract contract = BuildCustomerContractEntity(Guid.NewGuid(), customerId, NormalizeCustomerContractType(contractType), fileInfo.Name, "application/pdf", fileInfo.Length, ComputeFileHash(fileBytes), (description ?? string.Empty).Trim(), signedDate, expireDate, isPrimary, session.User?.Username ?? "local-user", now, fileBytes);
		if (contract.IsPrimary)
		{
			await ClearPrimaryCustomerContractAsync(customerId, contract.Id, ct);
		}
		_db.CustomerContracts.Add(contract);
		AddCustomerContractAuditLog(contract, "Create", session, now);
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "거래처 계약서를 등록했습니다.");
	}

	public async Task<OfficeMutationResult> SaveCustomerContractDraftAsync(Guid customerId, string contractType, DateOnly? signedDate, DateOnly? expireDate, string? description, bool isPrimary, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 등록할 수 있습니다.");
		}
		if (customerId == Guid.Empty)
		{
			return OfficeMutationResult.Denied("계약서를 등록할 거래처를 먼저 저장하세요.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 등록할 수 없습니다.");
		}
		string normalizedContractType = NormalizeCustomerContractType(contractType);
		string trimmedDescription = (description ?? string.Empty).Trim();
		if (!signedDate.HasValue && !expireDate.HasValue && string.IsNullOrWhiteSpace(trimmedDescription) && string.Equals(normalizedContractType, "거래계약서", StringComparison.Ordinal) && !isPrimary)
		{
			return OfficeMutationResult.Missing("저장할 계약서 초안 정보가 없습니다.");
		}
		DateTime now = DateTime.UtcNow;
		LocalCustomerContract contract = BuildCustomerContractEntity(Guid.NewGuid(), customerId, normalizedContractType, "PDF 미등록", string.Empty, 0L, string.Empty, trimmedDescription, signedDate, expireDate, isPrimary, session.User?.Username ?? "local-user", now, Array.Empty<byte>());
		if (contract.IsPrimary)
		{
			await ClearPrimaryCustomerContractAsync(customerId, contract.Id, ct);
		}
		_db.CustomerContracts.Add(contract);
		AddCustomerContractAuditLog(contract, "CreateDraft", session, now);
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "계약서 초안을 저장했습니다.");
	}

	public async Task<OfficeMutationResult> AttachCustomerContractPdfAsync(Guid contractId, string sourceFilePath, string contractType, DateOnly? signedDate, DateOnly? expireDate, string? description, bool isPrimary, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 등록할 수 있습니다.");
		}
		FileInfo? fileInfo;
		var fileValidationResult = TryValidateCustomerContractPdf(sourceFilePath, out fileInfo);
		if (fileValidationResult != null || fileInfo == null)
		{
			return fileValidationResult ?? OfficeMutationResult.Missing("등록할 PDF 파일을 찾을 수 없습니다.");
		}
		var contract = await _db.CustomerContracts.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerContract current) => current.Id == contractId, ct);
		contract = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, contract, ct);
		if (contract == null)
		{
			return OfficeMutationResult.Missing("PDF를 등록할 계약서 초안을 찾을 수 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == contract.CustomerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 수정할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(contract, expectedRevision, "거래처 계약서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		DateTime now = DateTime.UtcNow;
		byte[] fileBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
		contract.ContractType = NormalizeCustomerContractType(contractType);
		contract.FileName = fileInfo.Name;
		contract.MimeType = "application/pdf";
		contract.FileSize = fileInfo.Length;
		contract.FileHash = ComputeFileHash(fileBytes);
		contract.Description = (description ?? string.Empty).Trim();
		contract.SignedDate = signedDate;
		contract.ExpireDate = expireDate;
		contract.IsPrimary = isPrimary;
		contract.UploadedByUsername = session.User?.Username ?? "local-user";
		contract.UploadedAtUtc = now;
		contract.FileContent = fileBytes;
		contract.UpdatedAtUtc = now;
		contract.IsDirty = true;
		if (contract.IsPrimary)
		{
			await ClearPrimaryCustomerContractAsync(contract.CustomerId, contract.Id, ct);
		}
		AddCustomerContractAuditLog(contract, "AttachPdf", session, now);
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "계약서 PDF를 등록했습니다.");
	}

	public async Task<OfficeMutationResult> UpdateCustomerContractAsync(Guid contractId, string contractType, DateOnly? signedDate, DateOnly? expireDate, string? description, bool isPrimary, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 수정할 수 있습니다.");
		}
		var contract = await _db.CustomerContracts.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerContract current) => current.Id == contractId, ct);
		contract = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, contract, ct);
		if (contract == null)
		{
			return OfficeMutationResult.Missing("수정할 계약서를 찾을 수 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == contract.CustomerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 수정할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(contract, expectedRevision, "거래처 계약서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		DateTime now = DateTime.UtcNow;
		contract.ContractType = NormalizeCustomerContractType(contractType);
		contract.Description = (description ?? string.Empty).Trim();
		contract.SignedDate = signedDate;
		contract.ExpireDate = expireDate;
		if (isPrimary)
		{
			await ClearPrimaryCustomerContractAsync(contract.CustomerId, contract.Id, ct);
		}
		contract.IsPrimary = isPrimary;
		contract.IsDirty = true;
		contract.UpdatedAtUtc = now;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalCustomerContract",
			EntityId = contract.Id.ToString("D"),
			Action = "UpdateMetadata",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new { contract.CustomerId, contract.ContractType, contract.FileName, contract.IsPrimary, contract.SignedDate, contract.ExpireDate }, AuditJsonOptions),
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "계약서 정보를 저장했습니다.");
	}

	private static OfficeMutationResult? TryValidateCustomerContractPdf(string sourceFilePath, out FileInfo? fileInfo)
	{
		fileInfo = null;
		if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
		{
			return OfficeMutationResult.Missing("등록할 PDF 파일을 찾을 수 없습니다.");
		}
		fileInfo = new FileInfo(sourceFilePath);
		if (!string.Equals(fileInfo.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("계약서는 PDF 파일만 등록할 수 있습니다.");
		}
		if (fileInfo.Length <= 0)
		{
			return OfficeMutationResult.Denied("빈 PDF 파일은 등록할 수 없습니다.");
		}
		if (fileInfo.Length > 15728640)
		{
			return OfficeMutationResult.Denied("계약서 PDF는 15MB 이하로 등록해주세요. 스캔 해상도를 낮추거나 PDF를 압축한 뒤 다시 시도하세요.");
		}
		return null;
	}

	private static LocalCustomerContract BuildCustomerContractEntity(Guid contractId, Guid customerId, string contractType, string fileName, string mimeType, long fileSize, string fileHash, string description, DateOnly? signedDate, DateOnly? expireDate, bool isPrimary, string uploadedByUsername, DateTime now, byte[] fileContent)
	{
		return new LocalCustomerContract
		{
			Id = contractId,
			CustomerId = customerId,
			ContractType = contractType,
			FileName = fileName,
			MimeType = mimeType,
			FileSize = fileSize,
			FileHash = fileHash,
			Description = description,
			SignedDate = signedDate,
			ExpireDate = expireDate,
			IsPrimary = isPrimary,
			UploadedByUsername = uploadedByUsername,
			UploadedAtUtc = now,
			FileContent = fileContent,
			CreatedAtUtc = now,
			UpdatedAtUtc = now,
			IsDirty = true
		};
	}

	private void AddCustomerContractAuditLog(LocalCustomerContract contract, string action, SessionState session, DateTime createdAtUtc)
	{
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalCustomerContract",
			EntityId = contract.Id.ToString("D"),
			Action = action,
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new { contract.CustomerId, contract.ContractType, contract.FileName, contract.IsPrimary, contract.SignedDate, contract.ExpireDate }, AuditJsonOptions),
			CreatedAtUtc = createdAtUtc
		});
	}

	public async Task<OfficeMutationResult> DeleteCustomerContractAsync(Guid contractId, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 삭제할 수 있습니다.");
		}
		var contract = await _db.CustomerContracts.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerContract current) => current.Id == contractId, ct);
		contract = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, contract, ct);
		if (contract == null)
		{
			return OfficeMutationResult.Missing("삭제할 계약서를 찾을 수 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == contract.CustomerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(contract, expectedRevision, "거래처 계약서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		contract.IsDeleted = true;
		contract.IsDirty = true;
		contract.IsPrimary = false;
		contract.UpdatedAtUtc = DateTime.UtcNow;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalCustomerContract",
			EntityId = contract.Id.ToString("D"),
			Action = "Delete",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = JsonSerializer.Serialize(new { contract.CustomerId, contract.ContractType, contract.FileName }, AuditJsonOptions),
			AfterJson = string.Empty,
			CreatedAtUtc = DateTime.UtcNow
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "거래처 계약서를 삭제했습니다.");
	}

	public async Task<OfficeMutationResult> SetPrimaryCustomerContractAsync(Guid contractId, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 기본 계약서를 변경할 수 있습니다.");
		}
		var contract = await _db.CustomerContracts.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerContract current) => current.Id == contractId, ct);
		contract = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, contract, ct);
		if (contract == null)
		{
			return OfficeMutationResult.Missing("대표로 지정할 계약서를 찾을 수 없습니다.");
		}
		var customer = await _db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomer current) => current.Id == contract.CustomerId, ct);
		if (customer == null)
		{
			return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");
		}
		if (!CanAccessCustomer(customer, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 변경할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(contract, expectedRevision, "거래처 계약서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		await ClearPrimaryCustomerContractAsync(contract.CustomerId, contract.Id, ct);
		contract.IsPrimary = true;
		contract.IsDirty = true;
		contract.UpdatedAtUtc = DateTime.UtcNow;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalCustomerContract",
			EntityId = contract.Id.ToString("D"),
			Action = "SetPrimary",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new { contract.CustomerId, contract.ContractType, contract.FileName, contract.IsPrimary }, AuditJsonOptions),
			CreatedAtUtc = DateTime.UtcNow
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(contract.Id, "대표 계약서로 지정했습니다.");
	}

	public Task<List<LocalItem>> GetItemsAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from i in _db.Items.AsNoTracking()
			orderby i.NameOriginal
			select i).ToListAsync(ct);
	}

	public Task<List<LocalItem>> GetItemsAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return (from i in ApplyItemScope(_db.Items.AsNoTracking(), session)
			orderby i.NameOriginal
			select i).ToListAsync(ct);
	}

	public Task<List<LocalItem>> GetItemsForRentalScopeAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return (from i in ApplyRentalItemScope(_db.Items.AsNoTracking(), session)
			orderby i.NameOriginal
			select i).ToListAsync(ct);
	}

	public Task<List<LocalItem>> GetItemsForInventoryTransferAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		string tenantCode = ResolveCurrentTenantCode(session);
		List<string> tenantOfficeCodes = GetTenantOfficeCodes(session);
		return (from item in _db.Items.AsNoTracking()
			where item.TenantCode == tenantCode && (item.OfficeCode == "ALL" || tenantOfficeCodes.Contains(item.OfficeCode))
			orderby item.NameOriginal
			select item).ToListAsync(ct);
	}

	public Task<List<LocalItem>> GetDirtyItemsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditItems(session))
		{
			return Task.FromResult(new List<LocalItem>());
		}
		IQueryable<LocalItem> source = (from item in _db.Items.IgnoreQueryFilters()
			where item.IsDirty
			select item).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return source.ToListAsync(ct);
		}
		HashSet<string> writableOfficeCodes = GetWritableOfficeCodes(session);
		bool allowSharedWrite = CanWriteSharedOfficeScope(session);
		string tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
		return source.Where((LocalItem item) => item.TenantCode == tenantCode && ((allowSharedWrite && item.OfficeCode == "ALL") || writableOfficeCodes.Contains(item.OfficeCode))).ToListAsync(ct);
	}

	public async Task<List<LocalCustomer>> GetDirtyCustomersForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return new List<LocalCustomer>();
		}
		IQueryable<LocalCustomer> query = (from customer in _db.Customers.IgnoreQueryFilters()
			where customer.IsDirty
			select customer).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return await query.ToListAsync(ct);
		}
		return (await query.ToListAsync(ct)).Where((LocalCustomer customer) => CanWriteCustomerScope(session, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode)).ToList();
	}

	public async Task<List<LocalCustomerMaster>> GetDirtyCustomerMastersForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return new List<LocalCustomerMaster>();
		}
		IQueryable<LocalCustomerMaster> query = (from customerMaster in _db.CustomerMasters.IgnoreQueryFilters()
			where customerMaster.IsDirty
			select customerMaster).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return await query.ToListAsync(ct);
		}
		return (await query.ToListAsync(ct)).Where((LocalCustomerMaster customerMaster) => CanWriteCustomerScope(session, customerMaster.OfficeCode, customerMaster.TenantCode)).ToList();
	}

	public async Task<CustomerMasterSyncRepairResult> RepairDirtyCustomerMastersForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		CustomerMasterSyncRepairResult result = new CustomerMasterSyncRepairResult();
		List<LocalCustomerMaster> dirtyCustomerMasters = await (from localCustomerMaster in _db.CustomerMasters.IgnoreQueryFilters()
			where localCustomerMaster.IsDirty
			select localCustomerMaster).ToListAsync(ct);
		if (dirtyCustomerMasters.Count == 0)
		{
			return result;
		}
		HashSet<Guid> validCategoryIds = (await (from category in _db.CustomerCategories.IgnoreQueryFilters()
			where !category.IsDeleted
			select category.Id).ToListAsync(ct)).ToHashSet();
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalCustomerMaster customerMaster in dirtyCustomerMasters)
		{
			result.ScannedCount++;
			string normalizedOfficeCode = NormalizeOfficeScope(customerMaster.OfficeCode, "ALL");
			string normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customerMaster.TenantCode, normalizedOfficeCode);
			if (!string.Equals(customerMaster.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase) || !string.Equals(customerMaster.TenantCode, normalizedTenantCode, StringComparison.OrdinalIgnoreCase))
			{
				customerMaster.OfficeCode = normalizedOfficeCode;
				customerMaster.TenantCode = normalizedTenantCode;
				customerMaster.UpdatedAtUtc = now;
				changed = true;
				result.NormalizedScopeCount++;
			}
			if (customerMaster.CategoryId.HasValue && customerMaster.CategoryId.Value != Guid.Empty && !validCategoryIds.Contains(customerMaster.CategoryId.Value))
			{
				customerMaster.CategoryId = null;
				customerMaster.UpdatedAtUtc = now;
				changed = true;
				result.ClearedMissingCategoryCount++;
			}
			if (!CanWriteAllScopedData(session) && !CanWriteCustomerScope(session, customerMaster.OfficeCode, customerMaster.TenantCode))
			{
				result.SkippedOutOfScopeCount++;
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public async Task<CustomerSyncRepairResult> RepairDirtyCustomersForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		CustomerSyncRepairResult result = new CustomerSyncRepairResult();
		List<LocalCustomer> dirtyCustomers = await (from localCustomer in _db.Customers.IgnoreQueryFilters()
			where localCustomer.IsDirty
			select localCustomer).ToListAsync(ct);
		if (dirtyCustomers.Count == 0)
		{
			return result;
		}
		List<Guid> categoryIds = (from localCustomer in dirtyCustomers
			where localCustomer.CategoryId.HasValue && localCustomer.CategoryId.Value != Guid.Empty
			select localCustomer.CategoryId.GetValueOrDefault()).Distinct().ToList();
		HashSet<Guid> hashSet = ((categoryIds.Count != 0) ? (await (from category in _db.CustomerCategories.IgnoreQueryFilters()
			where categoryIds.Contains(category.Id) && !category.IsDeleted
			select category.Id).ToListAsync(ct)).ToHashSet() : new HashSet<Guid>());
		HashSet<Guid> validCategoryIds = hashSet;
		List<Guid> customerMasterIds = (from localCustomer in dirtyCustomers
			where localCustomer.CustomerMasterId.HasValue && localCustomer.CustomerMasterId.Value != Guid.Empty
			select localCustomer.CustomerMasterId.GetValueOrDefault()).Distinct().ToList();
		Dictionary<Guid, LocalCustomerMaster> dictionary = ((customerMasterIds.Count != 0) ? (await (from localCustomerMaster in _db.CustomerMasters.IgnoreQueryFilters()
			where customerMasterIds.Contains(localCustomerMaster.Id)
			select localCustomerMaster).ToDictionaryAsync((LocalCustomerMaster localCustomerMaster) => localCustomerMaster.Id, ct)) : new Dictionary<Guid, LocalCustomerMaster>());
		Dictionary<Guid, LocalCustomerMaster> customerMasters = dictionary;
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalCustomer customer in dirtyCustomers)
		{
			result.ScannedCount++;
			string normalizedOwnerFallback = NormalizeOfficeScope(customer.OfficeCode, DomainConstants.OfficeUsenet);
			string normalizedOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, normalizedOwnerFallback);
			string normalizedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(customer.OfficeCode, normalizedOfficeCode, normalizedOfficeCode);
			string normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, normalizedOfficeCode);
			if (!string.Equals(customer.ResponsibleOfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase) || !string.Equals(customer.OfficeCode, normalizedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase) || !string.Equals(customer.TenantCode, normalizedTenantCode, StringComparison.OrdinalIgnoreCase))
			{
				customer.ResponsibleOfficeCode = normalizedOfficeCode;
				customer.OfficeCode = normalizedOwnerOfficeCode;
				customer.TenantCode = normalizedTenantCode;
				customer.UpdatedAtUtc = now;
				changed = true;
				result.NormalizedScopeCount++;
			}
			if (customer.CategoryId.HasValue && customer.CategoryId.Value != Guid.Empty && !validCategoryIds.Contains(customer.CategoryId.Value))
			{
				customer.CategoryId = null;
				customer.UpdatedAtUtc = now;
				changed = true;
				result.ClearedMissingCategoryCount++;
			}
			if (customer.CustomerMasterId.HasValue && customer.CustomerMasterId.Value != Guid.Empty)
			{
				if (!customerMasters.TryGetValue(customer.CustomerMasterId.Value, out var customerMaster) || customerMaster.IsDeleted || !CanReadCustomerScope(session, customerMaster.OfficeCode, customerMaster.TenantCode))
				{
					customer.CustomerMasterId = null;
					customer.UpdatedAtUtc = now;
					changed = true;
					result.ClearedMissingCustomerMasterCount++;
				}
			}
			if (!CanWriteAllScopedData(session) && !CanWriteCustomerScope(session, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
			{
				result.SkippedOutOfScopeCount++;
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public async Task<List<LocalCustomerContract>> GetDirtyCustomerContractsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanManageCustomerContracts(session))
		{
			return new List<LocalCustomerContract>();
		}
		List<LocalCustomerContract> dirtyContracts = await (from contract in _db.CustomerContracts.IgnoreQueryFilters()
			where contract.IsDirty
			select contract).AsNoTracking().ToListAsync(ct);
		if (CanWriteAllScopedData(session) || dirtyContracts.Count == 0)
		{
			return dirtyContracts;
		}
		List<Guid> customerIds = (from contract in dirtyContracts
			select contract.CustomerId into customerId
			where customerId != Guid.Empty
			select customerId).Distinct().ToList();
		Dictionary<Guid, (string ResponsibleOfficeCode, string TenantCode, string OfficeCode)> scopeByCustomerId = await (from customer in (from customer in _db.Customers.IgnoreQueryFilters()
				where customerIds.Contains(customer.Id)
				select customer).AsNoTracking()
			select new { customer.Id, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode }).ToDictionaryAsync(customer => customer.Id, customer => (ResponsibleOfficeCode: customer.ResponsibleOfficeCode, TenantCode: customer.TenantCode, OfficeCode: customer.OfficeCode), ct);
		(string, string, string) value;
		return dirtyContracts.Where((LocalCustomerContract contract) => scopeByCustomerId.TryGetValue(contract.CustomerId, out value) && CanWriteCustomerScope(session, value.Item1, value.Item2, value.Item3)).ToList();
	}

	public async Task<List<LocalRentalBillingProfile>> GetDirtyRentalBillingProfilesForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditRentalProfiles(session))
		{
			return new List<LocalRentalBillingProfile>();
		}
		IQueryable<LocalRentalBillingProfile> query = (from profile in _db.RentalBillingProfiles.IgnoreQueryFilters()
			where profile.IsDirty
			select profile).AsNoTracking();
		return (await query.ToListAsync(ct)).Where((LocalRentalBillingProfile profile) => CanWriteRentalEntityScope(session, profile.TenantCode, profile.ResponsibleOfficeCode, profile.ManagementCompanyCode)).ToList();
	}

	public async Task<List<LocalRentalAsset>> GetDirtyRentalAssetsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditRentalAssets(session))
		{
			return new List<LocalRentalAsset>();
		}
		IQueryable<LocalRentalAsset> query = (from asset in _db.RentalAssets.IgnoreQueryFilters()
			where asset.IsDirty
			select asset).AsNoTracking();
		return (await query.ToListAsync(ct)).Where((LocalRentalAsset asset) => CanWriteRentalEntityScope(session, asset.TenantCode, asset.ResponsibleOfficeCode, asset.ManagementCompanyCode)).ToList();
	}

	public async Task<List<LocalRentalAssetAssignmentHistory>> GetDirtyRentalAssetAssignmentHistoriesForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditRentalAssets(session))
		{
			return new List<LocalRentalAssetAssignmentHistory>();
		}
		IQueryable<LocalRentalAssetAssignmentHistory> query = (from history in _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
			join asset in _db.RentalAssets.IgnoreQueryFilters() on history.AssetId equals asset.Id
			where history.IsDirty
			select history).AsNoTracking();
		return (await query.ToListAsync(ct)).Where((LocalRentalAssetAssignmentHistory history) => CanWriteRentalEntityScope(session, history.TenantCode, history.ResponsibleOfficeCode)).ToList();
	}

	public async Task<List<LocalRentalBillingLog>> GetDirtyRentalBillingLogsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditRentalProfiles(session))
		{
			return new List<LocalRentalBillingLog>();
		}
		IQueryable<LocalRentalBillingLog> query = (from log in _db.RentalBillingLogs.IgnoreQueryFilters()
			where log.IsDirty
			select log).AsNoTracking();
		return (await query.ToListAsync(ct)).Where((LocalRentalBillingLog log) => CanWriteRentalBillingLogScope(session, log)).ToList();
	}

	public async Task<List<LocalTransaction>> GetDirtyTransactionsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return new List<LocalTransaction>();
		}
		IQueryable<LocalTransaction> query = (from transaction in _db.Transactions.IgnoreQueryFilters()
			where transaction.IsDirty
			select transaction).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return await query.ToListAsync(ct);
		}
		return (await query.ToListAsync(ct)).Where((LocalTransaction transaction) => CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode)).ToList();
	}

	public async Task<List<LocalTransactionAttachment>> GetDirtyTransactionAttachmentsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return new List<LocalTransactionAttachment>();
		}
		List<LocalTransactionAttachment> dirtyAttachments = await (from attachment in _db.TransactionAttachments.IgnoreQueryFilters()
			where attachment.IsDirty
			select attachment).AsNoTracking().ToListAsync(ct);
		if (CanWriteAllScopedData(session) || dirtyAttachments.Count == 0)
		{
			return dirtyAttachments;
		}
		List<Guid> transactionIds = (from attachment in dirtyAttachments
			select attachment.TransactionId into transactionId
			where transactionId != Guid.Empty
			select transactionId).Distinct().ToList();
		Dictionary<Guid, LocalTransaction> transactions = await (from transaction in _db.Transactions.IgnoreQueryFilters()
			where transactionIds.Contains(transaction.Id)
			select transaction).AsNoTracking().ToDictionaryAsync((LocalTransaction transaction) => transaction.Id, ct);
		return dirtyAttachments.Where((LocalTransactionAttachment attachment) => transactions.TryGetValue(attachment.TransactionId, out var transaction) && CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode)).ToList();
	}

	public async Task<List<LocalPayment>> GetDirtyPaymentsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return new List<LocalPayment>();
		}
		List<LocalPayment> dirtyPayments = await (from payment in _db.Payments.IgnoreQueryFilters()
			where payment.IsDirty
			select payment).AsNoTracking().ToListAsync(ct);
		if (CanWriteAllScopedData(session) || dirtyPayments.Count == 0)
		{
			return dirtyPayments;
		}
		List<Guid> invoiceIds = (from payment in dirtyPayments
			select payment.InvoiceId into invoiceId
			where invoiceId != Guid.Empty
			select invoiceId).Distinct().ToList();
		Dictionary<Guid, LocalInvoice> invoices = await (from invoice in _db.Invoices.IgnoreQueryFilters()
			where invoiceIds.Contains(invoice.Id)
			select invoice).AsNoTracking().ToDictionaryAsync((LocalInvoice invoice) => invoice.Id, ct);
		return dirtyPayments.Where((LocalPayment payment) => !invoices.TryGetValue(payment.InvoiceId, out var invoice) || CanWriteOperationalScope(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode)).ToList();
	}

	public async Task<List<LocalInvoice>> GetDirtyInvoicesForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanSaveInvoices(session))
		{
			return new List<LocalInvoice>();
		}
		IQueryable<LocalInvoice> query = (from invoice in _db.Invoices.IgnoreQueryFilters().Include((LocalInvoice invoice) => invoice.Lines).Include((LocalInvoice invoice) => invoice.Payments)
			where invoice.IsDirty
			select invoice).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return await query.ToListAsync(ct);
		}
		return (await query.ToListAsync(ct)).Where((LocalInvoice invoice) => CanWriteOperationalScope(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode)).ToList();
	}

	public async Task<List<LocalInventoryTransfer>> GetDirtyInventoryTransfersForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditDeliveries(session))
		{
			return new List<LocalInventoryTransfer>();
		}
		IQueryable<LocalInventoryTransfer> query = (from transfer in _db.InventoryTransfers.IgnoreQueryFilters().Include((LocalInventoryTransfer transfer) => transfer.Lines)
			where transfer.IsDirty
			select transfer).AsNoTracking();
		if (CanWriteAllScopedData(session))
		{
			return await query.ToListAsync(ct);
		}
		return (await query.ToListAsync(ct)).Where((LocalInventoryTransfer transfer) => CanWriteOfficeScope(session, ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode)) || CanWriteOfficeScope(session, ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode))).ToList();
	}

	public async Task<InvoiceSyncRepairResult> RepairDirtyInvoicesForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		InvoiceSyncRepairResult result = new InvoiceSyncRepairResult();
		List<LocalInvoice> dirtyInvoices = await (from localInvoice in _db.Invoices.IgnoreQueryFilters()
			where localInvoice.IsDirty
			select localInvoice).ToListAsync(ct);
		if (dirtyInvoices.Count == 0)
		{
			return result;
		}
		List<Guid> customerIds = (from localInvoice in dirtyInvoices
			where localInvoice.CustomerId != Guid.Empty
			select localInvoice.CustomerId).Distinct().ToList();
		Dictionary<Guid, LocalCustomer> dictionary = ((customerIds.Count != 0) ? (await (from customer in _db.Customers.IgnoreQueryFilters()
			where customerIds.Contains(customer.Id)
			select customer).ToDictionaryAsync((LocalCustomer customer) => customer.Id, ct)) : new Dictionary<Guid, LocalCustomer>());
		Dictionary<Guid, LocalCustomer> customers = dictionary;
		List<Guid> versionGroupIds = (from localInvoice in dirtyInvoices
			select (localInvoice.VersionGroupId == Guid.Empty) ? localInvoice.Id : localInvoice.VersionGroupId into id
			where id != Guid.Empty
			select id).Distinct().ToList();
		List<LocalInvoice> list = ((versionGroupIds.Count != 0) ? (await (from localInvoice in _db.Invoices.IgnoreQueryFilters()
			where !localInvoice.IsDeleted && versionGroupIds.Contains((localInvoice.VersionGroupId == Guid.Empty) ? localInvoice.Id : localInvoice.VersionGroupId)
			select localInvoice).AsNoTracking().ToListAsync(ct)) : new List<LocalInvoice>());
		List<LocalInvoice> versionGroupInvoices = list;
		Dictionary<Guid, List<LocalInvoice>> invoicesByVersionGroup = (from localInvoice in versionGroupInvoices
			group localInvoice by (localInvoice.VersionGroupId == Guid.Empty) ? localInvoice.Id : localInvoice.VersionGroupId).ToDictionary((IGrouping<Guid, LocalInvoice> group) => group.Key, (IGrouping<Guid, LocalInvoice> group) => group.ToList());
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalInvoice invoice in dirtyInvoices)
		{
			result.ScannedCount++;
			if (!CanWriteAllScopedData(session) && !CanWriteOperationalScope(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
			{
				result.SkippedOutOfScopeCount++;
			}
			else
			{
				if (invoice.CustomerId != Guid.Empty && customers.TryGetValue(invoice.CustomerId, out var existingCustomer) && !existingCustomer.IsDeleted)
				{
					continue;
				}
				Guid versionGroupId = ((invoice.VersionGroupId == Guid.Empty) ? invoice.Id : invoice.VersionGroupId);
				if (invoicesByVersionGroup.TryGetValue(versionGroupId, out var relatedInvoices))
				{
					LocalCustomer? resolvedCustomer = (from candidate in relatedInvoices
						where candidate.Id != invoice.Id && candidate.CustomerId != Guid.Empty
						select customers.TryGetValue(candidate.CustomerId, out var candidateCustomer) ? candidateCustomer : null).FirstOrDefault((LocalCustomer? candidateCustomer) => candidateCustomer != null && !candidateCustomer.IsDeleted);
					if (resolvedCustomer != null)
					{
						invoice.CustomerId = resolvedCustomer.Id;
						invoice.ResponsibleOfficeCode = NormalizeOfficeScope(invoice.ResponsibleOfficeCode, resolvedCustomer.ResponsibleOfficeCode);
						invoice.IsDirty = true;
						invoice.UpdatedAtUtc = now;
						changed = true;
						result.ResolvedMissingCustomerCount++;
					}
				}
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public async Task<TransactionAttachmentSyncRepairResult> RepairDirtyTransactionAttachmentsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		TransactionAttachmentSyncRepairResult result = new TransactionAttachmentSyncRepairResult();
		List<LocalTransactionAttachment> dirtyAttachments = await (from localTransactionAttachment in _db.TransactionAttachments.IgnoreQueryFilters()
			where localTransactionAttachment.IsDirty
			select localTransactionAttachment).ToListAsync(ct);
		if (dirtyAttachments.Count == 0)
		{
			return result;
		}
		List<Guid> transactionIds = (from localTransactionAttachment in dirtyAttachments
			select localTransactionAttachment.TransactionId into transactionId
			where transactionId != Guid.Empty
			select transactionId).Distinct().ToList();
		Dictionary<Guid, LocalTransaction> dictionary = ((transactionIds.Count != 0) ? (await (from localTransaction in _db.Transactions.IgnoreQueryFilters()
			where transactionIds.Contains(localTransaction.Id)
			select localTransaction).ToDictionaryAsync((LocalTransaction localTransaction) => localTransaction.Id, ct)) : new Dictionary<Guid, LocalTransaction>());
		Dictionary<Guid, LocalTransaction> transactions = dictionary;
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalTransactionAttachment attachment in dirtyAttachments)
		{
			result.ScannedCount++;
			if (transactions.TryGetValue(attachment.TransactionId, out var transaction) && !transaction.IsDeleted)
			{
				if (!CanWriteAllScopedData(session) && !CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode))
				{
					result.SkippedOutOfScopeCount++;
				}
			}
			else if (attachment.IsDeleted)
			{
				attachment.IsDirty = false;
				attachment.UpdatedAtUtc = now;
				changed = true;
				result.MarkedCleanStaleDeletedCount++;
			}
			else
			{
				attachment.IsDeleted = true;
				attachment.IsDirty = true;
				attachment.UpdatedAtUtc = now;
				changed = true;
				result.MarkedDeletedMissingTransactionCount++;
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public async Task<TransactionSyncRepairResult> RepairDirtyTransactionsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		TransactionSyncRepairResult result = new TransactionSyncRepairResult();
		List<LocalTransaction> dirtyTransactions = await (from localTransaction in _db.Transactions.IgnoreQueryFilters()
			where localTransaction.IsDirty
			select localTransaction).ToListAsync(ct);
		if (dirtyTransactions.Count == 0)
		{
			return result;
		}
		if (!CanWriteAllScopedData(session))
		{
			dirtyTransactions = dirtyTransactions.Where((LocalTransaction localTransaction) => CanWriteOfficeScope(session, localTransaction.ResponsibleOfficeCode, localTransaction.OfficeCode)).ToList();
		}
		if (dirtyTransactions.Count == 0)
		{
			return result;
		}
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalTransaction transaction in dirtyTransactions)
		{
			result.ScannedCount++;
			LocalInvoice? linkedInvoice = null;
			if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
			{
				linkedInvoice = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalInvoice current) => current.Id == transaction.LinkedInvoiceId.Value, ct);
				if (linkedInvoice == null || linkedInvoice.IsDeleted)
				{
					if (string.IsNullOrWhiteSpace(transaction.LinkedInvoiceNumber) && linkedInvoice != null)
					{
						transaction.LinkedInvoiceNumber = (string.IsNullOrWhiteSpace(linkedInvoice.InvoiceNumber) ? linkedInvoice.LocalTempNumber : linkedInvoice.InvoiceNumber);
					}
					transaction.LinkedInvoiceId = null;
					if (string.Equals(transaction.TransactionKind, "전표수금", StringComparison.OrdinalIgnoreCase))
					{
						transaction.TransactionKind = "일반수금";
						transaction.SettlementAmount = 0m;
					}
					else if (string.Equals(transaction.TransactionKind, "전표지급", StringComparison.OrdinalIgnoreCase))
					{
						transaction.TransactionKind = "일반지급";
						transaction.SettlementAmount = 0m;
					}
					transaction.UpdatedAtUtc = now;
					transaction.IsDirty = true;
					changed = true;
					result.ClearedMissingInvoiceLinkCount++;
					linkedInvoice = null;
				}
			}
			LocalRentalBillingProfile? linkedProfile = null;
			if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
			{
				linkedProfile = await _db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalRentalBillingProfile current) => current.Id == transaction.LinkedRentalBillingProfileId.Value, ct);
				if (linkedProfile == null || linkedProfile.IsDeleted)
				{
					transaction.LinkedRentalBillingProfileId = null;
					if (string.Equals(transaction.TransactionKind, "렌탈수금", StringComparison.OrdinalIgnoreCase))
					{
						transaction.TransactionKind = "일반수금";
						transaction.SettlementAmount = 0m;
					}
					transaction.UpdatedAtUtc = now;
					transaction.IsDirty = true;
					changed = true;
					result.ClearedMissingRentalLinkCount++;
					linkedProfile = null;
				}
			}
			if (!((await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == transaction.CustomerId, ct))?.IsDeleted ?? true))
			{
				continue;
			}
			LocalCustomer? resolvedCustomer = null;
			if (linkedInvoice != null && linkedInvoice.CustomerId != Guid.Empty)
			{
				resolvedCustomer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == linkedInvoice.CustomerId, ct);
			}
			if ((resolvedCustomer == null || resolvedCustomer.IsDeleted) && linkedProfile != null && linkedProfile.CustomerId.HasValue && linkedProfile.CustomerId.Value != Guid.Empty)
			{
				resolvedCustomer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == linkedProfile.CustomerId.Value, ct);
			}
			if (resolvedCustomer != null && !resolvedCustomer.IsDeleted)
			{
				transaction.CustomerId = resolvedCustomer.Id;
				transaction.ResponsibleOfficeCode = NormalizeOfficeScope(transaction.ResponsibleOfficeCode, resolvedCustomer.ResponsibleOfficeCode);
				transaction.OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(transaction.OfficeCode, transaction.ResponsibleOfficeCode, resolvedCustomer.OfficeCode);
				transaction.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
					transaction.TenantCode,
					transaction.OfficeCode,
					resolvedCustomer.TenantCode,
					transaction.ResponsibleOfficeCode);
				transaction.UpdatedAtUtc = now;
				transaction.IsDirty = true;
				changed = true;
				result.ResolvedMissingCustomerCount++;
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public async Task<PaymentSyncRepairResult> RepairDirtyPaymentsForSyncAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		PaymentSyncRepairResult result = new PaymentSyncRepairResult();
		List<LocalPayment> dirtyPayments = await (from localPayment in _db.Payments.IgnoreQueryFilters()
			where localPayment.IsDirty
			select localPayment).ToListAsync(ct);
		if (dirtyPayments.Count == 0)
		{
			return result;
		}
		List<Guid> invoiceIds = (from localPayment in dirtyPayments
			select localPayment.InvoiceId into invoiceId
			where invoiceId != Guid.Empty
			select invoiceId).Distinct().ToList();
		Dictionary<Guid, LocalInvoice> dictionary = ((invoiceIds.Count != 0) ? (await (from localInvoice in _db.Invoices.IgnoreQueryFilters()
			where invoiceIds.Contains(localInvoice.Id)
			select localInvoice).ToDictionaryAsync((LocalInvoice localInvoice) => localInvoice.Id, ct)) : new Dictionary<Guid, LocalInvoice>());
		Dictionary<Guid, LocalInvoice> invoices = dictionary;
		if (!CanWriteAllScopedData(session))
		{
			dirtyPayments = dirtyPayments.Where((LocalPayment localPayment) => !invoices.TryGetValue(localPayment.InvoiceId, out var invoice) || CanWriteOperationalScope(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode)).ToList();
		}
		if (dirtyPayments.Count == 0)
		{
			return result;
		}
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		foreach (LocalPayment payment in dirtyPayments)
		{
			result.ScannedCount++;
			if (payment.InvoiceId == Guid.Empty || !invoices.TryGetValue(payment.InvoiceId, out var invoice) || invoice.IsDeleted)
			{
				if (!payment.IsDeleted)
				{
					result.MarkedDeletedMissingInvoiceCount++;
				}
				payment.IsDeleted = true;
				payment.IsDirty = true;
				payment.UpdatedAtUtc = now;
				changed = true;
			}
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
		return result;
	}

	public Task<LocalItem?> GetItemAsync(Guid itemId, CancellationToken ct = default(CancellationToken))
	{
		return _db.Items.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalItem item) => item.Id == itemId, ct);
	}

	public async Task<string> GetLatestPurchaseVendorNameAsync(Guid itemId, CancellationToken ct = default(CancellationToken))
	{
		if (itemId == Guid.Empty)
		{
			return string.Empty;
		}
		return (await (from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
			join line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking() on invoice.Id equals line.InvoiceId
			join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking() on invoice.CustomerId equals customer.Id
			where !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && !line.IsDeleted && line.ItemId == itemId && ((int)invoice.VoucherType == 1 || (int)invoice.VoucherType == 2)
			orderby invoice.InvoiceDate descending, invoice.UpdatedAtUtc descending
			select customer.NameOriginal).FirstOrDefaultAsync(ct))?.Trim() ?? string.Empty;
	}

	public async Task<LocalItem> UpsertItemAsync(LocalItem item, CancellationToken ct = default(CancellationToken))
	{
		return await UpsertItemAsync(item, (string?)null, ct);
	}

	public async Task<string> EnsureItemCategoryOptionForImportAsync(string? categoryName, CancellationToken ct = default(CancellationToken))
	{
		return await EnsureItemCategoryOptionExistsAsync(categoryName, ct, allowCreateOrReactivate: true);
	}

	public async Task<LocalItem> UpsertItemAsync(LocalItem item, string? preferredOfficeCode, CancellationToken ct = default(CancellationToken))
	{
		return await UpsertItemAsync(item, preferredOfficeCode, synchronizeLinkedRentalAssets: true, ct);
	}

	private async Task<LocalItem> UpsertItemAsync(LocalItem item, string? preferredOfficeCode, bool synchronizeLinkedRentalAssets, CancellationToken ct = default(CancellationToken))
	{
		item.NameOriginal = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal);
		item.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal);
		item.SpecificationOriginal = RentalCatalogValueNormalizer.NormalizeDisplayText(item.SpecificationOriginal);
		item.SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.SpecificationOriginal);
		item.CategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(item.CategoryName);
		NormalizeItemOperationalState(item);
		NormalizeItemScope(item, preferredOfficeCode);
		item.CategoryName = await EnsureItemCategoryOptionExistsAsync(item.CategoryName, ct);
		var existing = await _db.Items.FindAsync(new object[1] { item.Id }, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		string previousItemName = existing?.NameOriginal ?? string.Empty;
		string previousCategoryName = existing?.CategoryName ?? string.Empty;
		DateTime now = DateTime.UtcNow;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, item, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(item, existing, "품목", now, out string conflictMessage))
		{
			throw new InvalidOperationException(conflictMessage);
		}
		if (existing == null)
		{
			_db.Items.Add(item);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(item);
		}
		if (synchronizeLinkedRentalAssets && existing != null)
		{
			await SynchronizeLinkedRentalAssetItemMetadataForItemSaveAsync(item, previousItemName, previousCategoryName, ct);
		}
		await SyncItemWarehouseStocksAsync(item.Id, item.CurrentStock, preferredOfficeCode, !ItemOperationalPolicy.SupportsInventory(item.TrackingType), ct);
		await _db.SaveChangesAsync(ct);
		RaiseInventoryStateChanged();
		return item;
	}

	public async Task<decimal> GetOfficeStockQuantityAsync(Guid itemId, string? officeCode, CancellationToken ct = default(CancellationToken))
	{
		string warehouseCode = ResolvePrimaryWarehouseCode(officeCode);
		return await (from stock in _db.ItemWarehouseStocks.AsNoTracking()
			where stock.ItemId == itemId && stock.WarehouseCode == warehouseCode
			select stock.Quantity).FirstOrDefaultAsync(ct);
	}

	public async Task<int> RepairNegativeItemWarehouseStocksAsync(CancellationToken ct = default(CancellationToken))
	{
		await Task.CompletedTask;
		return 0;
	}

	public async Task<LocalItem?> SetItemOfficeStockAsync(Guid itemId, decimal quantity, string? officeCode, CancellationToken ct = default(CancellationToken))
	{
		if (quantity < 0m)
		{
			throw new InvalidOperationException("재고 수량은 0 이상으로 입력하세요.");
		}
		var item = await _db.Items.FindAsync(new object[1] { itemId }, ct);
		if (item == null)
		{
			return null;
		}
		string warehouseCode = ResolvePrimaryWarehouseCode(officeCode);
		List<LocalItemWarehouseStock> stocks = await _db.ItemWarehouseStocks.Where((LocalItemWarehouseStock stock) => stock.ItemId == itemId).ToListAsync(ct);
		var targetStock = stocks.FirstOrDefault((LocalItemWarehouseStock stock) => string.Equals(stock.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase));
		var previousQuantity = targetStock?.Quantity ?? 0m;
		var quantityDelta = quantity - previousQuantity;
		var now = DateTime.UtcNow;
		if (quantity == 0m)
		{
			if (targetStock != null)
			{
				_db.ItemWarehouseStocks.Remove(targetStock);
				stocks.Remove(targetStock);
			}
		}
		else if (targetStock == null)
		{
			targetStock = new LocalItemWarehouseStock
			{
				ItemId = itemId,
				WarehouseCode = warehouseCode,
				Quantity = quantity,
				UpdatedAtUtc = now
			};
			_db.ItemWarehouseStocks.Add(targetStock);
			stocks.Add(targetStock);
		}
		else
		{
			targetStock.Quantity = quantity;
			targetStock.UpdatedAtUtc = now;
		}
		if (quantityDelta != 0m && ItemOperationalPolicy.SupportsInventory(item.TrackingType))
		{
			var unitCost = Math.Max(0m, item.PurchasePrice);
			_db.InventoryMovements.Add(new LocalInventoryMovement
			{
				Id = Guid.NewGuid(),
				ItemId = itemId,
				WarehouseCode = warehouseCode,
				MovementType = ManualStockAdjustmentMovementType,
				QuantityDelta = quantityDelta,
				UnitCost = unitCost,
				Amount = Math.Round(Math.Abs(quantityDelta) * unitCost, 2, MidpointRounding.AwayFromZero),
				OccurredDate = DateOnly.FromDateTime(DateTime.Today),
				IsSettledCost = true,
				IsActive = true,
				Note = $"수동 재고조정: {previousQuantity:N2} → {quantity:N2}",
				CreatedByUsername = "local-user",
				CreatedAtUtc = now
			});
		}
		item.CurrentStock = stocks.Sum((LocalItemWarehouseStock stock) => stock.Quantity);
		item.IsDirty = true;
		item.UpdatedAtUtc = now;
		await _db.SaveChangesAsync(ct);
		RaiseInventoryStateChanged();
		return item;
	}

	public async Task DeleteItemAsync(Guid id, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		var item = await _db.Items.FindAsync(new object[1] { id }, ct);
		item = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, item, ct);
		if (item != null)
		{
			if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(item, expectedRevision, "품목", out string conflictMessage))
			{
				throw new InvalidOperationException(conflictMessage);
			}
			item.IsDeleted = true;
			item.IsDirty = true;
			item.CurrentStock = 0m;
			item.UpdatedAtUtc = DateTime.UtcNow;
			await SyncItemWarehouseStocksAsync(id, 0m, null, removeStocks: true, ct);
			await _db.SaveChangesAsync(ct);
			RaiseInventoryStateChanged();
		}
	}

	public async Task<OfficeMutationResult> DeleteItemAsync(Guid id, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditItems(session))
		{
			return OfficeMutationResult.Denied("현재 계정은 품목을 삭제할 권한이 없습니다.");
		}
		var item = await _db.Items.IgnoreQueryFilters().FirstOrDefaultAsync((LocalItem current) => current.Id == id && !current.IsDeleted, ct);
		item = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, item, ct);
		if (item == null)
		{
			return OfficeMutationResult.Missing("삭제할 품목을 찾을 수 없습니다.");
		}
		if (!CanWriteItemScope(item, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 품목을 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(item, expectedRevision, "품목", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		item.IsDeleted = true;
		item.IsDirty = true;
		item.CurrentStock = 0m;
		item.UpdatedAtUtc = DateTime.UtcNow;
		await SyncItemWarehouseStocksAsync(id, 0m, null, removeStocks: true, ct);
		await _db.SaveChangesAsync(ct);
		RaiseInventoryStateChanged();
		return OfficeMutationResult.Ok(id, "품목을 삭제했습니다.");
	}

	public async Task<OfficeMutationResult> ResetItemInventoryValueAsync(Guid itemId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanResetInventoryValue(session))
		{
			return OfficeMutationResult.Denied("현재 계정은 재고 초기화를 실행할 권한이 없습니다. 관리자 계정으로 로그인하거나 관리자에게 요청하세요.");
		}
		DateTime now = DateTime.UtcNow;
		OfficeMutationResult result;
		await using (IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(ct))
		{
			var item = await _db.Items.IgnoreQueryFilters().FirstOrDefaultAsync((LocalItem current) => current.Id == itemId && !current.IsDeleted, ct);
			if (item == null)
			{
				result = OfficeMutationResult.Missing("초기화할 품목을 찾을 수 없습니다.");
			}
			else if (!CanWriteItemScope(item, session))
			{
				result = OfficeMutationResult.Denied("권한이 없어 해당 품목의 재고를 초기화할 수 없습니다.");
			}
			else
			{
				var warehouseCodes = await ResolveInventoryResetWarehouseCodesAsync(itemId, item, ct);
				var occurredDate = await ResolveInventoryResetOccurredDateAsync(itemId, ct);
				var username = session.User?.Username ?? "local-user";
				var unitCost = Math.Max(0m, item.PurchasePrice);
				foreach (var warehouseCode in warehouseCodes)
				{
					_db.InventoryMovements.Add(new LocalInventoryMovement
					{
						Id = Guid.NewGuid(),
						ItemId = itemId,
						WarehouseCode = warehouseCode,
						MovementType = InventoryResetToZeroMovementType,
						QuantityDelta = 0m,
						UnitCost = unitCost,
						Amount = 0m,
						OccurredDate = occurredDate,
						IsSettledCost = true,
						IsActive = true,
						Note = "재고 초기화",
						CreatedByUsername = username,
						CreatedAtUtc = now
					});
				}

				await _db.SaveChangesAsync(ct);
				await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
				{
					Username = username,
					Role = session.User?.Role ?? string.Empty,
					OfficeCode = session.OfficeCode
				}, ct);

				var refreshed = await _db.Items.IgnoreQueryFilters().FirstAsync((LocalItem current) => current.Id == itemId, ct);
				refreshed.CurrentStock = 0m;
				refreshed.IsDirty = true;
				refreshed.UpdatedAtUtc = DateTime.UtcNow;
				await _db.SaveChangesAsync(ct);
				await transaction.CommitAsync(ct);
				RaiseInventoryStateChanged();
				result = OfficeMutationResult.Ok(itemId, "'" + item.NameOriginal + "' 품목의 재고를 초기화했습니다. 기존 전표/재고이동 이력은 유지되고, 초기화 시점 이후 재고만 다시 계산됩니다.");
			}
		}
		return result;
	}

	private async Task<List<string>> ResolveInventoryResetWarehouseCodesAsync(Guid itemId, LocalItem item, CancellationToken ct)
	{
		var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void AddCode(string? warehouseCode, string? officeCode = null)
		{
			var normalized = NormalizeWarehouseCode(warehouseCode, officeCode, item.OfficeCode);
			if (!string.IsNullOrWhiteSpace(normalized))
			{
				codes.Add(normalized);
			}
		}

		var stockCodes = await _db.ItemWarehouseStocks.AsNoTracking()
			.Where((LocalItemWarehouseStock stock) => stock.ItemId == itemId)
			.Select((LocalItemWarehouseStock stock) => stock.WarehouseCode)
			.ToListAsync(ct);
		foreach (var warehouseCode in stockCodes)
		{
			AddCode(warehouseCode, ResolveOfficeCodeFromWarehouseCode(warehouseCode));
		}

		var invoiceWarehouses = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
			.Where((LocalInvoice invoice) => !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && invoice.Lines.Any((LocalInvoiceLine line) => !line.IsDeleted && line.ItemId == itemId))
			.Select((LocalInvoice invoice) => new
			{
				invoice.SourceWarehouseCode,
				invoice.ResponsibleOfficeCode,
				invoice.OfficeCode
			})
			.ToListAsync(ct);
		foreach (var invoice in invoiceWarehouses)
		{
			AddCode(invoice.SourceWarehouseCode, invoice.ResponsibleOfficeCode);
		}

		var transferWarehouses = await _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
			.Where((LocalInventoryTransfer transfer) => !transfer.IsDeleted && transfer.Lines.Any((LocalInventoryTransferLine line) => !line.IsDeleted && line.ItemId == itemId))
			.Select((LocalInventoryTransfer transfer) => new
			{
				transfer.FromWarehouseCode,
				transfer.ToWarehouseCode
			})
			.ToListAsync(ct);
		foreach (var transfer in transferWarehouses)
		{
			AddCode(transfer.FromWarehouseCode, ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode));
			AddCode(transfer.ToWarehouseCode, ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode));
		}

		var adjustmentWarehouses = await _db.InventoryMovements.AsNoTracking()
			.Where((LocalInventoryMovement movement) => movement.IsActive && movement.ItemId == itemId && (movement.MovementType == ManualStockAdjustmentMovementType || movement.MovementType == InventoryResetToZeroMovementType))
			.Select((LocalInventoryMovement movement) => movement.WarehouseCode)
			.ToListAsync(ct);
		foreach (var warehouseCode in adjustmentWarehouses)
		{
			AddCode(warehouseCode, ResolveOfficeCodeFromWarehouseCode(warehouseCode));
		}

		if (codes.Count == 0)
		{
			codes.Add(ResolvePrimaryWarehouseCode(item.OfficeCode));
		}

		return codes.OrderBy((string code) => code, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private async Task<DateOnly> ResolveInventoryResetOccurredDateAsync(Guid itemId, CancellationToken ct)
	{
		var latest = DateOnly.FromDateTime(DateTime.Today);
		void Use(DateOnly value)
		{
			if (value > latest)
			{
				latest = value;
			}
		}

		var invoiceDates = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
			.Where((LocalInvoice invoice) => !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && invoice.Lines.Any((LocalInvoiceLine line) => !line.IsDeleted && line.ItemId == itemId))
			.Select((LocalInvoice invoice) => invoice.InvoiceDate)
			.ToListAsync(ct);
		foreach (var invoiceDate in invoiceDates)
		{
			Use(invoiceDate);
		}

		var transferDates = await _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
			.Where((LocalInventoryTransfer transfer) => !transfer.IsDeleted && transfer.Lines.Any((LocalInventoryTransferLine line) => !line.IsDeleted && line.ItemId == itemId))
			.Select((LocalInventoryTransfer transfer) => transfer.TransferDate)
			.ToListAsync(ct);
		foreach (var transferDate in transferDates)
		{
			Use(transferDate);
		}

		var adjustmentDates = await _db.InventoryMovements.AsNoTracking()
			.Where((LocalInventoryMovement movement) => movement.IsActive && movement.ItemId == itemId && (movement.MovementType == ManualStockAdjustmentMovementType || movement.MovementType == InventoryResetToZeroMovementType))
			.Select((LocalInventoryMovement movement) => movement.OccurredDate)
			.ToListAsync(ct);
		foreach (var adjustmentDate in adjustmentDates)
		{
			Use(adjustmentDate);
		}

		return latest;
	}

	public async Task<List<LocalOffice>> GetOfficesAsync(CancellationToken ct = default(CancellationToken))
	{
		return (await (from office in _db.Offices.AsNoTracking()
			where office.Code == "USENET" || office.Code == "ITWORLD" || office.Code == "YEONSU"
			select office).ToListAsync(ct)).OrderBy((LocalOffice office) => GetOfficeSortOrder(office.Code)).ThenBy<LocalOffice, string>((LocalOffice office) => office.Code, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public IReadOnlyList<string> GetReadableOfficeCodesForSession(SessionState session)
	{
		return OrderOfficeCodes(GetReadableOfficeCodes(session));
	}

	public IReadOnlyList<string> GetWritableOfficeCodesForSession(SessionState session)
	{
		return OrderOfficeCodes(GetWritableOfficeCodes(session));
	}

	public IReadOnlyList<string> GetReadableRentalOfficeCodesForSession(SessionState session)
	{
		return OrderOfficeCodes(GetReadableRentalOfficeCodes(session));
	}

	public IReadOnlyList<string> GetWritableRentalOfficeCodesForSession(SessionState session)
	{
		return OrderOfficeCodes(GetWritableRentalOfficeCodes(session));
	}

	public Task<List<LocalWarehouse>> GetWarehousesAsync(bool onlyActive = true, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalWarehouse> source = from warehouse in _db.Warehouses.AsNoTracking()
			where warehouse.Code == DomainConstants.WarehouseUsenetMain || warehouse.Code == DomainConstants.WarehouseItworldMain || warehouse.Code == DomainConstants.WarehouseYeonsuMain
			select warehouse;
		if (onlyActive)
		{
			source = source.Where((LocalWarehouse w) => w.IsActive);
		}
		return (from w in source
			orderby w.OfficeCode, w.Name
			select w).ToListAsync(ct);
	}

	public Task<List<LocalWarehouse>> GetWarehousesForInventoryTransferAsync(SessionState session, bool onlyActive = true, CancellationToken ct = default(CancellationToken))
	{
		List<string> tenantOfficeCodes = GetTenantOfficeCodes(session);
		IQueryable<LocalWarehouse> source = from warehouse in _db.Warehouses.AsNoTracking()
			where tenantOfficeCodes.Contains(warehouse.OfficeCode) && (warehouse.Code == DomainConstants.WarehouseUsenetMain || warehouse.Code == DomainConstants.WarehouseItworldMain || warehouse.Code == DomainConstants.WarehouseYeonsuMain)
			select warehouse;
		if (onlyActive)
		{
			source = source.Where((LocalWarehouse warehouse) => warehouse.IsActive);
		}
		return (from warehouse in source
			orderby warehouse.OfficeCode, warehouse.Name
			select warehouse).ToListAsync(ct);
	}

	public Task<List<LocalWarehouse>> GetWarehousesByOfficeAsync(string officeCode, bool onlyActive = true, CancellationToken ct = default(CancellationToken))
	{
		string normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
		IQueryable<LocalWarehouse> source = from warehouse in _db.Warehouses.AsNoTracking()
			where warehouse.OfficeCode == normalizedOfficeCode
			where warehouse.Code == DomainConstants.WarehouseUsenetMain || warehouse.Code == DomainConstants.WarehouseItworldMain || warehouse.Code == DomainConstants.WarehouseYeonsuMain
			select warehouse;
		if (onlyActive)
		{
			source = source.Where((LocalWarehouse w) => w.IsActive);
		}
		return source.OrderBy((LocalWarehouse w) => w.Name).ToListAsync(ct);
	}

	public Task<List<LocalInvoice>> GetInvoicesAsync(DateOnly? from = null, DateOnly? to = null, Guid? customerId = null, CancellationToken ct = default(CancellationToken))
	{
		return GetInvoicesWithOptionsAsync(from, to, customerId, latestOnly: true, ct);
	}

	public Task<List<LocalInvoice>> GetInvoicesAsync(DateOnly? from, DateOnly? to, Guid? customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return GetInvoicesWithOptionsAsync(from, to, customerId, latestOnly: true, session, ct);
	}

	public async Task<List<LocalInvoiceListSummary>> GetInvoiceListSummariesAsync(DateOnly? from, DateOnly? to, Guid? customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var stopwatch = Stopwatch.StartNew();
		IQueryable<LocalInvoice> query = ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session)
			.Where((LocalInvoice invoice) => invoice.IsLatestVersion);
		if (from.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.CustomerId == ((Guid?)customerId).Value);
		}

		var invoiceRows = await (from invoice in query
			orderby invoice.InvoiceDate descending, invoice.UpdatedAtUtc descending, invoice.VersionNumber descending
			select new LocalInvoiceListSummary
			{
				Id = invoice.Id,
				VersionGroupId = invoice.VersionGroupId,
				CustomerId = invoice.CustomerId,
				ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
				LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId,
				LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId,
				InvoiceNumber = invoice.InvoiceNumber,
				LocalTempNumber = invoice.LocalTempNumber,
				InvoiceDate = invoice.InvoiceDate,
				VoucherType = invoice.VoucherType,
				TotalAmount = invoice.TotalAmount,
				SupplyAmount = invoice.SupplyAmount,
				VatAmount = invoice.VatAmount,
				VatMode = invoice.VatMode,
				TaxInvoiceIssued = invoice.TaxInvoiceIssued,
				PurchaseReceivingRequired = invoice.PurchaseReceivingRequired,
				PurchaseReceivingStatus = invoice.PurchaseReceivingStatus,
				IsDirty = invoice.IsDirty,
				Revision = invoice.Revision,
				UpdatedAtUtc = invoice.UpdatedAtUtc,
				LastSavedAtUtc = invoice.LastSavedAtUtc,
				VersionNumber = invoice.VersionNumber
			}).ToListAsync(ct);

		if (invoiceRows.Count == 0)
		{
			OperationTiming.LogIfSlow(
				"DATA",
				"Invoice list summary load",
				stopwatch.Elapsed,
				"invoices=0",
				infoThreshold: TimeSpan.FromMilliseconds(300),
				warningThreshold: TimeSpan.FromSeconds(2));
			return invoiceRows;
		}

		var invoiceIdQuery = query.Select((LocalInvoice invoice) => new { invoice.Id });
		var paymentRows = await (from payment in _db.Payments.AsNoTracking()
			join invoice in invoiceIdQuery on payment.InvoiceId equals invoice.Id
			select new
			{
				payment.InvoiceId,
				payment.Amount
			}).ToListAsync(ct);
		var settledAmounts = paymentRows
			.GroupBy(payment => payment.InvoiceId)
			.ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));

		var lineRows = await (from line in _db.InvoiceLines.AsNoTracking()
			join invoice in invoiceIdQuery on line.InvoiceId equals invoice.Id
			orderby line.InvoiceId, line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue, line.Id
			select new
			{
				line.InvoiceId,
				line.ItemNameOriginal,
				line.Remark
			}).ToListAsync(ct);
		var firstItemSummaries = lineRows
			.GroupBy(line => line.InvoiceId)
			.ToDictionary(
				group => group.Key,
				group => BuildInvoiceListFirstItemSummary(group.Select(line => (line.ItemNameOriginal, line.Remark))));

		foreach (var invoice in invoiceRows)
		{
			if (settledAmounts.TryGetValue(invoice.Id, out var settledAmount))
				invoice.SettledAmount = settledAmount;
			if (firstItemSummaries.TryGetValue(invoice.Id, out var firstItemSummary))
				invoice.FirstItemSummary = firstItemSummary;
		}

		OperationTiming.LogIfSlow(
			"DATA",
			"Invoice list summary load",
			stopwatch.Elapsed,
			$"invoices={invoiceRows.Count:N0}, lines={lineRows.Count:N0}, payments={paymentRows.Count:N0}",
			infoThreshold: TimeSpan.FromMilliseconds(300),
			warningThreshold: TimeSpan.FromSeconds(2));
		return invoiceRows;
	}

	public async Task<(DateOnly? FirstDate, DateOnly? LastDate)> GetInvoiceDateRangeAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalInvoice> query = ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session)
			.Where((LocalInvoice invoice) => invoice.IsLatestVersion);
		var firstDate = await query
			.OrderBy((LocalInvoice invoice) => invoice.InvoiceDate)
			.Select((LocalInvoice invoice) => (DateOnly?)invoice.InvoiceDate)
			.FirstOrDefaultAsync(ct);
		if (!firstDate.HasValue)
			return (null, null);
		var lastDate = await query
			.OrderByDescending((LocalInvoice invoice) => invoice.InvoiceDate)
			.Select((LocalInvoice invoice) => (DateOnly?)invoice.InvoiceDate)
			.FirstOrDefaultAsync(ct);
		return (firstDate, lastDate);
	}

	private static string BuildInvoiceListFirstItemSummary(IEnumerable<(string ItemNameOriginal, string Remark)> lines)
	{
		var activeLines = lines.ToList();
		if (activeLines.Count == 0)
			return "(품목 없음)";

		var firstLabel = activeLines
			.Select(line => string.IsNullOrWhiteSpace(line.ItemNameOriginal) ? line.Remark : line.ItemNameOriginal)
			.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
			?.Trim();
		if (string.IsNullOrWhiteSpace(firstLabel))
			firstLabel = "(품목 없음)";

		return activeLines.Count == 1
			? firstLabel
			: $"{firstLabel} 외 {activeLines.Count - 1}건";
	}

	public Task<List<LocalInvoice>> GetInvoicesWithOptionsAsync(DateOnly? from, DateOnly? to, Guid? customerId, bool latestOnly, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalInvoice> source = _db.Invoices.Include((LocalInvoice i) => i.Lines.Where((LocalInvoiceLine l) => !l.IsDeleted)).Include((LocalInvoice i) => i.Payments.Where((LocalPayment p) => !p.IsDeleted)).AsSplitQuery().AsNoTracking();
		if (latestOnly)
		{
			source = source.Where((LocalInvoice i) => i.IsLatestVersion);
		}
		if (from.HasValue)
		{
			source = source.Where((LocalInvoice i) => i.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			source = source.Where((LocalInvoice i) => i.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			source = source.Where((LocalInvoice i) => i.CustomerId == ((Guid?)customerId).Value);
		}
		return (from i in source
			orderby i.InvoiceDate descending, i.UpdatedAtUtc descending, i.VersionNumber descending
			select i).ToListAsync(ct);
	}

	public Task<List<LocalInvoice>> GetInvoicesWithOptionsAsync(DateOnly? from, DateOnly? to, Guid? customerId, bool latestOnly, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalInvoice> query = _db.Invoices.Include((LocalInvoice i) => i.Lines.Where((LocalInvoiceLine l) => !l.IsDeleted)).Include((LocalInvoice i) => i.Payments.Where((LocalPayment p) => !p.IsDeleted)).AsSplitQuery().AsNoTracking();
		query = ApplyInvoiceScope(query, session);
		if (latestOnly)
		{
			query = query.Where((LocalInvoice i) => i.IsLatestVersion);
		}
		if (from.HasValue)
		{
			query = query.Where((LocalInvoice i) => i.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			query = query.Where((LocalInvoice i) => i.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			query = query.Where((LocalInvoice i) => i.CustomerId == ((Guid?)customerId).Value);
		}
		return (from i in query
			orderby i.InvoiceDate descending, i.UpdatedAtUtc descending, i.VersionNumber descending
			select i).ToListAsync(ct);
	}

	public async Task<List<LocalInvoice>> GetYeonsuDeliveryInvoicesAsync(DateOnly? from = null, DateOnly? to = null, Guid? customerId = null, string? warehouseCode = null, string? responsibleOfficeCode = null, CancellationToken ct = default(CancellationToken))
	{
		string officeFilterText = (responsibleOfficeCode ?? string.Empty).Trim();
		string normalizedOfficeCode = (string.IsNullOrWhiteSpace(officeFilterText) ? string.Empty : NormalizeOfficeCode(officeFilterText, DomainConstants.OfficeYeonsu));
		IQueryable<LocalInvoice> query = from invoice in _db.Invoices.Include((LocalInvoice invoice) => invoice.Lines.Where((LocalInvoiceLine line) => !line.IsDeleted)).Include((LocalInvoice invoice) => invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted)).AsSplitQuery().AsNoTracking()
			where !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && (int)invoice.VoucherType == 0
			select invoice;
		if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
		{
			query = query.Where((LocalInvoice invoice) =>
				invoice.ResponsibleOfficeCode == normalizedOfficeCode ||
				((invoice.ResponsibleOfficeCode == null || invoice.ResponsibleOfficeCode == string.Empty) &&
				 invoice.OfficeCode == normalizedOfficeCode));
		}
		if (from.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.CustomerId == ((Guid?)customerId).Value);
		}
		string normalizedWarehouseCode = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
		if (!string.IsNullOrWhiteSpace(normalizedWarehouseCode))
		{
			query = query.Where((LocalInvoice invoice) => invoice.SourceWarehouseCode == normalizedWarehouseCode);
		}
		return await (from invoice in query
			orderby invoice.InvoiceDate descending, invoice.LastSavedAtUtc descending, invoice.UpdatedAtUtc descending
			select invoice).ToListAsync(ct);
	}

	public Task<List<LocalInvoice>> GetYeonsuDeliveryInvoicesAsync(DateOnly? from, DateOnly? to, Guid? customerId, string? warehouseCode, string? responsibleOfficeCode, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (session.HasGlobalDataScope)
		{
			string responsibleOfficeCode2 = NormalizeOfficeScope(responsibleOfficeCode, string.Empty);
			return GetYeonsuDeliveryInvoicesAsync(from, to, customerId, warehouseCode, responsibleOfficeCode2, ct);
		}

		string currentTenantCode = ResolveCurrentTenantCode(session);
		if (CanViewAllDeliveryScope(session))
		{
			List<string> tenantOfficeCodes = GetTenantOfficeCodes(session);
			string requestedOfficeCode = NormalizeOfficeScope(responsibleOfficeCode, string.Empty);
			if (!string.IsNullOrWhiteSpace(requestedOfficeCode) && !IsSharedOfficeScope(requestedOfficeCode))
			{
				if (!tenantOfficeCodes.Contains(requestedOfficeCode, StringComparer.OrdinalIgnoreCase))
				{
					return Task.FromResult(new List<LocalInvoice>());
				}
				return GetDeliveryInvoicesForTenantOfficeScopeAsync(from, to, customerId, warehouseCode, new[] { requestedOfficeCode }, currentTenantCode, ct);
			}

			return GetDeliveryInvoicesForTenantOfficeScopeAsync(from, to, customerId, warehouseCode, tenantOfficeCodes, currentTenantCode, ct);
		}

		string ownOfficeCode = NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeYeonsu);
		return GetDeliveryInvoicesForTenantOfficeScopeAsync(from, to, customerId, warehouseCode, new[] { ownOfficeCode }, currentTenantCode, ct);
	}

	private async Task<List<LocalInvoice>> GetDeliveryInvoicesForTenantOfficeScopeAsync(DateOnly? from, DateOnly? to, Guid? customerId, string? warehouseCode, IReadOnlyCollection<string> officeCodes, string tenantCode, CancellationToken ct)
	{
		List<string> normalizedOfficeCodes = officeCodes
			.Select((string officeCode) => NormalizeOfficeCode(officeCode, DomainConstants.OfficeYeonsu))
			.Where((string officeCode) => !string.IsNullOrWhiteSpace(officeCode))
			.Distinct<string>(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (normalizedOfficeCodes.Count == 0)
		{
			return new List<LocalInvoice>();
		}

		string normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
		IQueryable<LocalInvoice> query = from invoice in _db.Invoices.Include((LocalInvoice invoice) => invoice.Lines.Where((LocalInvoiceLine line) => !line.IsDeleted)).Include((LocalInvoice invoice) => invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted)).AsSplitQuery().AsNoTracking()
			where !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && (int)invoice.VoucherType == 0
			where invoice.TenantCode == normalizedTenantCode &&
			      ((invoice.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
			        (invoice.OfficeCode == OfficeCodeCatalog.Shared ||
			         invoice.OfficeCode == null ||
			         invoice.OfficeCode == string.Empty ||
			         normalizedOfficeCodes.Contains(invoice.OfficeCode))) ||
			       normalizedOfficeCodes.Contains(invoice.ResponsibleOfficeCode) ||
			       ((invoice.ResponsibleOfficeCode == null || invoice.ResponsibleOfficeCode == string.Empty) &&
			        normalizedOfficeCodes.Contains(invoice.OfficeCode)))
			select invoice;
		if (from.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.CustomerId == ((Guid?)customerId).Value);
		}
		string normalizedWarehouseCode = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
		if (!string.IsNullOrWhiteSpace(normalizedWarehouseCode))
		{
			query = query.Where((LocalInvoice invoice) => invoice.SourceWarehouseCode == normalizedWarehouseCode);
		}
		return await (from invoice in query
			orderby invoice.InvoiceDate descending, invoice.LastSavedAtUtc descending, invoice.UpdatedAtUtc descending
			select invoice).ToListAsync(ct);
	}

	public async Task<List<LocalInvoice>> GetSalesPurchaseLedgerInvoicesAsync(DateOnly? from = null, DateOnly? to = null, Guid? customerId = null, string? warehouseCode = null, string? responsibleOfficeCode = null, CancellationToken ct = default(CancellationToken))
	{
		string officeFilterText = (responsibleOfficeCode ?? string.Empty).Trim();
		string normalizedOfficeCode = (string.IsNullOrWhiteSpace(officeFilterText) ? string.Empty : NormalizeOfficeCode(officeFilterText, DomainConstants.OfficeYeonsu));
		IQueryable<LocalInvoice> query = from invoice in _db.Invoices.Include((LocalInvoice invoice) => invoice.Lines.Where((LocalInvoiceLine line) => !line.IsDeleted)).Include((LocalInvoice invoice) => invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted)).AsSplitQuery().AsNoTracking()
			where !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed && ((int)invoice.VoucherType == 0 || (int)invoice.VoucherType == 1 || (int)invoice.VoucherType == 2)
			select invoice;
		if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
		{
			query = query.Where((LocalInvoice invoice) =>
				invoice.ResponsibleOfficeCode == normalizedOfficeCode ||
				((invoice.ResponsibleOfficeCode == null || invoice.ResponsibleOfficeCode == string.Empty) &&
				 invoice.OfficeCode == normalizedOfficeCode));
		}
		if (from.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.InvoiceDate <= ((DateOnly?)to).Value);
		}
		if (customerId.HasValue)
		{
			query = query.Where((LocalInvoice invoice) => invoice.CustomerId == ((Guid?)customerId).Value);
		}
		string normalizedWarehouseCode = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
		if (!string.IsNullOrWhiteSpace(normalizedWarehouseCode))
		{
			query = query.Where((LocalInvoice invoice) => invoice.SourceWarehouseCode == normalizedWarehouseCode);
		}
		return await (from invoice in query
			orderby invoice.InvoiceDate descending, invoice.LastSavedAtUtc descending, invoice.UpdatedAtUtc descending
			select invoice).ToListAsync(ct);
	}

	public async Task<LocalInvoice?> GetInvoiceAsync(Guid id, CancellationToken ct = default(CancellationToken))
	{
		return await _db.Invoices
			.Include((LocalInvoice i) => i.Lines
				.Where((LocalInvoiceLine l) => !l.IsDeleted)
				.OrderBy((LocalInvoiceLine l) => l.OrderIndex > 0 ? l.OrderIndex : int.MaxValue)
				.ThenBy((LocalInvoiceLine l) => l.Id))
			.Include((LocalInvoice i) => i.Payments.Where((LocalPayment p) => !p.IsDeleted))
			.AsSplitQuery()
			.AsNoTracking()
			.FirstOrDefaultAsync((LocalInvoice i) => i.Id == id, ct);
	}

	public async Task<LocalInvoice?> GetInvoiceAsync(Guid id, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var invoice = await GetInvoiceAsync(id, ct);
		return (invoice != null && CanAccessInvoice(invoice, session)) ? invoice : null;
	}

	public async Task<LocalInvoice?> GetLatestInvoiceVersionAsync(Guid invoiceIdOrVersionGroupId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (invoiceIdOrVersionGroupId == Guid.Empty)
		{
			return null;
		}

		var seed = await _db.Invoices.AsNoTracking()
			.FirstOrDefaultAsync(invoice => invoice.Id == invoiceIdOrVersionGroupId, ct);
		var versionGroupId = seed is null
			? invoiceIdOrVersionGroupId
			: seed.VersionGroupId == Guid.Empty
				? seed.Id
				: seed.VersionGroupId;

		var latest = await _db.Invoices
			.Include(invoice => invoice.Lines
				.Where(line => !line.IsDeleted)
				.OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
				.ThenBy(line => line.Id))
			.Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
			.AsSplitQuery()
			.AsNoTracking()
			.Where(invoice =>
				!invoice.IsDeleted &&
				(invoice.VersionGroupId == versionGroupId ||
				 (invoice.VersionGroupId == Guid.Empty && invoice.Id == versionGroupId) ||
				 invoice.Id == invoiceIdOrVersionGroupId))
			.OrderByDescending(invoice => invoice.IsLatestVersion)
			.ThenByDescending(invoice => invoice.VersionNumber)
			.ThenByDescending(invoice => invoice.UpdatedAtUtc)
			.FirstOrDefaultAsync(ct);

		return (latest != null && CanAccessInvoice(latest, session)) ? latest : null;
	}

	public async Task<LocalInvoice?> GetSalesInvoiceForRentalBillingAsync(Guid billingProfileId, Guid? billingRunId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (billingProfileId == Guid.Empty)
		{
			return null;
		}

		IQueryable<LocalInvoice> query = _db.Invoices
			.Include((LocalInvoice i) => i.Lines.Where((LocalInvoiceLine l) => !l.IsDeleted))
			.Include((LocalInvoice i) => i.Payments.Where((LocalPayment p) => !p.IsDeleted))
			.AsSplitQuery()
			.AsNoTracking();
		query = ApplyInvoiceScope(query, session);
		query = query.Where((LocalInvoice invoice) =>
			!invoice.IsDeleted &&
			invoice.IsLatestVersion &&
			invoice.VoucherType == VoucherType.Sales &&
			invoice.LinkedRentalBillingProfileId == billingProfileId);
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			query = query.Where((LocalInvoice invoice) => invoice.LinkedRentalBillingRunId == billingRunId.Value);
		}

		return await query
			.OrderByDescending((LocalInvoice invoice) => invoice.InvoiceDate)
			.ThenByDescending((LocalInvoice invoice) => invoice.UpdatedAtUtc)
			.ThenByDescending((LocalInvoice invoice) => invoice.CreatedAtUtc)
			.FirstOrDefaultAsync(ct);
	}

	public async Task<List<LocalInvoice>> GetInvoiceVersionsAsync(Guid invoiceIdOrVersionGroupId, CancellationToken ct = default(CancellationToken))
	{
		Guid versionGroupId = invoiceIdOrVersionGroupId;
		var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync((LocalInvoice i) => i.Id == invoiceIdOrVersionGroupId, ct);
		if (invoice != null)
		{
			versionGroupId = ((invoice.VersionGroupId == Guid.Empty) ? invoice.Id : invoice.VersionGroupId);
		}
		return await (from i in _db.Invoices.Include((LocalInvoice i) => i.Lines.Where((LocalInvoiceLine l) => !l.IsDeleted)).Include((LocalInvoice i) => i.Payments.Where((LocalPayment p) => !p.IsDeleted)).AsSplitQuery().AsNoTracking()
			where i.VersionGroupId == versionGroupId || (i.VersionGroupId == Guid.Empty && i.Id == versionGroupId)
			orderby i.VersionNumber descending, i.UpdatedAtUtc descending
			select i).ToListAsync(ct);
	}

	public async Task<List<LocalInvoice>> GetInvoiceVersionsAsync(Guid invoiceIdOrVersionGroupId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return (await GetInvoiceVersionsAsync(invoiceIdOrVersionGroupId, ct)).Where((LocalInvoice version) => CanAccessInvoice(version, session)).ToList();
	}

	public async Task<LocalInvoice> SaveInvoiceAsync(LocalInvoice invoice, CancellationToken ct = default(CancellationToken))
	{
		InvoiceSaveContext context = new InvoiceSaveContext
		{
			Username = "system",
			Role = "admin",
			OfficeCode = DomainConstants.OfficeUsenet,
			ForceOverride = true
		};
		InvoiceSaveResult result = await SaveInvoiceAsync(invoice, context, null, ct);
		if (!result.Success)
		{
			throw new InvalidOperationException(result.Message);
		}
		var saved = await GetInvoiceAsync(result.SavedInvoiceId, ct);
		if (saved == null)
		{
			throw new InvalidOperationException("저장한 전표를 다시 불러올 수 없습니다.");
		}
		return saved;
	}

	public async Task<InvoiceSaveResult> SaveInvoiceAsync(LocalInvoice invoice, InvoiceSaveContext saveContext, SessionState? session = null, CancellationToken ct = default(CancellationToken))
	{
		if (invoice == null)
		{
			throw new ArgumentNullException("invoice");
		}
		if (session != null && !CanSaveInvoices(session))
		{
			return InvoiceSaveResult.Denied("관리자 또는 god 권한 계정만 전표를 저장할 수 있습니다.");
		}
		InvoiceSaveContext context = NormalizeSaveContext(saveContext);
		DateTime now = DateTime.UtcNow;
		var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync((LocalCustomer c) => c.Id == invoice.CustomerId, ct);
		if (customer == null)
		{
			return InvoiceSaveResult.Missing("거래처 정보를 찾을 수 없습니다.");
		}
		string customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
		if (!CanAccessCustomer(customerTenantCode: TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, customerOfficeCode), customerId: customer.Id, customerOfficeCode: customer.ResponsibleOfficeCode, session: session, role: context.Role, fallbackOfficeCode: customer.OfficeCode))
		{
			return InvoiceSaveResult.Denied("권한이 없어 해당 거래처 전표를 저장할 수 없습니다.");
		}
		var latest = await ResolveLatestVersionAsync(invoice, ct);
		if (latest != null && !CanAccessInvoice(latest, session))
		{
			return InvoiceSaveResult.Denied("권한이 없어 해당 거래처 전표를 저장할 수 없습니다.");
		}
		if (latest != null && !context.ForceOverride && !string.IsNullOrWhiteSpace(context.ExpectedConcurrencyStamp) && !string.Equals(context.ExpectedConcurrencyStamp, latest.ConcurrencyStamp, StringComparison.OrdinalIgnoreCase))
		{
			if (!CanAutoRebaseInvoiceOnLatestSameUserSave(context, latest))
			{
				return InvoiceSaveResult.Conflict("다른 사용자가 먼저 저장했습니다. 최신 전표를 다시 불러온 뒤 다시 시도하세요.");
			}

			AppLogger.Info(
				"INVOICE",
				$"전표 동시수정 충돌을 같은 사용자 최신 저장분 기준으로 자동 재기반 처리합니다. invoice={latest.Id}, user={context.Username}, expectedStamp={context.ExpectedConcurrencyStamp}, latestStamp={latest.ConcurrencyStamp}");
		}
		Guid versionGroupId = ResolveVersionGroupId(invoice, latest);
		if (latest != null && latest.VersionGroupId == Guid.Empty)
		{
			latest.VersionGroupId = versionGroupId;
		}
		Guid targetInvoiceId = ((latest != null) ? Guid.NewGuid() : ((invoice.Id == Guid.Empty) ? Guid.NewGuid() : invoice.Id));
		int versionNumber = (latest?.VersionNumber ?? 0) + 1;
		string responsibleOfficeCode = ResolveInvoiceResponsibleOfficeForSave(
			session,
			(!string.IsNullOrWhiteSpace(invoice.ResponsibleOfficeCode)) ? invoice.ResponsibleOfficeCode : latest?.ResponsibleOfficeCode,
			customerOfficeCode);
		string ownerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
			invoice.OfficeCode,
			responsibleOfficeCode,
			customer.OfficeCode);
		string tenantCode = ResolveOperationalTenantForSave(
			session,
			invoice.TenantCode,
			ownerOfficeCode,
			customer.TenantCode,
			customer.OfficeCode);
		if (session != null && !CanWriteOperationalScope(session, tenantCode, responsibleOfficeCode, ownerOfficeCode))
		{
			return InvoiceSaveResult.Denied("권한이 없어 해당 담당지점 범위의 전표를 저장할 수 없습니다.");
		}
		string warehouseOfficeCode = (IsSharedOfficeScope(responsibleOfficeCode) ? NormalizeOfficeCode(context.OfficeCode, DomainConstants.OfficeUsenet) : NormalizeOfficeCode(responsibleOfficeCode, customerOfficeCode));
		string sourceWarehouseCode = NormalizeWarehouseCode(invoice.SourceWarehouseCode, warehouseOfficeCode, warehouseOfficeCode);
		bool hasIncomingReceivingStatus = !string.IsNullOrWhiteSpace(invoice.PurchaseReceivingStatus);
		bool purchaseReceivingRequired = invoice.VoucherType == VoucherType.Purchase && (invoice.PurchaseReceivingRequired || (latest?.PurchaseReceivingRequired ?? false) || (!hasIncomingReceivingStatus && latest == null));
		string purchaseReceivingStatus = InvoiceReceivingStatuses.Normalize(hasIncomingReceivingStatus ? invoice.PurchaseReceivingStatus : latest?.PurchaseReceivingStatus, invoice.VoucherType == VoucherType.Purchase, purchaseReceivingRequired);
		DateTime? purchaseReceivedAtUtc = InvoiceReceivingStatuses.IsConfirmed(purchaseReceivingStatus) ? (invoice.PurchaseReceivedAtUtc ?? latest?.PurchaseReceivedAtUtc) : null;
		string purchaseReceivedByUsername = InvoiceReceivingStatuses.IsConfirmed(purchaseReceivingStatus)
			? ((!string.IsNullOrWhiteSpace(invoice.PurchaseReceivedByUsername)) ? invoice.PurchaseReceivedByUsername : (latest?.PurchaseReceivedByUsername ?? string.Empty))
			: string.Empty;
		string purchaseReceivingOfficeCode = (!string.IsNullOrWhiteSpace(invoice.PurchaseReceivingOfficeCode)) ? invoice.PurchaseReceivingOfficeCode : (latest?.PurchaseReceivingOfficeCode ?? string.Empty);
		string purchaseReceivingWarehouseCode = (!string.IsNullOrWhiteSpace(invoice.PurchaseReceivingWarehouseCode)) ? invoice.PurchaseReceivingWarehouseCode : (latest?.PurchaseReceivingWarehouseCode ?? string.Empty);
		string purchaseReceivingMemo = invoice.PurchaseReceivingMemo ?? string.Empty;
		List<LocalInvoiceLine> validLines = (invoice.Lines ?? new List<LocalInvoiceLine>()).Where((LocalInvoiceLine localInvoiceLine) => !localInvoiceLine.IsDeleted && !string.IsNullOrWhiteSpace(localInvoiceLine.ItemNameOriginal)).ToList();
		if (await ValidateInvoiceLineItemScopeAsync(validLines, session, ct) is { } itemScopeResult)
		{
			return itemScopeResult;
		}
		Dictionary<Guid, string> itemTrackingMap = await BuildItemTrackingMapAsync(ct);
		foreach (LocalInvoiceLine line in validLines)
		{
			line.ItemTrackingType = ResolveInvoiceLineTrackingType(line, itemTrackingMap);
		}
		var totals = InvoiceVatModes.CalculateTotals(validLines.Select((LocalInvoiceLine localInvoiceLine) => localInvoiceLine.LineAmount), invoice.VatMode);
		decimal totalAmount = totals.TotalAmount;
		decimal supplyAmount = totals.SupplyAmount;
		decimal vatAmount = totals.VatAmount;
		LocalInvoice newInvoice = new LocalInvoice
		{
			Id = targetInvoiceId,
			CustomerId = invoice.CustomerId,
			TenantCode = tenantCode,
			OfficeCode = ownerOfficeCode,
			InvoiceNumber = ((!string.IsNullOrWhiteSpace(invoice.InvoiceNumber)) ? invoice.InvoiceNumber : (latest?.InvoiceNumber ?? string.Empty)),
			LocalTempNumber = ((!string.IsNullOrWhiteSpace(invoice.LocalTempNumber)) ? invoice.LocalTempNumber : (latest?.LocalTempNumber ?? string.Empty)),
			VoucherType = invoice.VoucherType,
			InvoiceDate = invoice.InvoiceDate,
			TotalAmount = totalAmount,
			SupplyAmount = supplyAmount,
			VatAmount = vatAmount,
			VatMode = InvoiceVatModes.Normalize(invoice.VatMode),
			TaxInvoiceIssued = invoice.TaxInvoiceIssued,
			PurchaseReceivingRequired = purchaseReceivingRequired,
			PurchaseReceivingStatus = purchaseReceivingStatus,
			PurchaseReceivedAtUtc = purchaseReceivedAtUtc,
			PurchaseReceivedByUsername = purchaseReceivedByUsername,
			PurchaseReceivingOfficeCode = purchaseReceivingOfficeCode,
			PurchaseReceivingWarehouseCode = purchaseReceivingWarehouseCode,
			PurchaseReceivingMemo = purchaseReceivingMemo,
			Memo = (invoice.Memo ?? string.Empty),
			ResponsibleOfficeCode = responsibleOfficeCode,
			SourceWarehouseCode = sourceWarehouseCode,
			DeliveryGroupId = invoice.DeliveryGroupId,
			ParentInvoiceId = invoice.ParentInvoiceId,
			LinkedRentalBillingProfileId = invoice.LinkedRentalBillingProfileId,
			LinkedRentalBillingRunId = invoice.LinkedRentalBillingRunId,
			VersionGroupId = versionGroupId,
			VersionNumber = versionNumber,
			PreviousVersionId = latest?.Id,
			IsLatestVersion = true,
			IsConfirmed = true,
			CreatedByUsername = (string.IsNullOrWhiteSpace(latest?.CreatedByUsername) ? context.Username : latest.CreatedByUsername),
			LastSavedByUsername = context.Username,
			LastSavedAtUtc = now,
			ConcurrencyStamp = Guid.NewGuid().ToString("N"),
			CostStatus = "Pending",
			IsDirty = true,
			UpdatedAtUtc = now,
			CreatedAtUtc = (latest?.CreatedAtUtc ?? now),
			Revision = (latest?.Revision ?? 0)
		};
		if (string.IsNullOrWhiteSpace(newInvoice.LocalTempNumber))
		{
			LocalInvoice localInvoice = newInvoice;
			localInvoice.LocalTempNumber = await GenerateLocalTempNumberAsync(newInvoice.InvoiceDate, ct);
		}
		newInvoice.Lines = CloneLines(validLines, targetInvoiceId);
		List<LocalPayment> requestedPayments = (invoice.Payments ?? new List<LocalPayment>()).Where((LocalPayment p) => !p.IsDeleted).ToList();
		bool relinkRentalSettlementRecordsToNewVersion = latest != null && (IsRentalBillingInvoice(latest) || IsRentalBillingInvoice(newInvoice));
		if (!relinkRentalSettlementRecordsToNewVersion && requestedPayments.Count > 0)
		{
			newInvoice.Payments = ClonePayments(requestedPayments, targetInvoiceId, now);
		}
		else if (!relinkRentalSettlementRecordsToNewVersion && latest != null)
		{
			List<LocalPayment> latestPayments = latest.Payments.Where((LocalPayment payment) => !payment.IsDeleted).ToList();
			newInvoice.Payments = ClonePayments(latestPayments, targetInvoiceId, now);
		}
		if (latest != null)
		{
			latest.IsLatestVersion = false;
			latest.IsDirty = true;
			latest.UpdatedAtUtc = now;
			latest.LastSavedAtUtc = now;
		}
		string beforeJson = ((latest == null) ? string.Empty : JsonSerializer.Serialize(BuildAuditInvoice(latest), AuditJsonOptions));
		string afterJson = JsonSerializer.Serialize(BuildAuditInvoice(newInvoice), AuditJsonOptions);
		_db.Invoices.Add(newInvoice);
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInvoice",
			EntityId = newInvoice.Id.ToString("D"),
			Action = ((latest == null) ? "Create" : "Revise"),
			Username = context.Username,
			Role = context.Role,
			OfficeCode = context.OfficeCode,
			BeforeJson = beforeJson,
			AfterJson = afterJson,
			CreatedAtUtc = now
		});
		var rentalSettlementTargets = BuildRentalSettlementTargetsForInvoiceSave(latest, newInvoice);
		if (relinkRentalSettlementRecordsToNewVersion && latest != null)
		{
			await RelinkInvoiceSettlementRecordsToNewVersionAsync(latest.Id, newInvoice.Id, newInvoice.InvoiceNumber, now, ct);
		}
		await _db.SaveChangesAsync(ct);
		await RebuildInventorySnapshotsAsync(context, ct);
		await RecalculateRentalSettlementsAsync(rentalSettlementTargets, ct, markDirty: true);
		return InvoiceSaveResult.Ok(newInvoice.Id, newInvoice.ConcurrencyStamp, (latest == null) ? "전표를 저장했습니다." : $"전표 {newInvoice.VersionNumber}차 버전으로 저장했습니다.");
	}

	private static bool IsRentalBillingInvoice(LocalInvoice? invoice)
	{
		return invoice?.LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty;
	}

	private async Task RelinkInvoiceSettlementRecordsToNewVersionAsync(
		Guid previousInvoiceId,
		Guid newInvoiceId,
		string invoiceNumber,
		DateTime updatedAtUtc,
		CancellationToken ct)
	{
		var payments = await _db.Payments.IgnoreQueryFilters()
			.Where(payment => !payment.IsDeleted && payment.InvoiceId == previousInvoiceId)
			.ToListAsync(ct);
		foreach (var payment in payments)
		{
			payment.InvoiceId = newInvoiceId;
			payment.IsDirty = true;
			payment.UpdatedAtUtc = updatedAtUtc;
		}

		var transactions = await _db.Transactions.IgnoreQueryFilters()
			.Where(transaction => !transaction.IsDeleted && transaction.LinkedInvoiceId == previousInvoiceId)
			.ToListAsync(ct);
		foreach (var transaction in transactions)
		{
			transaction.LinkedInvoiceId = newInvoiceId;
			transaction.LinkedInvoiceNumber = invoiceNumber;
			transaction.IsDirty = true;
			transaction.UpdatedAtUtc = updatedAtUtc;
		}
	}

	private static List<(Guid ProfileId, Guid? RunId)> BuildRentalSettlementTargetsForInvoiceSave(LocalInvoice? previousInvoice, LocalInvoice newInvoice)
	{
		var targets = new List<(Guid ProfileId, Guid? RunId)>();
		AddRentalSettlementTarget(targets, previousInvoice?.LinkedRentalBillingProfileId, previousInvoice?.LinkedRentalBillingRunId);
		AddRentalSettlementTarget(targets, newInvoice.LinkedRentalBillingProfileId, newInvoice.LinkedRentalBillingRunId);
		return targets.Distinct().ToList();
	}

	private static void AddRentalSettlementTarget(List<(Guid ProfileId, Guid? RunId)> targets, Guid? profileId, Guid? runId)
	{
		if (!profileId.HasValue || profileId.Value == Guid.Empty)
		{
			return;
		}
		targets.Add((profileId.Value, runId));
	}

	private static bool CanAutoRebaseInvoiceOnLatestSameUserSave(InvoiceSaveContext context, LocalInvoice latest)
	{
		if (!context.AutoRebaseWhenLatestSavedBySameUser)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(context.Username) || string.IsNullOrWhiteSpace(latest.LastSavedByUsername))
		{
			return false;
		}

		return string.Equals(
			context.Username.Trim(),
			latest.LastSavedByUsername.Trim(),
			StringComparison.OrdinalIgnoreCase);
	}

	public async Task DeleteInvoiceAsync(Guid id, CancellationToken ct = default(CancellationToken))
	{
		var target = await _db.Invoices.FirstOrDefaultAsync((LocalInvoice localInvoice) => localInvoice.Id == id, ct);
		if (target == null)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		Guid versionGroupId = ((target.VersionGroupId == Guid.Empty) ? target.Id : target.VersionGroupId);
		List<LocalInvoice> invoicesToDelete = await _db.Invoices.Where((LocalInvoice localInvoice) => localInvoice.Id == id || localInvoice.VersionGroupId == versionGroupId).ToListAsync(ct);
		foreach (LocalInvoice invoice in invoicesToDelete)
		{
			invoice.IsDeleted = true;
			invoice.IsLatestVersion = false;
			invoice.IsDirty = true;
			invoice.UpdatedAtUtc = now;
			invoice.LastSavedAtUtc = now;
		}
		List<Guid> deletedInvoiceIds = invoicesToDelete.Select((LocalInvoice localInvoice) => localInvoice.Id).ToList();
		var rentalSettlementTargets = await LoadRentalSettlementTargetsForInvoiceDeleteAsync(deletedInvoiceIds, ct);
		await DetachTransactionsFromInvoicesAsync(deletedInvoiceIds, now, ct);
		await MarkPaymentsDeletedForInvoicesAsync(deletedInvoiceIds, now, ct);
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInvoice",
			EntityId = id.ToString("D"),
			Action = "Delete",
			Username = "system",
			Role = "admin",
			OfficeCode = DomainConstants.OfficeUsenet,
			BeforeJson = string.Empty,
			AfterJson = string.Empty,
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RecalculateRentalSettlementsAsync(rentalSettlementTargets, ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = "system",
			Role = "admin",
			OfficeCode = DomainConstants.OfficeUsenet,
			ForceOverride = true
		}, ct);
	}

	public async Task<OfficeMutationResult> DeleteInvoiceAsync(Guid id, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanSaveInvoices(session))
		{
			return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 전표를 삭제할 수 있습니다.");
		}
		var target = await _db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync((LocalInvoice localInvoice) => localInvoice.Id == id, ct);
		target = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, target, ct);
		if (target == null)
		{
			return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");
		}
		if (!CanAccessInvoice(target, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 전표를 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(target, expectedRevision, "전표", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		DateTime now = DateTime.UtcNow;
		Guid versionGroupId = ((target.VersionGroupId == Guid.Empty) ? target.Id : target.VersionGroupId);
		List<LocalInvoice> invoicesToDelete = await (from localInvoice in _db.Invoices.IgnoreQueryFilters()
			where localInvoice.Id == id || localInvoice.VersionGroupId == versionGroupId
			select localInvoice).ToListAsync(ct);
		List<Guid> deletedInvoiceIds = invoicesToDelete.Select((LocalInvoice localInvoice) => localInvoice.Id).ToList();
		if (!CanEditPayments(session) && await HasActivePaymentSideEffectsForInvoiceDeleteAsync(deletedInvoiceIds, ct))
		{
			return OfficeMutationResult.Denied("전표에 연결된 수금/지급 내역을 함께 변경하거나 삭제하려면 수금/지급 편집 권한이 필요합니다.");
		}
		foreach (LocalInvoice invoice in invoicesToDelete)
		{
			invoice.IsDeleted = true;
			invoice.IsLatestVersion = false;
			invoice.IsDirty = true;
			invoice.UpdatedAtUtc = now;
			invoice.LastSavedAtUtc = now;
		}
		var rentalSettlementTargets = await LoadRentalSettlementTargetsForInvoiceDeleteAsync(deletedInvoiceIds, ct);
		await DetachTransactionsFromInvoicesAsync(deletedInvoiceIds, now, ct);
		await MarkPaymentsDeletedForInvoicesAsync(deletedInvoiceIds, now, ct);
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInvoice",
			EntityId = id.ToString("D"),
			Action = "Delete",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = string.Empty,
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RecalculateRentalSettlementsAsync(rentalSettlementTargets, ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			ForceOverride = false
		}, ct);
		return OfficeMutationResult.Ok(id, "전표를 삭제했습니다.");
	}

	public async Task<LocalPayment> SavePaymentAsync(LocalPayment payment, CancellationToken ct = default(CancellationToken))
	{
		var existing = await _db.Payments.FindAsync(new object[1] { payment.Id }, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		var affectedInvoiceIds = new[] { existing?.InvoiceId, payment.InvoiceId }
			.Where(id => id.HasValue && id.Value != Guid.Empty)
			.Select(id => id!.Value)
			.Distinct()
			.ToList();
		DateTime now = DateTime.UtcNow;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, payment, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(payment, existing, "수금/지급", now, out string conflictMessage))
		{
			throw new InvalidOperationException(conflictMessage);
		}
		if (existing == null)
		{
			_db.Payments.Add(payment);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(payment);
		}
		await _db.SaveChangesAsync(ct);
		await RecalculateRentalSettlementForInvoicePaymentsAsync(affectedInvoiceIds, ct, markDirty: true);
		return payment;
	}

	public async Task<OfficeMutationResult> SavePaymentAsync(LocalPayment payment, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var invoice = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalInvoice current) => current.Id == payment.InvoiceId, ct);
		if (invoice == null)
		{
			return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");
		}
		if (!CanWriteOperationalScope(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 전표 수금/지급을 저장할 수 없습니다.");
		}
		var existing = await _db.Payments.IgnoreQueryFilters().Include((LocalPayment current) => current.Invoice).FirstOrDefaultAsync((LocalPayment current) => current.Id == payment.Id, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		LocalInvoice? existingInvoice = null;
		if (existing is not null)
		{
			existingInvoice = existing.Invoice
			                  ?? await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalInvoice current) => current.Id == existing.InvoiceId, ct);
		}
		if (existingInvoice != null && !CanWriteOperationalScope(session, existingInvoice.TenantCode, existingInvoice.ResponsibleOfficeCode, existingInvoice.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 전표 수금/지급을 저장할 수 없습니다.");
		}
		var affectedInvoiceIds = new[] { existing?.InvoiceId, payment.InvoiceId }
			.Where(id => id.HasValue && id.Value != Guid.Empty)
			.Select(id => id!.Value)
			.Distinct()
			.ToList();
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, payment, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(now: DateTime.UtcNow, candidate: payment, existing: existing, entityDisplayName: "수금/지급", conflictMessage: out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (existing == null)
		{
			_db.Payments.Add(payment);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(payment);
		}
		await _db.SaveChangesAsync(ct);
		await RecalculateRentalSettlementForInvoicePaymentsAsync(affectedInvoiceIds, ct, markDirty: true);
		return OfficeMutationResult.Ok(payment.Id, "수금/지급을 저장했습니다.");
	}

	public async Task DeletePaymentAsync(Guid id, CancellationToken ct = default(CancellationToken))
	{
		var payment = await _db.Payments.FindAsync(new object[1] { id }, ct);
		if (payment != null)
		{
			var invoice = await _db.Invoices.IgnoreQueryFilters()
				.AsNoTracking()
				.FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
			var linkedTransaction = await _db.Transactions.IgnoreQueryFilters()
				.FirstOrDefaultAsync(current => current.Id == id, ct);
			var linkedRentalProfileId = linkedTransaction?.LinkedRentalBillingProfileId;
			var linkedRentalRunId = linkedTransaction?.LinkedRentalBillingRunId;
			var now = DateTime.UtcNow;
			if (linkedTransaction != null &&
			    !linkedTransaction.IsDeleted &&
			    (!linkedTransaction.LinkedInvoiceId.HasValue ||
			     linkedTransaction.LinkedInvoiceId.Value == payment.InvoiceId))
			{
				linkedTransaction.IsDeleted = true;
				linkedTransaction.IsDirty = true;
				linkedTransaction.UpdatedAtUtc = now;
				foreach (var attachment in await _db.TransactionAttachments.IgnoreQueryFilters()
					         .Where(current => current.TransactionId == id && !current.IsDeleted)
					         .ToListAsync(ct))
				{
					attachment.IsDeleted = true;
					attachment.IsDirty = true;
					attachment.UpdatedAtUtc = now;
				}
			}

			payment.IsDeleted = true;
			payment.IsDirty = true;
			payment.UpdatedAtUtc = now;
			await _db.SaveChangesAsync(ct);
			if (invoice?.LinkedRentalBillingProfileId is Guid billingProfileId && billingProfileId != Guid.Empty)
			{
				await RecalculateRentalSettlementAsync(billingProfileId, invoice.LinkedRentalBillingRunId, ct);
			}
			if (linkedRentalProfileId is Guid transactionBillingProfileId && transactionBillingProfileId != Guid.Empty)
			{
				await RecalculateRentalSettlementAsync(transactionBillingProfileId, linkedRentalRunId, ct);
			}
		}
	}

	public async Task RecalculateRentalSettlementForInvoicePaymentsAsync(IEnumerable<Guid> invoiceIds, CancellationToken ct = default(CancellationToken), bool markDirty = false)
	{
		List<Guid> targetInvoiceIds = (invoiceIds ?? Enumerable.Empty<Guid>())
			.Where((Guid id) => id != Guid.Empty)
			.Distinct()
			.ToList();
		if (targetInvoiceIds.Count == 0)
		{
			return;
		}

		var linkedRuns = await (from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
			where targetInvoiceIds.Contains(invoice.Id) &&
			      invoice.LinkedRentalBillingProfileId.HasValue &&
			      invoice.LinkedRentalBillingProfileId.Value != Guid.Empty
			select new
			{
				ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
				RunId = invoice.LinkedRentalBillingRunId
			}).ToListAsync(ct);

		foreach (var linkedRun in linkedRuns
			         .DistinctBy(current => new { current.ProfileId, current.RunId }))
		{
			await RecalculateRentalSettlementAsync(linkedRun.ProfileId, linkedRun.RunId, ct, markDirty);
		}
	}

	public async Task RecalculateRentalSettlementsAsync(IEnumerable<(Guid ProfileId, Guid? RunId)> targets, CancellationToken ct = default(CancellationToken), bool markDirty = false)
	{
		List<(Guid ProfileId, Guid? RunId)> distinctTargets = (targets ?? Enumerable.Empty<(Guid ProfileId, Guid? RunId)>())
			.Where(target => target.ProfileId != Guid.Empty)
			.Distinct()
			.ToList();
		foreach (var target in distinctTargets)
		{
			await RecalculateRentalSettlementAsync(target.ProfileId, target.RunId, ct, markDirty);
		}
	}

	public async Task ApplyPulledInvoiceDeleteSideEffectsAsync(
		IEnumerable<(Guid InvoiceId, DateTime UpdatedAtUtc, long Revision)> invoices,
		CancellationToken ct = default(CancellationToken))
	{
		var deleteRecords = (invoices ?? Enumerable.Empty<(Guid InvoiceId, DateTime UpdatedAtUtc, long Revision)>())
			.Where(record => record.InvoiceId != Guid.Empty)
			.GroupBy(record => record.InvoiceId)
			.Select(group => group
				.OrderByDescending(record => record.Revision)
				.ThenByDescending(record => record.UpdatedAtUtc)
				.First())
			.ToList();
		if (deleteRecords.Count == 0)
		{
			return;
		}

		var invoiceIds = deleteRecords.Select(record => record.InvoiceId).ToList();
		var updatedAtUtc = deleteRecords.Max(record => record.UpdatedAtUtc);
		var revision = deleteRecords.Max(record => record.Revision);
		var rentalSettlementTargets = await LoadRentalSettlementTargetsForInvoiceDeleteAsync(invoiceIds, ct);
		await DetachTransactionsFromInvoicesAsync(invoiceIds, updatedAtUtc, ct, markDirty: false, revision: revision);
		await MarkPaymentsDeletedForInvoicesAsync(invoiceIds, updatedAtUtc, ct, markDirty: false, revision: revision);
		await _db.SaveChangesAsync(ct);
		await RecalculateRentalSettlementsAsync(rentalSettlementTargets, ct, markDirty: false);
	}

	public async Task ReconcilePulledTransactionSideEffectsAsync(IEnumerable<Guid> transactionIds, CancellationToken ct = default(CancellationToken))
	{
		List<Guid> targetTransactionIds = (transactionIds ?? Enumerable.Empty<Guid>())
			.Where((Guid id) => id != Guid.Empty)
			.Distinct()
			.ToList();
		if (targetTransactionIds.Count == 0)
		{
			return;
		}

		List<LocalTransaction> transactions = await _db.Transactions.IgnoreQueryFilters()
			.Where((LocalTransaction transaction) => targetTransactionIds.Contains(transaction.Id))
			.ToListAsync(ct);
		foreach (LocalTransaction transaction in transactions)
		{
			if (transaction.IsDeleted)
			{
				await RemoveLinkedInvoicePaymentAsync(
					transaction.Id,
					ct,
					markDirty: false,
					updatedAtUtc: transaction.UpdatedAtUtc,
					revision: transaction.Revision);
			}
			else
			{
				LocalInvoice? linkedInvoice = null;
				if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
				{
					linkedInvoice = await _db.Invoices.IgnoreQueryFilters()
						.FirstOrDefaultAsync((LocalInvoice invoice) => invoice.Id == transaction.LinkedInvoiceId.Value, ct);
				}
				if (linkedInvoice == null &&
				    transaction.LinkedRentalBillingProfileId.HasValue &&
				    transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
				{
					linkedInvoice = await FindTrackedSalesInvoiceForRentalBillingAsync(
						transaction.LinkedRentalBillingProfileId.Value,
						transaction.LinkedRentalBillingRunId,
						ct);
				}

				if (linkedInvoice == null)
				{
					await RemoveLinkedInvoicePaymentAsync(
						transaction.Id,
						ct,
						markDirty: false,
						updatedAtUtc: transaction.UpdatedAtUtc,
						revision: transaction.Revision);
				}
				else
				{
					await SyncInvoicePaymentFromTransactionAsync(transaction, linkedInvoice, ct, markDirty: false);
				}
			}

			if (transaction.LinkedRentalBillingProfileId is Guid billingProfileId && billingProfileId != Guid.Empty)
			{
				await RecalculateRentalSettlementAsync(billingProfileId, transaction.LinkedRentalBillingRunId, ct, markDirty: false);
			}
		}
	}

	public Task<LocalCompanyProfile?> GetCompanyProfileAsync(CancellationToken ct = default(CancellationToken))
	{
		return GetCompanyProfileAsync(null, null, ct);
	}

	public Task<LocalCompanyProfile?> GetCompanyProfileAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return GetCompanyProfileAsync(session.User?.Username, session.OfficeCode, ct);
	}

	public Task EnsureCompanyProfilesHealthyAsync(CancellationToken ct = default(CancellationToken))
	{
		return EnsureCompanyProfileDefaultsAsync(ct);
	}

	public async Task<List<LocalCompanyProfile>> GetCompanyProfilesAsync(CancellationToken ct = default(CancellationToken))
	{
		return await (from profile in _db.CompanyProfiles.AsNoTracking()
			where !profile.IsDeleted && profile.IsActive
			orderby profile.OfficeCode, profile.IsDefaultForOffice descending, profile.ProfileName
			select profile).ToListAsync(ct);
	}

	public async Task<LocalCompanyProfile?> GetCompanyProfileAsync(string? username, string? officeCode, CancellationToken ct = default(CancellationToken))
	{
		List<LocalCompanyProfile> profiles = await GetCompanyProfilesAsync(ct);
		if (profiles.Count == 0)
		{
			return null;
		}
		string normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
		Guid? assignedId = await GetAssignedCompanyProfileIdAsync(username, ct);
		if (assignedId.HasValue)
		{
			var assigned = profiles.FirstOrDefault((LocalCompanyProfile profile) => profile.Id == assignedId.Value);
			if (assigned != null && string.Equals(NormalizeOfficeCode(assigned.OfficeCode, normalizedOfficeCode), normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
			{
				return assigned;
			}
		}
		var officeDefault = profiles.FirstOrDefault((LocalCompanyProfile profile) => string.Equals(profile.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase) && profile.IsDefaultForOffice);
		if (officeDefault != null)
		{
			return officeDefault;
		}
		var officeMatch = profiles.FirstOrDefault((LocalCompanyProfile profile) => string.Equals(profile.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase));
		if (officeMatch != null)
		{
			return officeMatch;
		}
		return profiles[0];
	}

	public async Task SaveCompanyProfileAsync(LocalCompanyProfile profile, CancellationToken ct = default(CancellationToken))
	{
		await EnsureCompanyProfileDefaultsAsync(ct);
		profile.ProfileName = ((!string.IsNullOrWhiteSpace(profile.ProfileName)) ? profile.ProfileName.Trim() : (string.IsNullOrWhiteSpace(profile.TradeName) ? "회사설정" : (profile.TradeName.Trim() + " 설정")));
		profile.OfficeCode = NormalizeOfficeCode(profile.OfficeCode, DomainConstants.OfficeUsenet);
		profile.TradeName = (profile.TradeName ?? string.Empty).Trim();
		profile.Representative = (profile.Representative ?? string.Empty).Trim();
		profile.BusinessNumber = (profile.BusinessNumber ?? string.Empty).Trim();
		profile.BusinessType = (profile.BusinessType ?? string.Empty).Trim();
		profile.BusinessItem = (profile.BusinessItem ?? string.Empty).Trim();
		profile.Address = (profile.Address ?? string.Empty).Trim();
		profile.ContactNumber = (profile.ContactNumber ?? string.Empty).Trim();
		profile.Email = (profile.Email ?? string.Empty).Trim();
		profile.BankAccountText = profile.BankAccountText ?? string.Empty;
		profile.IsActive = true;
		var existing = await _db.CompanyProfiles.FindAsync(new object[1] { profile.Id }, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		DateTime now = DateTime.UtcNow;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, profile, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(profile, existing, "회사설정", now, out string conflictMessage))
		{
			throw new InvalidOperationException(conflictMessage);
		}
		if (existing == null)
		{
			_db.CompanyProfiles.Add(profile);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(profile);
		}
		if (profile.IsDefaultForOffice)
		{
			foreach (LocalCompanyProfile other in await (from current in _db.CompanyProfiles.IgnoreQueryFilters()
				where current.Id != profile.Id && !current.IsDeleted && current.IsActive && current.IsDefaultForOffice && current.OfficeCode == profile.OfficeCode
				select current).ToListAsync(ct))
			{
				other.IsDefaultForOffice = false;
				other.IsDirty = true;
				other.UpdatedAtUtc = now;
			}
		}
		await _db.SaveChangesAsync(ct);
	}

	public async Task<LocalMutationResult> DeleteCompanyProfileAsync(Guid profileId, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		await EnsureCompanyProfileDefaultsAsync(ct);
		var profile = await _db.CompanyProfiles.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCompanyProfile current) => current.Id == profileId, ct);
		profile = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, profile, ct);
		if (profile == null)
		{
			return LocalMutationResult.Missing("회사설정을 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(profile, expectedRevision, "회사설정", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		List<string> assignedUsers = (await (from setting in _db.Settings.AsNoTracking()
			where setting.Key.StartsWith("CompanyProfile.Assigned.") && setting.Value == ((Guid)profileId).ToString("D")
			select setting).ToListAsync(ct)).Select((LocalSetting setting) => setting.Key.Substring("CompanyProfile.Assigned.".Length)).ToList();
		if (assignedUsers.Count > 0)
		{
			return LocalMutationResult.Denied("해당 회사설정을 사용하는 사용자가 있습니다 (" + string.Join(", ", assignedUsers) + "). 먼저 연결을 변경해주세요.");
		}
		List<LocalCompanyProfile> activeProfiles = await (from current in _db.CompanyProfiles.IgnoreQueryFilters()
			where !current.IsDeleted && current.IsActive
			orderby current.OfficeCode, current.IsDefaultForOffice descending
			select current).ToListAsync(ct);
		if (activeProfiles.Count <= 1)
		{
			return LocalMutationResult.Denied("마지막 활성 회사설정은 삭제할 수 없습니다.");
		}
		string profileOfficeCode = NormalizeOfficeCode(profile.OfficeCode, DomainConstants.OfficeUsenet);
		List<LocalCompanyProfile> officeProfiles = activeProfiles.Where((LocalCompanyProfile current) => string.Equals(NormalizeOfficeCode(current.OfficeCode, profileOfficeCode), profileOfficeCode, StringComparison.OrdinalIgnoreCase)).ToList();
		if (officeProfiles.Count <= 1)
		{
			return LocalMutationResult.Denied("해당 담당지점의 마지막 회사설정은 삭제할 수 없습니다.");
		}
		if (profile.IsDefaultForOffice)
		{
			var replacement = (from current in officeProfiles
				where current.Id != profile.Id
				orderby current.UpdatedAtUtc descending
				select current).FirstOrDefault();
			if (replacement != null)
			{
				replacement.IsDefaultForOffice = true;
				replacement.IsDirty = true;
				replacement.UpdatedAtUtc = DateTime.UtcNow;
			}
		}
		profile.IsDeleted = true;
		profile.IsActive = false;
		profile.IsDefaultForOffice = false;
		profile.IsDirty = true;
		profile.UpdatedAtUtc = DateTime.UtcNow;
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(profileId, "회사설정을 삭제했습니다.");
	}

	private async Task EnsureCompanyProfileDefaultsAsync(CancellationToken ct)
	{
		List<LocalCompanyProfile> profiles = await _db.CompanyProfiles.IgnoreQueryFilters().ToListAsync(ct);
		List<LocalCompanyProfile> activeProfiles = profiles.Where((LocalCompanyProfile localCompanyProfile) => !localCompanyProfile.IsDeleted && localCompanyProfile.IsActive).ToList();
		HashSet<Guid> validProfileIds = activeProfiles.Select((LocalCompanyProfile localCompanyProfile) => localCompanyProfile.Id).ToHashSet();
		List<LocalSetting> assignmentSettings = await (from localSetting in _db.Settings.IgnoreQueryFilters()
			where localSetting.Key.StartsWith("CompanyProfile.Assigned.")
			select localSetting).ToListAsync(ct);
		Guid result;
		bool hasInvalidAssignment = assignmentSettings.Any((LocalSetting localSetting) => !string.IsNullOrWhiteSpace(localSetting.Value) && (!Guid.TryParse(localSetting.Value, out result) || !validProfileIds.Contains(result)));
		bool missingDefaults = RequiredCompanyProfileDefaults.Any((CompanyProfileDefaultDefinition companyProfileDefaultDefinition) => !activeProfiles.Any((LocalCompanyProfile localCompanyProfile) => string.Equals(NormalizeOfficeCode(localCompanyProfile.OfficeCode, companyProfileDefaultDefinition.OfficeCode), companyProfileDefaultDefinition.OfficeCode, StringComparison.OrdinalIgnoreCase) && localCompanyProfile.IsDefaultForOffice));
		if (!hasInvalidAssignment && !missingDefaults && activeProfiles.Count > 0)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		bool changed = false;
		CompanyProfileDefaultDefinition[] requiredCompanyProfileDefaults = RequiredCompanyProfileDefaults;
		foreach (CompanyProfileDefaultDefinition definition in requiredCompanyProfileDefaults)
		{
			var current = (from localCompanyProfile in profiles
				where string.Equals(NormalizeOfficeCode(localCompanyProfile.OfficeCode, definition.OfficeCode), definition.OfficeCode, StringComparison.OrdinalIgnoreCase)
				orderby localCompanyProfile.IsDefaultForOffice descending, !localCompanyProfile.IsDeleted && localCompanyProfile.IsActive descending, localCompanyProfile.UpdatedAtUtc descending
				select localCompanyProfile).FirstOrDefault();
			if (current == null)
			{
				LocalCompanyProfile created = new LocalCompanyProfile
				{
					ProfileName = definition.ProfileName,
					OfficeCode = definition.OfficeCode,
					TradeName = definition.TradeName,
					Representative = definition.Representative,
					BusinessNumber = definition.BusinessNumber,
					BusinessType = string.Empty,
					BusinessItem = string.Empty,
					Address = definition.Address,
					ContactNumber = definition.ContactNumber,
					Email = string.Empty,
					BankAccountText = string.Empty,
					IsDefaultForOffice = true,
					IsActive = true,
					IsDeleted = false,
					IsDirty = false,
					CreatedAtUtc = now,
					UpdatedAtUtc = now
				};
				_db.CompanyProfiles.Add(created);
				profiles.Add(created);
				changed = true;
			}
			else
			{
				changed |= ApplyCompanyProfileDefault(current, definition, now);
			}
		}
		foreach (IGrouping<string, LocalCompanyProfile> group in profiles.Where((LocalCompanyProfile localCompanyProfile) => !localCompanyProfile.IsDeleted && localCompanyProfile.IsActive).GroupBy<LocalCompanyProfile, string>((LocalCompanyProfile localCompanyProfile) => NormalizeOfficeCode(localCompanyProfile.OfficeCode, DomainConstants.OfficeUsenet), StringComparer.OrdinalIgnoreCase))
		{
			LocalCompanyProfile canonical = (from localCompanyProfile in @group
				orderby localCompanyProfile.IsDefaultForOffice descending, localCompanyProfile.UpdatedAtUtc descending
				select localCompanyProfile).First();
			foreach (LocalCompanyProfile profile in group)
			{
				bool shouldBeDefault = profile.Id == canonical.Id;
				if (profile.IsDefaultForOffice != shouldBeDefault)
				{
					profile.IsDefaultForOffice = shouldBeDefault;
					profile.IsDirty = false;
					profile.UpdatedAtUtc = now;
					changed = true;
				}
			}
		}
		foreach (LocalSetting setting in assignmentSettings)
		{
			if (!string.IsNullOrWhiteSpace(setting.Value) && (!Guid.TryParse(setting.Value, out var profileId) || !profiles.Any((LocalCompanyProfile localCompanyProfile) => localCompanyProfile.Id == profileId && !localCompanyProfile.IsDeleted && localCompanyProfile.IsActive)))
			{
				setting.Value = string.Empty;
				changed = true;
			}
		}
		if (!changed)
		{
			return;
		}
		using (SuppressSyncDispatch())
		{
			await _db.SaveChangesAsync(ct);
			try
			{
				await _db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"UX_CompanyProfiles_DefaultPerOffice_Active\" ON \"CompanyProfiles\" (\"OfficeCode\") WHERE COALESCE(\"IsDefaultForOffice\", 0) = 1 AND COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(\"IsActive\", 1) = 1;");
			}
			catch (Exception ex)
			{
				AppLogger.Warn("LOCALDB", $"회사 기본 프로필 고유 인덱스 보강 실패: {ex.Message}");
			}
		}
	}

	private static bool ApplyCompanyProfileDefault(LocalCompanyProfile profile, CompanyProfileDefaultDefinition definition, DateTime now)
	{
		bool flag = false;
		string text = NormalizeOfficeCode(profile.OfficeCode, definition.OfficeCode);
		if (!string.Equals(profile.OfficeCode, text, StringComparison.OrdinalIgnoreCase))
		{
			profile.OfficeCode = text;
			flag = true;
		}
		string text2 = (string.IsNullOrWhiteSpace(profile.ProfileName) ? definition.ProfileName : profile.ProfileName.Trim());
		if (!string.Equals(profile.ProfileName, text2, StringComparison.CurrentCulture))
		{
			profile.ProfileName = text2;
			flag = true;
		}
		string text3 = (string.IsNullOrWhiteSpace(profile.TradeName) ? definition.TradeName : profile.TradeName.Trim());
		if (!string.Equals(profile.TradeName, text3, StringComparison.CurrentCulture))
		{
			profile.TradeName = text3;
			flag = true;
		}
		if (string.IsNullOrWhiteSpace(profile.Representative) && !string.IsNullOrWhiteSpace(definition.Representative))
		{
			profile.Representative = definition.Representative;
			flag = true;
		}
		if (string.IsNullOrWhiteSpace(profile.BusinessNumber) && !string.IsNullOrWhiteSpace(definition.BusinessNumber))
		{
			profile.BusinessNumber = definition.BusinessNumber;
			flag = true;
		}
		if (string.IsNullOrWhiteSpace(profile.Address) && !string.IsNullOrWhiteSpace(definition.Address))
		{
			profile.Address = definition.Address;
			flag = true;
		}
		if (string.IsNullOrWhiteSpace(profile.ContactNumber) && !string.IsNullOrWhiteSpace(definition.ContactNumber))
		{
			profile.ContactNumber = definition.ContactNumber;
			flag = true;
		}
		if (!profile.IsDefaultForOffice)
		{
			profile.IsDefaultForOffice = true;
			flag = true;
		}
		if (!profile.IsActive)
		{
			profile.IsActive = true;
			flag = true;
		}
		if (profile.IsDeleted)
		{
			profile.IsDeleted = false;
			flag = true;
		}
		if (flag)
		{
			profile.IsDirty = false;
			profile.UpdatedAtUtc = now;
		}
		return flag;
	}

	public async Task<Guid?> GetAssignedCompanyProfileIdAsync(string? username, CancellationToken ct = default(CancellationToken))
	{
		string normalizedUsername = NormalizeUsername(username);
		if (string.IsNullOrWhiteSpace(normalizedUsername))
		{
			return null;
		}
		string settingKey = GetCompanyProfileAssignmentKey(normalizedUsername);
		return await GetValidatedGuidSettingAsync(settingKey, (Guid profileId) => _db.CompanyProfiles.IgnoreQueryFilters().AnyAsync((LocalCompanyProfile profile) => profile.Id == profileId && !profile.IsDeleted && profile.IsActive, ct), ct);
	}

	public async Task SetAssignedCompanyProfileAsync(string? username, Guid? profileId, CancellationToken ct = default(CancellationToken))
	{
		string normalizedUsername = NormalizeUsername(username);
		if (!string.IsNullOrWhiteSpace(normalizedUsername))
		{
			string value = string.Empty;
			if (profileId.HasValue && profileId.Value != Guid.Empty && await ValidateGuidReferenceAsync(profileId.Value, (Guid candidateId) => _db.CompanyProfiles.IgnoreQueryFilters().AnyAsync((LocalCompanyProfile profile) => profile.Id == candidateId && !profile.IsDeleted && profile.IsActive, ct)))
			{
				value = profileId.Value.ToString("D");
			}
			await SetSettingAsync(GetCompanyProfileAssignmentKey(normalizedUsername), value, ct);
		}
	}

	public async Task<Guid?> GetValidatedGuidSettingAsync(string settingKey, Func<Guid, Task<bool>> existsAsync, CancellationToken ct = default(CancellationToken))
	{
		if (!Guid.TryParse(await GetSettingAsync(settingKey, ct), out var referenceId))
		{
			return null;
		}
		if (await ValidateGuidReferenceAsync(referenceId, existsAsync))
		{
			return referenceId;
		}
		await SetSettingAsync(settingKey, string.Empty, ct);
		return null;
	}

	public async Task SetValidatedGuidSettingAsync(string settingKey, Guid? referenceId, Func<Guid, Task<bool>> existsAsync, CancellationToken ct = default(CancellationToken))
	{
		string value = string.Empty;
		if (referenceId is Guid currentId && currentId != Guid.Empty && await ValidateGuidReferenceAsync(currentId, existsAsync))
		{
			value = currentId.ToString("D");
		}
		await SetSettingAsync(settingKey, value, ct);
	}

	private static async Task<bool> ValidateGuidReferenceAsync(Guid referenceId, Func<Guid, Task<bool>> existsAsync)
	{
		if (referenceId == Guid.Empty)
		{
			return false;
		}
		if (existsAsync == null)
		{
			return false;
		}
		if (await existsAsync(referenceId))
		{
			return true;
		}
		return false;
	}

	public Task<List<LocalUnit>> GetUnitsAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from u in _db.Units.AsNoTracking()
			where u.IsActive
			orderby u.Name
			select u).ToListAsync(ct);
	}

	public async Task EnsureCustomerCategoryIntegrityAsync(CancellationToken ct = default(CancellationToken))
	{
		await CustomerCategoryMaintenance.NormalizeAsync(_db, ct);
	}

	public async Task<List<LocalCustomerCategory>> GetCategoriesAsync(CancellationToken ct = default(CancellationToken))
	{
		await EnsureCustomerCategoryIntegrityAsync(ct);
		return await (from c in _db.CustomerCategories.AsNoTracking()
			orderby c.Name, c.CreatedAtUtc
			select c).ToListAsync(ct);
	}

	public Task<List<LocalPriceGradeOption>> GetPriceGradeOptionsAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from option in _db.PriceGradeOptions.AsNoTracking()
			where option.IsActive
			orderby option.SortOrder, option.Name
			select option).ToListAsync(ct);
	}

	public async Task<List<LocalTradeTypeOption>> GetTradeTypeOptionsAsync(CancellationToken ct = default(CancellationToken))
	{
		await EnsureTradeTypeOptionIntegrityAsync(ct);
		return await (from option in _db.TradeTypeOptions.AsNoTracking()
			where option.IsActive
			orderby option.SortOrder, option.Name
			select option).ToListAsync(ct);
	}

	public Task<List<LocalItemCategoryOption>> GetItemCategoryOptionsAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from option in _db.ItemCategoryOptions.AsNoTracking()
			where option.IsActive
			orderby option.SortOrder, option.Name
			select option).ToListAsync(ct);
	}

	private static long? ResolveSelectionOptionExpectedRevision(long? expectedRevision, long candidateRevision)
	{
		if (expectedRevision is > 0)
		{
			return expectedRevision;
		}

		return candidateRevision > 0 ? candidateRevision : null;
	}

	public async Task<LocalMutationResult> SaveCustomerCategoryAsync(LocalCustomerCategory category, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (category == null)
		{
			throw new ArgumentNullException("category");
		}
		await EnsureCustomerCategoryIntegrityAsync(ct);
		string name = DefaultCustomerCategories.NormalizeName(category.Name);
		if (string.IsNullOrWhiteSpace(name))
		{
			return LocalMutationResult.Denied("고객분류 이름을 입력하세요.");
		}
		List<LocalCustomerCategory> categories = await _db.CustomerCategories.IgnoreQueryFilters().ToListAsync(ct);
		if (categories.Any((LocalCustomerCategory current) => current.Id != category.Id && !current.IsDeleted && string.Equals(DefaultCustomerCategories.NormalizeName(current.Name), name, StringComparison.CurrentCultureIgnoreCase)))
		{
			return LocalMutationResult.Denied("같은 이름의 고객분류가 이미 있습니다.");
		}
		DateTime now = DateTime.UtcNow;
		var existing = categories.FirstOrDefault((LocalCustomerCategory current) => current.Id == category.Id);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		var expected = ResolveSelectionOptionExpectedRevision(expectedRevision, category.Revision);
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(existing, expected, "고객분류", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		if (existing == null)
		{
			LocalCustomerCategory created = new LocalCustomerCategory
			{
				Id = ((category.Id == Guid.Empty) ? Guid.NewGuid() : category.Id),
				Name = name,
				IsSystemDefault = false,
				IsDeleted = false,
				IsDirty = true,
				CreatedAtUtc = now,
				UpdatedAtUtc = now
			};
			_db.CustomerCategories.Add(created);
			await _db.SaveChangesAsync(ct);
			return LocalMutationResult.Ok(created.Id, "고객분류를 추가했습니다.");
		}
		existing.Name = name;
		existing.IsSystemDefault = false;
		existing.IsDeleted = false;
		existing.IsDirty = true;
		existing.UpdatedAtUtc = now;
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(existing.Id, "고객분류를 수정했습니다.");
	}

	public async Task<LocalMutationResult> DeleteCustomerCategoryAsync(Guid categoryId, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		await EnsureCustomerCategoryIntegrityAsync(ct);
		var category = await _db.CustomerCategories.IgnoreQueryFilters().FirstOrDefaultAsync((LocalCustomerCategory current) => current.Id == categoryId, ct);
		category = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, category, ct);
		if (category == null)
		{
			return LocalMutationResult.Missing("고객분류를 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(category, expectedRevision, "고객분류", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		bool flag = await _db.Customers.IgnoreQueryFilters().AnyAsync((LocalCustomer customer) => customer.CategoryId == categoryId, ct);
		if (!flag)
		{
			flag = await _db.CustomerMasters.IgnoreQueryFilters().AnyAsync((LocalCustomerMaster customer) => customer.CategoryId == categoryId, ct);
		}
		if (flag)
		{
			return LocalMutationResult.Denied("사용 중인 고객분류는 삭제할 수 없습니다.");
		}
		category.IsDeleted = true;
		category.IsDirty = true;
		category.UpdatedAtUtc = DateTime.UtcNow;
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(category.Id, "고객분류를 삭제했습니다.");
	}

	public async Task<LocalMutationResult> SavePriceGradeOptionAsync(LocalPriceGradeOption option, string? previousName = null, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (option == null)
		{
			throw new ArgumentNullException("option");
		}
		string name = (option.Name ?? string.Empty).Trim();
		if (string.IsNullOrWhiteSpace(name))
		{
			return LocalMutationResult.Denied("가격등급 이름을 입력하세요.");
		}
		string source = SelectionOptionDefaults.NormalizePriceSource(option.PriceSource);
		List<LocalPriceGradeOption> options = await _db.PriceGradeOptions.IgnoreQueryFilters().ToListAsync(ct);
		DateTime now = DateTime.UtcNow;
		var existing = options.FirstOrDefault((LocalPriceGradeOption current) => current.Id == option.Id);
		var sameNameOption = options.FirstOrDefault((LocalPriceGradeOption current) => current.Id != option.Id && string.Equals((current.Name ?? string.Empty).Trim(), name, StringComparison.CurrentCultureIgnoreCase));
		if (sameNameOption != null)
		{
			if (existing != null || (!sameNameOption.IsDeleted && sameNameOption.IsActive))
			{
				return LocalMutationResult.Denied("같은 이름의 가격등급이 이미 있습니다.");
			}
			existing = sameNameOption;
		}
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		var expected = ResolveSelectionOptionExpectedRevision(expectedRevision, option.Revision);
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(existing, expected, "가격등급", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		string oldName = (previousName ?? existing?.Name ?? string.Empty).Trim();
		if (existing == null)
		{
			LocalPriceGradeOption created = new LocalPriceGradeOption
			{
				Id = ((option.Id == Guid.Empty) ? Guid.NewGuid() : option.Id),
				Name = name,
				PriceSource = source,
				SortOrder = option.SortOrder,
				IsSystemDefault = false,
				IsActive = true,
				IsDeleted = false,
				IsDirty = true,
				CreatedAtUtc = now,
				UpdatedAtUtc = now
			};
			_db.PriceGradeOptions.Add(created);
			await _db.SaveChangesAsync(ct);
			return LocalMutationResult.Ok(created.Id, "가격등급을 추가했습니다.");
		}
		existing.Name = name;
		existing.PriceSource = source;
		existing.SortOrder = option.SortOrder;
		existing.IsSystemDefault = false;
		existing.IsActive = true;
		existing.IsDeleted = false;
		existing.IsDirty = true;
		existing.UpdatedAtUtc = now;
		if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
		{
			foreach (LocalCustomer customer in await (from localCustomer in _db.Customers.IgnoreQueryFilters()
				where localCustomer.PriceGrade == oldName
				select localCustomer).ToListAsync(ct))
			{
				customer.PriceGrade = name;
				customer.IsDirty = true;
				customer.UpdatedAtUtc = now;
			}
		}
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(existing.Id, "가격등급을 수정했습니다.");
	}

	public async Task<LocalMutationResult> DeletePriceGradeOptionAsync(Guid optionId, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		var option = await _db.PriceGradeOptions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalPriceGradeOption current) => current.Id == optionId, ct);
		option = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, option, ct);
		if (option == null)
		{
			return LocalMutationResult.Missing("가격등급을 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(option, expectedRevision, "가격등급", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		if (await _db.Customers.IgnoreQueryFilters().AnyAsync((LocalCustomer customer) => customer.PriceGrade == option.Name, ct))
		{
			return LocalMutationResult.Denied("사용 중인 가격등급은 삭제할 수 없습니다.");
		}
		option.IsDeleted = true;
		option.IsDirty = true;
		option.IsActive = false;
		option.UpdatedAtUtc = DateTime.UtcNow;
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(option.Id, "가격등급을 삭제했습니다.");
	}

	public async Task<LocalMutationResult> SaveTradeTypeOptionAsync(LocalTradeTypeOption option, string? previousName = null, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (option == null)
		{
			throw new ArgumentNullException("option");
		}
		await EnsureTradeTypeOptionIntegrityAsync(ct);
		if (!CustomerClassificationNormalizer.TryNormalizeTradeType(option.Name, out string name))
		{
			return LocalMutationResult.Denied("거래구분은 매출, 매입, 매출/매입 3개만 사용할 수 있습니다.");
		}
		var canonical = CustomerClassificationNormalizer.TradeTypeDefinition.Find(name);
		if (canonical == null)
		{
			return LocalMutationResult.Denied("거래구분은 매출, 매입, 매출/매입 3개만 사용할 수 있습니다.");
		}
		List<LocalTradeTypeOption> options = await _db.TradeTypeOptions.IgnoreQueryFilters().ToListAsync(ct);
		DateTime now = DateTime.UtcNow;
		var existing = options.FirstOrDefault((LocalTradeTypeOption current) => current.Id == option.Id);
		var sameNameOption = options.FirstOrDefault((LocalTradeTypeOption current) => current.Id != option.Id && string.Equals((current.Name ?? string.Empty).Trim(), name, StringComparison.CurrentCultureIgnoreCase));
		if (sameNameOption != null)
		{
			if (existing != null || (!sameNameOption.IsDeleted && sameNameOption.IsActive))
			{
				return LocalMutationResult.Denied("같은 이름의 거래구분이 이미 있습니다.");
			}
			existing = sameNameOption;
		}
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		var expected = ResolveSelectionOptionExpectedRevision(expectedRevision, option.Revision);
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(existing, expected, "거래구분", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		string oldName = CustomerTradeTypes.Normalize(previousName ?? existing?.Name);
		if (existing == null)
		{
			LocalTradeTypeOption created = new LocalTradeTypeOption
			{
				Id = ((option.Id == Guid.Empty) ? Guid.NewGuid() : option.Id),
				Name = name,
				AllowsSales = canonical.AllowsSales,
				AllowsPurchase = canonical.AllowsPurchase,
				SortOrder = canonical.SortOrder,
				IsSystemDefault = false,
				IsActive = true,
				IsDeleted = false,
				IsDirty = true,
				CreatedAtUtc = now,
				UpdatedAtUtc = now
			};
			_db.TradeTypeOptions.Add(created);
			await _db.SaveChangesAsync(ct);
			return LocalMutationResult.Ok(created.Id, "거래구분을 추가했습니다.");
		}
		existing.Name = name;
		existing.AllowsSales = canonical.AllowsSales;
		existing.AllowsPurchase = canonical.AllowsPurchase;
		existing.SortOrder = canonical.SortOrder;
		existing.IsSystemDefault = false;
		existing.IsActive = true;
		existing.IsDeleted = false;
		existing.IsDirty = true;
		existing.UpdatedAtUtc = now;
		if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
		{
			foreach (LocalCustomer customer in await (from localCustomer in _db.Customers.IgnoreQueryFilters()
				where localCustomer.TradeType == oldName
				select localCustomer).ToListAsync(ct))
			{
				customer.TradeType = name;
				customer.IsDirty = true;
				customer.UpdatedAtUtc = now;
			}
		}
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(existing.Id, "거래구분을 수정했습니다.");
	}

	public async Task<LocalMutationResult> DeleteTradeTypeOptionAsync(Guid optionId, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		await EnsureTradeTypeOptionIntegrityAsync(ct);
		var option = await _db.TradeTypeOptions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTradeTypeOption current) => current.Id == optionId, ct);
		option = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, option, ct);
		if (option == null)
		{
			return LocalMutationResult.Missing("거래구분을 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(option, expectedRevision, "거래구분", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		if (CustomerClassificationNormalizer.TradeTypeDefinition.Find(option.Name) is not null)
		{
			return LocalMutationResult.Denied("거래구분 기준값은 시스템 고정값이라 삭제할 수 없습니다.");
		}
		if (await _db.Customers.IgnoreQueryFilters().AnyAsync((LocalCustomer customer) => customer.TradeType == option.Name, ct))
		{
			return LocalMutationResult.Denied("사용 중인 거래구분은 삭제할 수 없습니다.");
		}
		option.IsDeleted = true;
		option.IsDirty = true;
		option.IsActive = false;
		option.UpdatedAtUtc = DateTime.UtcNow;
		await _db.SaveChangesAsync(ct);
		return LocalMutationResult.Ok(option.Id, "거래구분을 삭제했습니다.");
	}

	private void NormalizeCustomerClassification(LocalCustomer customer)
	{
		if (customer == null)
		{
			throw new ArgumentNullException("customer");
		}
		string value = (customer.TradeType ?? string.Empty).Trim();
		DefaultCustomerCategoryDefinition category2;
		if (CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(value, out DefaultCustomerCategoryDefinition category, out string normalizedTradeType))
		{
			if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
			{
				customer.CategoryId = category.Id;
			}
			customer.TradeType = normalizedTradeType;
		}
		else if (CustomerClassificationNormalizer.TryResolveCategory(value, out category2))
		{
			if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
			{
				customer.CategoryId = category2.Id;
			}
			customer.TradeType = "매출";
		}
		else
		{
			customer.TradeType = CustomerTradeTypes.Normalize(value);
		}
	}

	private async Task EnsureTradeTypeOptionIntegrityAsync(CancellationToken ct = default(CancellationToken))
	{
		DateTime now = DateTime.UtcNow;
		List<LocalTradeTypeOption> options = await _db.TradeTypeOptions.IgnoreQueryFilters().ToListAsync(ct);
		bool changed = false;
		foreach (SelectionOptionDefaults.TradeTypeDefinition definition in SelectionOptionDefaults.DefaultTradeTypes)
		{
			var existing = options.FirstOrDefault((LocalTradeTypeOption localTradeTypeOption) => localTradeTypeOption.Id == definition.Id) ?? options.FirstOrDefault((LocalTradeTypeOption localTradeTypeOption) => string.Equals(localTradeTypeOption.Name?.Trim(), definition.Name, StringComparison.CurrentCultureIgnoreCase));
			if (existing == null)
			{
				LocalTradeTypeOption created = new LocalTradeTypeOption
				{
					Id = definition.Id,
					Name = definition.Name,
					AllowsSales = definition.AllowsSales,
					AllowsPurchase = definition.AllowsPurchase,
					SortOrder = definition.SortOrder,
					IsSystemDefault = false,
					IsActive = true,
					IsDeleted = false,
					IsDirty = true,
					CreatedAtUtc = now,
					UpdatedAtUtc = now
				};
				_db.TradeTypeOptions.Add(created);
				options.Add(created);
				changed = true;
			}
			else
			{
				changed |= ApplyTradeTypeOptionIntegrity(existing, definition.Name, definition.AllowsSales, definition.AllowsPurchase, definition.SortOrder, now);
			}
		}
		foreach (LocalTradeTypeOption option in options.Where((LocalTradeTypeOption localTradeTypeOption) => !localTradeTypeOption.IsDeleted && CustomerClassificationNormalizer.TradeTypeDefinition.Find(localTradeTypeOption.Name) is null))
		{
			option.IsDeleted = true;
			option.IsActive = false;
			option.IsDirty = true;
			option.UpdatedAtUtc = now;
			changed = true;
		}
		if (changed)
		{
			await _db.SaveChangesAsync(ct);
		}
	}

	private static bool ApplyTradeTypeOptionIntegrity(LocalTradeTypeOption option, string canonicalName, bool allowsSales, bool allowsPurchase, int sortOrder, DateTime now)
	{
		bool flag = false;
		if (!string.Equals(option.Name, canonicalName, StringComparison.CurrentCulture))
		{
			option.Name = canonicalName;
			flag = true;
		}
		if (option.AllowsSales != allowsSales)
		{
			option.AllowsSales = allowsSales;
			flag = true;
		}
		if (option.AllowsPurchase != allowsPurchase)
		{
			option.AllowsPurchase = allowsPurchase;
			flag = true;
		}
		if (option.SortOrder != sortOrder)
		{
			option.SortOrder = sortOrder;
			flag = true;
		}
		if (!option.IsActive)
		{
			option.IsActive = true;
			flag = true;
		}
		if (option.IsDeleted)
		{
			option.IsDeleted = false;
			flag = true;
		}
		if (flag)
		{
			option.IsDirty = true;
			option.UpdatedAtUtc = now;
		}
		return flag;
	}

	public async Task<LocalMutationResult> SaveItemCategoryOptionAsync(LocalItemCategoryOption option, string? previousName = null, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (option == null)
		{
			throw new ArgumentNullException("option");
		}
		string name = SelectionOptionDefaults.NormalizeItemCategoryName(option.Name);
		if (string.IsNullOrWhiteSpace(name))
		{
			return LocalMutationResult.Denied("품목분류 이름을 입력하세요.");
		}
		string nameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(name);
		IEnumerable<LocalItemCategoryOption> local = _db.ItemCategoryOptions.Local;
		List<LocalItemCategoryOption> options = (from localItemCategoryOption in local.Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
			group localItemCategoryOption by localItemCategoryOption.Id into @group
			select @group.First()).ToList();
		DateTime now = DateTime.UtcNow;
		var existing = options.FirstOrDefault((LocalItemCategoryOption current) => current.Id == option.Id);
		var sameNameOption = options.FirstOrDefault((LocalItemCategoryOption current) => current.Id != option.Id && string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(current.Name), nameKey, StringComparison.OrdinalIgnoreCase));
		if (sameNameOption != null)
		{
			if (existing != null || (!sameNameOption.IsDeleted && sameNameOption.IsActive))
			{
				return LocalMutationResult.Denied("같은 이름의 품목분류가 이미 있습니다.");
			}
			existing = sameNameOption;
		}
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		var expected = ResolveSelectionOptionExpectedRevision(expectedRevision, option.Revision);
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(existing, expected, "품목분류", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		string oldName = SelectionOptionDefaults.NormalizeItemCategoryName(previousName ?? existing?.Name);
		if (existing == null)
		{
			LocalItemCategoryOption created = new LocalItemCategoryOption
			{
				Id = ((option.Id == Guid.Empty) ? Guid.NewGuid() : option.Id),
				Name = name,
				SortOrder = option.SortOrder,
				IsSystemDefault = false,
				IsActive = true,
				IsDeleted = false,
				IsDirty = true,
				CreatedAtUtc = now,
				UpdatedAtUtc = now
			};
			_db.ItemCategoryOptions.Add(created);
			await _db.SaveChangesAsync(ct);
			RaiseInventoryStateChanged();
			return LocalMutationResult.Ok(created.Id, "품목분류를 추가했습니다.");
		}
		existing.Name = name;
		existing.SortOrder = option.SortOrder;
		existing.IsSystemDefault = false;
		existing.IsActive = true;
		existing.IsDeleted = false;
		existing.IsDirty = true;
		existing.UpdatedAtUtc = now;
		if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
		{
			string oldKey = RentalCatalogValueNormalizer.NormalizeLooseKey(oldName);
			foreach (LocalItem item in (await _db.Items.IgnoreQueryFilters().ToListAsync(ct)).Where((LocalItem localItem) => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(localItem.CategoryName), oldKey, StringComparison.OrdinalIgnoreCase)))
			{
				item.CategoryName = name;
				item.IsDirty = true;
				item.UpdatedAtUtc = now;
			}
			foreach (LocalRentalAsset asset in (await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct)).Where((LocalRentalAsset localRentalAsset) => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(localRentalAsset.ItemCategoryName), oldKey, StringComparison.OrdinalIgnoreCase)))
			{
				asset.ItemCategoryName = name;
				asset.IsDirty = true;
				asset.UpdatedAtUtc = now;
			}
		}
		await _db.SaveChangesAsync(ct);
		RaiseInventoryStateChanged();
		return LocalMutationResult.Ok(existing.Id, "품목분류를 수정했습니다.");
	}

	public async Task<LocalMutationResult> DeleteItemCategoryOptionAsync(Guid optionId, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		var option = await _db.ItemCategoryOptions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalItemCategoryOption current) => current.Id == optionId, ct);
		option = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, option, ct);
		if (option == null)
		{
			return LocalMutationResult.Missing("품목분류를 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(option, expectedRevision, "품목분류", out string conflictMessage))
		{
			return LocalMutationResult.Conflict(conflictMessage);
		}
		string optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
		bool itemInUse = (await _db.Items.IgnoreQueryFilters().ToListAsync(ct)).Any((LocalItem item) => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
		bool rentalInUse = (await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct)).Any((LocalRentalAsset asset) => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
		if (itemInUse || rentalInUse)
		{
			return LocalMutationResult.Denied("사용 중인 품목분류는 삭제할 수 없습니다.");
		}
		option.IsDeleted = true;
		option.IsDirty = true;
		option.IsActive = false;
		option.UpdatedAtUtc = DateTime.UtcNow;
		await _db.SaveChangesAsync(ct);
		RaiseInventoryStateChanged();
		return LocalMutationResult.Ok(option.Id, "품목분류를 삭제했습니다.");
	}

	public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default(CancellationToken))
	{
		return (await _db.Settings.FindAsync(new object[1] { key }, ct))?.Value;
	}

	public async Task SetSettingAsync(string key, string value, CancellationToken ct = default(CancellationToken))
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				var setting = await _db.Settings.FindAsync(new object[1] { key }, ct);
				if (setting == null)
				{
					_db.Settings.Add(new LocalSetting
					{
						Key = key,
						Value = value
					});
				}
				else
				{
					if (string.Equals(setting.Value, value, StringComparison.Ordinal))
					{
						break;
					}
					setting.Value = value;
				}
				await _db.SaveChangesAsync(ct);
				break;
			}
			catch (DbUpdateConcurrencyException) when (attempt == 0)
			{
				_db.ChangeTracker.Clear();
			}
		}
	}

	public async Task<string?> GetInvoicePrintPayloadAsync(Guid invoiceId, CancellationToken ct = default(CancellationToken))
	{
		string key = BuildInvoicePrintSettingKey(invoiceId);
		return (await _db.Settings.FindAsync(new object[1] { key }, ct))?.Value;
	}

	public async Task SaveInvoicePrintPayloadAsync(Guid invoiceId, string payloadJson, CancellationToken ct = default(CancellationToken))
	{
		string key = BuildInvoicePrintSettingKey(invoiceId);
		var setting = await _db.Settings.FindAsync(new object[1] { key }, ct);
		if (setting == null)
		{
			_db.Settings.Add(new LocalSetting
			{
				Key = key,
				Value = (payloadJson ?? string.Empty)
			});
		}
		else
		{
			setting.Value = payloadJson ?? string.Empty;
		}
		await _db.SaveChangesAsync(ct);
	}

	private static string BuildInvoicePrintSettingKey(Guid invoiceId)
	{
		return $"InvoicePrint:{invoiceId:N}";
	}

	public async Task<List<AttachmentSelectionState>> GetAttachmentSelectionsAsync(string customerKey, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(customerKey))
		{
			return new List<AttachmentSelectionState>();
		}
		try
		{
			return await (from selection in _db.AttachmentSelections.AsNoTracking()
				where selection.CustomerKey == customerKey
				orderby selection.OrderIndex ?? int.MaxValue, selection.DocCode
				select new AttachmentSelectionState
				{
					DocCode = selection.DocCode,
					IsChecked = selection.IsChecked,
					OrderIndex = selection.OrderIndex
				}).ToListAsync(ct);
		}
		catch (Exception ex)
		{
			AppLogger.Error("AttachmentSelection", "Failed to load attachment selections for key '" + customerKey + "'.", ex);
			return new List<AttachmentSelectionState>();
		}
	}

	public async Task SaveAttachmentSelectionsAsync(string customerKey, IReadOnlyCollection<AttachmentSelectionState> selections, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(customerKey))
		{
			return;
		}
		try
		{
			IReadOnlyCollection<AttachmentSelectionState> incoming = (IReadOnlyCollection<AttachmentSelectionState>)(((object)selections) ?? ((object)Array.Empty<AttachmentSelectionState>()));
			DateTime now = DateTime.UtcNow;
			Dictionary<string, AttachmentSelectionState> incomingByCode = incoming.Where((AttachmentSelectionState selection) => !string.IsNullOrWhiteSpace(selection.DocCode)).GroupBy<AttachmentSelectionState, string>((AttachmentSelectionState selection) => selection.DocCode, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, AttachmentSelectionState>, string, AttachmentSelectionState>((IGrouping<string, AttachmentSelectionState> group) => group.Key, (IGrouping<string, AttachmentSelectionState> group) => group.Last(), StringComparer.OrdinalIgnoreCase);
			List<LocalAttachmentSelection> existing = await _db.AttachmentSelections.Where((LocalAttachmentSelection selection) => selection.CustomerKey == customerKey).ToListAsync(ct);
			foreach (LocalAttachmentSelection row in existing)
			{
				if (!incomingByCode.TryGetValue(row.DocCode, out var state))
				{
					_db.AttachmentSelections.Remove(row);
					continue;
				}
				row.IsChecked = state.IsChecked;
				row.OrderIndex = state.OrderIndex;
				row.UpdatedAtUtc = now;
				state = null;
			}
			foreach (AttachmentSelectionState state2 in incomingByCode.Values)
			{
				if (!existing.Any((LocalAttachmentSelection existingRow) => string.Equals(existingRow.DocCode, state2.DocCode, StringComparison.OrdinalIgnoreCase)))
				{
					_db.AttachmentSelections.Add(new LocalAttachmentSelection
					{
						CustomerKey = customerKey,
						DocCode = state2.DocCode,
						IsChecked = state2.IsChecked,
						OrderIndex = state2.OrderIndex,
						UpdatedAtUtc = now
					});
				}
			}
			await _db.SaveChangesAsync(ct);
		}
		catch (Exception ex)
		{
			AppLogger.Error("AttachmentSelection", "Failed to save attachment selections for key '" + customerKey + "'.", ex);
		}
	}

	private const string CachedSessionUsernameSuffix = "Username";
	private const string CachedSessionRoleSuffix = "Role";
	private const string CachedSessionPermissionsSuffix = "Permissions";
	private const string CachedSessionTenantCodeSuffix = "TenantCode";
	private const string CachedSessionScopeTypeSuffix = "ScopeType";
	private const string CachedSessionOfficeCodeSuffix = "OfficeCode";
	private const string CachedSessionPasswordProofSuffix = "PasswordProof";

	public async Task SaveSessionCacheAsync(string username, string role, IEnumerable<string> permissions, string? tenantCode = null, string? scopeType = null, string? officeCode = null, string? password = null, CancellationToken ct = default(CancellationToken))
	{
		string displayUsername = (username ?? string.Empty).Trim();
		string normalizedUsername = NormalizeUsername(displayUsername);
		if (string.IsNullOrWhiteSpace(normalizedUsername))
		{
			return;
		}

		string normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode);
		string normalizedScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType, DomainConstants.IsAdminRole(role) ? "Admin" : "OfficeOnly");
		string normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
		string permissionsText = string.Join(',', permissions);
		string? passwordProof = !string.IsNullOrEmpty(password) ? ProtectOfflinePasswordProof(password) : null;

		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionUsernameSuffix, displayUsername, ct);
		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionRoleSuffix, role, ct);
		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionPermissionsSuffix, permissionsText, ct);
		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionTenantCodeSuffix, normalizedTenantCode, ct);
		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionScopeTypeSuffix, normalizedScopeType, ct);
		await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionOfficeCodeSuffix, normalizedOfficeCode, ct);
		if (passwordProof is not null)
		{
			await SetCachedSessionSettingAsync(normalizedUsername, CachedSessionPasswordProofSuffix, passwordProof, ct);
		}

		await SetSettingAsync("CachedSession_Username", displayUsername, ct);
		await SetSettingAsync("CachedSession_Role", role, ct);
		await SetSettingAsync("CachedSession_Permissions", permissionsText, ct);
		await SetSettingAsync("CachedSession_TenantCode", normalizedTenantCode, ct);
		await SetSettingAsync("CachedSession_ScopeType", normalizedScopeType, ct);
		await SetSettingAsync("CachedSession_OfficeCode", normalizedOfficeCode, ct);
		if (passwordProof is not null)
		{
			await SetSettingAsync("CachedSession_PasswordProof", passwordProof, ct);
		}
	}

	public async Task<bool> VerifyCachedSessionPasswordAsync(string username, string password, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
		{
			return false;
		}

		string normalizedUsername = NormalizeUsername(username);
		string? cachedUsername = await GetCachedSessionSettingAsync(normalizedUsername, CachedSessionUsernameSuffix, ct);
		if (string.Equals(cachedUsername, username, StringComparison.OrdinalIgnoreCase))
		{
			string? protectedProof = await GetCachedSessionSettingAsync(normalizedUsername, CachedSessionPasswordProofSuffix, ct);
			return VerifyOfflinePasswordProof(password, protectedProof);
		}

		string? legacyCachedUsername = await GetSettingAsync("CachedSession_Username", ct);
		if (!string.Equals(legacyCachedUsername, username, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return VerifyOfflinePasswordProof(password, await GetSettingAsync("CachedSession_PasswordProof", ct));
	}

	public async Task<UserSessionDto?> GetCachedSessionAsync(string username, CancellationToken ct = default(CancellationToken))
	{
		string normalizedUsername = NormalizeUsername(username);
		string? cachedUsername = await GetCachedSessionSettingAsync(normalizedUsername, CachedSessionUsernameSuffix, ct);
		bool useLegacyCache = false;

		if (!string.Equals(cachedUsername, username, StringComparison.OrdinalIgnoreCase))
		{
			string? legacyCachedUsername = await GetSettingAsync("CachedSession_Username", ct);
			if (!string.Equals(legacyCachedUsername, username, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			cachedUsername = legacyCachedUsername;
			useLegacyCache = true;
		}

		string role = await ReadCachedSessionValueAsync(normalizedUsername, CachedSessionRoleSuffix, useLegacyCache, ct) ?? "User";
		string tenantCode = await ReadCachedSessionValueAsync(normalizedUsername, CachedSessionTenantCodeSuffix, useLegacyCache, ct) ?? string.Empty;
		string scopeType = await ReadCachedSessionValueAsync(normalizedUsername, CachedSessionScopeTypeSuffix, useLegacyCache, ct) ?? string.Empty;
		string officeCode = await ReadCachedSessionValueAsync(normalizedUsername, CachedSessionOfficeCodeSuffix, useLegacyCache, ct) ?? string.Empty;
		string permissionsRaw = await ReadCachedSessionValueAsync(normalizedUsername, CachedSessionPermissionsSuffix, useLegacyCache, ct) ?? string.Empty;
		List<string> permissions = permissionsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
		return new UserSessionDto
		{
			UserId = Guid.Empty,
			Username = (cachedUsername ?? username),
			Role = role,
			TenantCode = tenantCode,
			OfficeCode = officeCode,
			ScopeType = scopeType,
			Permissions = permissions
		};
	}

	public async Task<string?> GetCachedOfficeCodeAsync(string? username = null, CancellationToken ct = default(CancellationToken))
	{
		string normalizedUsername = NormalizeUsername(username);
		if (!string.IsNullOrWhiteSpace(normalizedUsername))
		{
			string? value = await GetCachedSessionSettingAsync(normalizedUsername, CachedSessionOfficeCodeSuffix, ct);
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
		}

		return await GetSettingAsync("CachedSession_OfficeCode", ct);
	}

	private Task SetCachedSessionSettingAsync(string normalizedUsername, string suffix, string value, CancellationToken ct)
		=> SetSettingAsync(GetCachedSessionSettingKey(normalizedUsername, suffix), value, ct);

	private Task<string?> GetCachedSessionSettingAsync(string normalizedUsername, string suffix, CancellationToken ct)
		=> string.IsNullOrWhiteSpace(normalizedUsername)
			? Task.FromResult<string?>(null)
			: GetSettingAsync(GetCachedSessionSettingKey(normalizedUsername, suffix), ct);

	private async Task<string?> ReadCachedSessionValueAsync(string normalizedUsername, string suffix, bool useLegacyCache, CancellationToken ct)
	{
		if (!useLegacyCache)
		{
			return await GetCachedSessionSettingAsync(normalizedUsername, suffix, ct);
		}

		return await GetSettingAsync("CachedSession_" + suffix, ct);
	}

	private static string GetCachedSessionSettingKey(string normalizedUsername, string suffix)
		=> "CachedSession." + NormalizeUsername(normalizedUsername) + "." + suffix;

	private static string ProtectOfflinePasswordProof(string password)
	{
		try
		{
			byte[] salt = RandomNumberGenerator.GetBytes(16);
			const int iterations = 120000;
			byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
			string payload = string.Join('|', "v1", iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
			byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), null, DataProtectionScope.CurrentUser);
			return Convert.ToBase64String(protectedBytes);
		}
		catch
		{
			return string.Empty;
		}
	}

	private static bool VerifyOfflinePasswordProof(string password, string? protectedProof)
	{
		if (string.IsNullOrWhiteSpace(protectedProof))
		{
			return false;
		}
		try
		{
			byte[] protectedBytes = Convert.FromBase64String(protectedProof);
			byte[] payloadBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
			string payload = Encoding.UTF8.GetString(payloadBytes);
			string[] parts = payload.Split('|');
			if (parts.Length != 4 ||
			    !string.Equals(parts[0], "v1", StringComparison.Ordinal) ||
			    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iterations) ||
			    iterations < 10000)
			{
				return false;
			}
			byte[] salt = Convert.FromBase64String(parts[2]);
			byte[] expectedHash = Convert.FromBase64String(parts[3]);
			byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
			return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
		}
		catch
		{
			return false;
		}
	}

	public Task<List<LocalTransaction>> GetTransactionsAsync(Guid customerId, CancellationToken ct = default(CancellationToken))
	{
		return (from transaction in _db.Transactions.AsNoTracking()
			where transaction.CustomerId == customerId
			orderby transaction.TransactionDate descending
			select transaction).ToListAsync(ct);
	}

	public Task<List<LocalTransaction>> GetTransactionsAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalTransaction> query = from transaction in _db.Transactions.AsNoTracking()
			where transaction.CustomerId == customerId
			select transaction;
		query = ApplyTransactionScope(query, session);
		return query.OrderByDescending((LocalTransaction transaction) => transaction.TransactionDate).ToListAsync(ct);
	}

	public Task<List<LocalTransaction>> GetTransactionsAsync(DateOnly from, DateOnly to, Guid? customerId = null, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalTransaction> source = from transaction in _db.Transactions.AsNoTracking()
			where transaction.TransactionDate >= @from && transaction.TransactionDate <= to
			select transaction;
		if (customerId.HasValue)
		{
			source = source.Where((LocalTransaction transaction) => transaction.CustomerId == ((Guid?)customerId).Value);
		}
		return (from transaction in source
			orderby transaction.TransactionDate descending, transaction.CreatedAtUtc descending
			select transaction).ToListAsync(ct);
	}

	public Task<List<LocalTransaction>> GetTransactionsAsync(DateOnly from, DateOnly to, Guid? customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalTransaction> query = from transaction in _db.Transactions.AsNoTracking()
			where transaction.TransactionDate >= @from && transaction.TransactionDate <= to
			select transaction;
		query = ApplyTransactionScope(query, session);
		if (customerId.HasValue)
		{
			query = query.Where((LocalTransaction transaction) => transaction.CustomerId == ((Guid?)customerId).Value);
		}
		return (from transaction in query
			orderby transaction.TransactionDate descending, transaction.CreatedAtUtc descending
			select transaction).ToListAsync(ct);
	}

	public async Task<decimal> GetAdvanceBalanceAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customerId, ct);
		if (customer == null)
		{
			return default(decimal);
		}
		string customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
		if (!CanAccessCustomer(customerTenantCode: TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, customerOfficeCode), customerId: customer.Id, customerOfficeCode: customer.ResponsibleOfficeCode, session: session, role: session.User?.Role, fallbackOfficeCode: customer.OfficeCode))
		{
			return default(decimal);
		}
		return await GetAdvanceBalanceCoreAsync(customerId, ct);
	}

	public async Task<CustomerFinancialSummary> GetCustomerFinancialSummaryAsync(Guid customerId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == customerId, ct);
		if (customer == null)
		{
			return new CustomerFinancialSummary();
		}
		string customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
		if (!CanAccessCustomer(customerTenantCode: TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, customerOfficeCode), customerId: customer.Id, customerOfficeCode: customer.ResponsibleOfficeCode, session: session, role: session.User?.Role, fallbackOfficeCode: customer.OfficeCode))
		{
			return new CustomerFinancialSummary();
		}
		List<LocalInvoice> scopedInvoices = (await (from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking().Include((LocalInvoice invoice) => invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted))
			where invoice.CustomerId == customerId && ((int)invoice.VoucherType == 0 || (int)invoice.VoucherType == 1)
			select invoice).ToListAsync(ct)).Where((LocalInvoice invoice) => IsCustomerFinancialSummaryInvoice(invoice) && CanAccessInvoice(invoice, session)).ToList();
		decimal receivableAmount = scopedInvoices.Where((LocalInvoice invoice) => invoice.VoucherType == VoucherType.Sales).Sum((LocalInvoice invoice) => Math.Max(0m, invoice.TotalAmount - invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted).Sum((LocalPayment payment) => payment.Amount)));
		decimal payableAmount = scopedInvoices.Where((LocalInvoice invoice) => invoice.VoucherType == VoucherType.Purchase).Sum((LocalInvoice invoice) => Math.Max(0m, invoice.TotalAmount - invoice.Payments.Where((LocalPayment payment) => !payment.IsDeleted).Sum((LocalPayment payment) => payment.Amount)));
		List<LocalTransaction> transactions = await (from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
			where !transaction.IsDeleted && transaction.CustomerId == customerId
			select transaction).ToListAsync(ct);
		decimal prepaymentAmount = transactions.Where((LocalTransaction transaction) => transaction.AdvanceDelta > 0m && (transaction.LinkedInvoiceId.HasValue || transaction.LinkedRentalBillingProfileId.HasValue)).Sum((LocalTransaction transaction) => transaction.AdvanceDelta);
		decimal prepaidAmount = transactions.Sum((LocalTransaction transaction) => transaction.PrepaidDelta);
		return new CustomerFinancialSummary
		{
			AdvanceBalance = transactions.Sum((LocalTransaction transaction) => transaction.AdvanceDelta),
			ReceivableAmount = receivableAmount,
			PayableAmount = payableAmount,
			PrepaymentAmount = prepaymentAmount,
			PrepaidAmount = prepaidAmount
		};
	}

	private static bool IsCustomerFinancialSummaryInvoice(LocalInvoice invoice)
	{
		return invoice is not null &&
			!invoice.IsDeleted &&
			invoice.IsLatestVersion &&
			invoice.IsConfirmed &&
			(invoice.VoucherType == VoucherType.Sales || invoice.VoucherType == VoucherType.Purchase);
	}

	public async Task<InvoiceSettlementSummary> GetInvoiceSettlementSummaryAsync(Guid invoiceId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var invoice = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalInvoice current) => current.Id == invoiceId, ct);
		if (invoice == null || !CanAccessInvoice(invoice, session))
		{
			return new InvoiceSettlementSummary();
		}
		decimal settledAmount = await GetInvoiceSettledAmountCoreAsync(invoiceId, ct);
		return new InvoiceSettlementSummary
		{
			InvoiceTotal = invoice.TotalAmount,
			SettledAmount = settledAmount,
			RemainingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount)
		};
	}

	public async Task<RentalSettlementSummary> GetRentalSettlementSummaryAsync(Guid billingProfileId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		return await GetRentalSettlementSummaryAsync(billingProfileId, null, null, session, ct);
	}

	public async Task<RentalSettlementSummary> GetRentalSettlementSummaryAsync(Guid billingProfileId, Guid? billingRunId, decimal? billedAmountOverride, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalRentalBillingProfile current) => current.Id == billingProfileId, ct);
		if (profile == null || !CanAccessRentalProfile(profile, session))
		{
			return new RentalSettlementSummary();
		}
		decimal settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, billingRunId, ct);
		decimal billedAmount = await ResolveBillingRunAmountAsync(profile, billingRunId, billedAmountOverride, ct);
		decimal outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
		return new RentalSettlementSummary
		{
			BilledAmount = billedAmount,
			SettledAmount = settledAmount,
			OutstandingAmount = outstandingAmount,
			BillingStatus = ((outstandingAmount <= 0m) ? "완료" : (string.IsNullOrWhiteSpace(profile.BillingStatus) ? "청구중" : profile.BillingStatus)),
			SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount),
			CompletionStatus = ((outstandingAmount <= 0m) ? "완료" : "미완료")
		};
	}

	public async Task<LocalRentalBillingProfile?> GetRentalBillingProfileAsync(Guid billingProfileId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalRentalBillingProfile current) => current.Id == billingProfileId, ct);
		return (profile == null || !CanAccessRentalProfile(profile, session)) ? null : profile;
	}

	public async Task<LocalTransaction> SaveTransactionAsync(LocalTransaction transaction, CancellationToken ct = default(CancellationToken))
	{
		var existing = await _db.Transactions.FindAsync(new object[1] { transaction.Id }, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		DateTime now = DateTime.UtcNow;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, transaction, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(transaction, existing, "수금/지급 내역", now, out string conflictMessage))
		{
			throw new InvalidOperationException(conflictMessage);
		}
		if (existing == null)
		{
			_db.Transactions.Add(transaction);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(transaction);
		}
		await _db.SaveChangesAsync(ct);
		return transaction;
	}

	public async Task<OfficeMutationResult> SaveTransactionAsync(LocalTransaction transaction, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (transaction == null)
		{
			throw new ArgumentNullException("transaction");
		}
		if (!CanEditPayments(session))
		{
			return OfficeMutationResult.Denied("권한이 없어 수금/지급을 저장할 수 없습니다.");
		}
		string requestedTransactionKind = transaction.TransactionKind;
		Guid requestedCustomerId = transaction.CustomerId;
		string requestedResponsibleOfficeCode = transaction.ResponsibleOfficeCode;
		decimal requestedSettlementAmount = transaction.SettlementAmount;
		Guid? requestedLinkedInvoiceId = transaction.LinkedInvoiceId;
		Guid? requestedLinkedRentalProfileId = transaction.LinkedRentalBillingProfileId;
		Guid? requestedLinkedRentalRunId = transaction.LinkedRentalBillingRunId;
		List<string> warnings = new List<string>();
		bool hasLinkedInvoiceRequest = transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty;
		var customer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == transaction.CustomerId, ct);
		string customerOfficeCode = string.Empty;
		if (customer == null)
		{
			if (!hasLinkedInvoiceRequest)
			{
				return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");
			}
		}
		else
		{
			customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
			if (!CanWriteCustomerScope(session, customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
			{
				if (!hasLinkedInvoiceRequest)
				{
					return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");
				}
				customer = null;
				customerOfficeCode = string.Empty;
			}
		}
		var existing = await _db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransaction current) => current.Id == transaction.Id, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		if (existing != null && !CanWriteOperationalScope(session, existing.TenantCode, existing.ResponsibleOfficeCode, existing.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");
		}
		_ = existing?.LinkedInvoiceId;
		Guid? previousLinkedRentalId = existing?.LinkedRentalBillingProfileId;
		Guid? previousLinkedRentalRunId = existing?.LinkedRentalBillingRunId;
		transaction.TransactionKind = PaymentFlowConstants.NormalizeTransactionKind(transaction.TransactionKind, transaction.PaymentTotal > 0m && transaction.ReceiptTotal <= 0m);
		LocalInvoice? linkedInvoice = null;
		if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
		{
			linkedInvoice = await _db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync((LocalInvoice current) => current.Id == transaction.LinkedInvoiceId.Value, ct);
			if (linkedInvoice == null)
			{
				return OfficeMutationResult.Missing("연결할 전표를 찾을 수 없습니다.");
			}
			if (!CanWriteOperationalScope(session, linkedInvoice.TenantCode, linkedInvoice.ResponsibleOfficeCode, linkedInvoice.OfficeCode))
			{
				return OfficeMutationResult.Denied("권한이 없어 해당 전표 결제를 처리할 수 없습니다.");
			}
			if (linkedInvoice.CustomerId != Guid.Empty && (customer == null || linkedInvoice.CustomerId != transaction.CustomerId))
			{
				var linkedInvoiceCustomer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == linkedInvoice.CustomerId, ct);
				if (linkedInvoiceCustomer == null)
				{
					return OfficeMutationResult.Missing("연결 전표의 거래처를 찾을 수 없습니다.");
				}
				if (!CanWriteCustomerScope(session, linkedInvoiceCustomer.ResponsibleOfficeCode, linkedInvoiceCustomer.TenantCode, linkedInvoiceCustomer.OfficeCode))
				{
					return OfficeMutationResult.Denied("권한이 없어 연결 전표 거래처의 수금/지급을 저장할 수 없습니다.");
				}
				transaction.CustomerId = linkedInvoice.CustomerId;
				customer = linkedInvoiceCustomer;
				customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
			}
			transaction.LinkedInvoiceNumber = (string.IsNullOrWhiteSpace(linkedInvoice.InvoiceNumber) ? linkedInvoice.LocalTempNumber : linkedInvoice.InvoiceNumber);
			VoucherType voucherType = linkedInvoice.VoucherType;
			bool flag = (uint)(voucherType - 1) <= 1u;
			bool preferInvoicePayment = flag;
			transaction.TransactionKind = NormalizeLinkedInvoiceTransactionKind(transaction.TransactionKind, preferInvoicePayment);
			if (linkedInvoice.LinkedRentalBillingProfileId.HasValue &&
			    linkedInvoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
			{
				if (transaction.LinkedRentalBillingProfileId != linkedInvoice.LinkedRentalBillingProfileId)
				{
					transaction.LinkedRentalBillingProfileId = linkedInvoice.LinkedRentalBillingProfileId;
				}
				if (transaction.LinkedRentalBillingRunId != linkedInvoice.LinkedRentalBillingRunId)
				{
					transaction.LinkedRentalBillingRunId = linkedInvoice.LinkedRentalBillingRunId;
				}
			}
		}
		else
		{
			transaction.LinkedInvoiceId = null;
			transaction.LinkedInvoiceNumber = string.Empty;
		}
		LocalRentalBillingProfile? linkedRentalProfile = null;
		if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
		{
			linkedRentalProfile = await _db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefaultAsync((LocalRentalBillingProfile current) => current.Id == transaction.LinkedRentalBillingProfileId.Value, ct);
			if (linkedRentalProfile == null)
			{
				return OfficeMutationResult.Missing("연결할 렌탈 청구 대상을 찾을 수 없습니다.");
			}
			if (!CanWriteRentalEntityScope(session, linkedRentalProfile.TenantCode, linkedRentalProfile.ResponsibleOfficeCode, linkedRentalProfile.ManagementCompanyCode))
			{
				return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구 결제를 처리할 수 없습니다.");
			}
			if (!PaymentFlowConstants.IsRentalSettlementKind(transaction.TransactionKind))
			{
				transaction.TransactionKind = "렌탈수금";
			}
			if (linkedInvoice == null)
			{
				linkedInvoice = await FindTrackedSalesInvoiceForRentalBillingAsync(
					linkedRentalProfile.Id,
					transaction.LinkedRentalBillingRunId,
					ct);
				if (linkedInvoice != null)
				{
					if (!CanWriteOperationalScope(session, linkedInvoice.TenantCode, linkedInvoice.ResponsibleOfficeCode, linkedInvoice.OfficeCode))
					{
						return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구 전표 결제를 처리할 수 없습니다.");
					}

					transaction.LinkedInvoiceId = linkedInvoice.Id;
					transaction.LinkedInvoiceNumber = string.IsNullOrWhiteSpace(linkedInvoice.InvoiceNumber)
						? linkedInvoice.LocalTempNumber
						: linkedInvoice.InvoiceNumber;
					if (!transaction.LinkedRentalBillingRunId.HasValue ||
					    transaction.LinkedRentalBillingRunId.Value == Guid.Empty)
					{
						transaction.LinkedRentalBillingRunId = linkedInvoice.LinkedRentalBillingRunId;
					}
				}
			}
		}
		else
		{
			transaction.LinkedRentalBillingProfileId = null;
			transaction.LinkedRentalBillingRunId = null;
		}
		if (customer == null)
		{
			return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");
		}
		if (string.IsNullOrWhiteSpace(customerOfficeCode))
		{
			customerOfficeCode = ResolveResponsibleOfficeScopeForAccess(customer.ResponsibleOfficeCode, customer.OfficeCode);
		}
		string derivedResponsibleOfficeCode = linkedInvoice?.ResponsibleOfficeCode ?? linkedRentalProfile?.ResponsibleOfficeCode ?? linkedRentalProfile?.ManagementCompanyCode ?? customerOfficeCode ?? existing?.ResponsibleOfficeCode ?? transaction.ResponsibleOfficeCode;
		transaction.ResponsibleOfficeCode = NormalizeOfficeScope(derivedResponsibleOfficeCode, customerOfficeCode);
		string derivedOwnerOfficeCode = linkedInvoice?.OfficeCode ?? linkedRentalProfile?.OfficeCode ?? customer.OfficeCode ?? existing?.OfficeCode ?? transaction.OfficeCode;
		transaction.OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(derivedOwnerOfficeCode, transaction.ResponsibleOfficeCode, customer.OfficeCode);
		transaction.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
			transaction.TenantCode,
			transaction.OfficeCode,
			customer.TenantCode,
			transaction.ResponsibleOfficeCode);
		if (!CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");
		}
		decimal receiptTotal = Math.Max(0m, transaction.ReceiptTotal);
		decimal paymentTotal = Math.Max(0m, transaction.PaymentTotal);
		decimal absoluteAmount = ((receiptTotal > 0m) ? receiptTotal : paymentTotal);
		transaction.SettlementAmount = Math.Max(0m, transaction.SettlementAmount);
		transaction.AdvanceDelta = 0m;
		transaction.PrepaidDelta = 0m;
		decimal num = ((linkedInvoice != null) ? (await GetInvoiceRemainingAmountForTransactionAsync(linkedInvoice.Id, transaction.Id, linkedInvoice.TotalAmount, ct)) : default(decimal));
		decimal linkedInvoiceRemainingAmount = num;
		string transactionKind = transaction.TransactionKind;
		string kind = transactionKind;
		if (kind == "선수금입금")
		{
			transaction.LinkedInvoiceId = null;
			transaction.LinkedInvoiceNumber = string.Empty;
			transaction.LinkedRentalBillingProfileId = null;
			transaction.LinkedRentalBillingRunId = null;
			transaction.SettlementAmount = 0m;
			transaction.AdvanceDelta = receiptTotal;
		}
		else
		{
			string kind2 = kind;
			if (kind2 == "선수금환불")
			{
				transaction.LinkedInvoiceId = null;
				transaction.LinkedInvoiceNumber = string.Empty;
				transaction.LinkedRentalBillingProfileId = null;
				transaction.LinkedRentalBillingRunId = null;
				transaction.SettlementAmount = 0m;
				transaction.AdvanceDelta = -paymentTotal;
			}
			else
			{
				string kind3 = kind;
				if (kind3 == "선수금차감")
				{
					if (linkedInvoice == null)
					{
						return OfficeMutationResult.Denied("선수금 차감은 연결 전표가 있어야 합니다.");
					}
					transaction.SettlementAmount = ((transaction.SettlementAmount > 0m) ? transaction.SettlementAmount : absoluteAmount);
					if (transaction.SettlementAmount <= 0m)
					{
						return OfficeMutationResult.Denied("선수금 차감 금액을 입력하세요.");
					}
					transaction.SettlementAmount = Math.Min(transaction.SettlementAmount, linkedInvoiceRemainingAmount);
					transaction.AdvanceDelta = -transaction.SettlementAmount;
				}
				else
				{
					string kind4 = kind;
					if (kind4 == "전표수금" || kind4 == "일반수금")
					{
						if (linkedInvoice != null)
						{
							transaction.SettlementAmount = ((transaction.SettlementAmount > 0m) ? Math.Min(transaction.SettlementAmount, (receiptTotal <= 0m) ? transaction.SettlementAmount : receiptTotal) : receiptTotal);
							if (transaction.SettlementAmount <= 0m)
							{
								return OfficeMutationResult.Denied("전표 수금 금액을 입력하세요.");
							}
							transaction.SettlementAmount = Math.Min(transaction.SettlementAmount, linkedInvoiceRemainingAmount);
							if (receiptTotal > transaction.SettlementAmount)
							{
								transaction.AdvanceDelta = receiptTotal - transaction.SettlementAmount;
							}
						}
						else
						{
							transaction.LinkedInvoiceId = null;
							transaction.LinkedInvoiceNumber = string.Empty;
							transaction.LinkedRentalBillingProfileId = null;
							transaction.LinkedRentalBillingRunId = null;
							transaction.SettlementAmount = 0m;
						}
					}
					else
					{
						string kind5 = kind;
						if (kind5 == "전표지급" || kind5 == "일반지급")
						{
							if (linkedInvoice != null)
							{
								transaction.SettlementAmount = ((transaction.SettlementAmount > 0m) ? Math.Min(transaction.SettlementAmount, (paymentTotal <= 0m) ? transaction.SettlementAmount : paymentTotal) : paymentTotal);
								if (transaction.SettlementAmount <= 0m)
								{
									return OfficeMutationResult.Denied("전표 지급 금액을 입력하세요.");
								}
								transaction.SettlementAmount = Math.Min(transaction.SettlementAmount, linkedInvoiceRemainingAmount);
								if (paymentTotal > transaction.SettlementAmount)
								{
									transaction.PrepaidDelta = paymentTotal - transaction.SettlementAmount;
								}
							}
							else
							{
								transaction.LinkedInvoiceId = null;
								transaction.LinkedInvoiceNumber = string.Empty;
								transaction.LinkedRentalBillingProfileId = null;
								transaction.LinkedRentalBillingRunId = null;
								transaction.SettlementAmount = 0m;
							}
						}
						else
						{
							string kind6 = kind;
							if (kind6 == "렌탈수금")
							{
								if (linkedRentalProfile == null)
								{
									return OfficeMutationResult.Denied("렌탈 수금은 연결 청구건이 있어야 합니다.");
								}
								transaction.SettlementAmount = ((transaction.SettlementAmount > 0m) ? Math.Min(transaction.SettlementAmount, (receiptTotal <= 0m) ? transaction.SettlementAmount : receiptTotal) : receiptTotal);
								if (transaction.SettlementAmount <= 0m)
								{
									return OfficeMutationResult.Denied("렌탈 수금 금액을 입력하세요.");
								}
							}
							else
							{
								transaction.LinkedInvoiceId = null;
								transaction.LinkedInvoiceNumber = string.Empty;
								transaction.LinkedRentalBillingProfileId = null;
								transaction.LinkedRentalBillingRunId = null;
								transaction.SettlementAmount = 0m;
							}
						}
					}
				}
			}
		}
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		if (existing != null && !CanWriteOperationalScope(session, existing.TenantCode, existing.ResponsibleOfficeCode, existing.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");
		}
		previousLinkedRentalId = existing?.LinkedRentalBillingProfileId;
		previousLinkedRentalRunId = existing?.LinkedRentalBillingRunId;
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, transaction, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(now: DateTime.UtcNow, candidate: transaction, existing: existing, entityDisplayName: "수금/지급 내역", conflictMessage: out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (existing == null)
		{
			_db.Transactions.Add(transaction);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(transaction);
		}
		await _db.SaveChangesAsync(ct);
		if (linkedInvoice == null)
		{
			await RemoveLinkedInvoicePaymentAsync(transaction.Id, ct);
		}
		else
		{
			await SyncInvoicePaymentFromTransactionAsync(transaction, linkedInvoice, ct);
		}
		if (linkedRentalProfile != null)
		{
			await RecalculateRentalSettlementAsync(linkedRentalProfile.Id, transaction.LinkedRentalBillingRunId, ct);
		}
		if (previousLinkedRentalId.HasValue && previousLinkedRentalId.Value != Guid.Empty && (previousLinkedRentalId != transaction.LinkedRentalBillingProfileId || previousLinkedRentalRunId != transaction.LinkedRentalBillingRunId))
		{
			await RecalculateRentalSettlementAsync(previousLinkedRentalId.Value, previousLinkedRentalRunId, ct);
		}
		if (!string.Equals(requestedTransactionKind, transaction.TransactionKind, StringComparison.OrdinalIgnoreCase))
		{
			warnings.Add($"처리구분을 '{requestedTransactionKind}'에서 '{transaction.TransactionKind}'(으)로 보정했습니다.");
		}
		if (!string.Equals((requestedResponsibleOfficeCode ?? string.Empty).Trim(), (transaction.ResponsibleOfficeCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
		{
			warnings.Add($"담당지점을 '{requestedResponsibleOfficeCode}'에서 '{transaction.ResponsibleOfficeCode}'(으)로 보정했습니다.");
		}
		if (requestedLinkedInvoiceId != transaction.LinkedInvoiceId)
		{
			warnings.Add("연결 전표 값을 저장 규칙에 맞게 다시 정리했습니다.");
		}
		if (requestedCustomerId != transaction.CustomerId)
		{
			warnings.Add("거래처를 연결 전표 기준으로 다시 맞췄습니다.");
		}
		if (requestedLinkedRentalProfileId != transaction.LinkedRentalBillingProfileId || requestedLinkedRentalRunId != transaction.LinkedRentalBillingRunId)
		{
			warnings.Add("연결 렌탈 청구 값을 저장 규칙에 맞게 다시 정리했습니다.");
		}
		if (requestedSettlementAmount != transaction.SettlementAmount)
		{
			warnings.Add($"수금/지급 금액을 {requestedSettlementAmount:N0}원에서 {transaction.SettlementAmount:N0}원으로 조정했습니다.");
		}
		return OfficeMutationResult.Ok(transaction.Id, "수금/지급을 저장했습니다.", grantedTemporaryAccess: false, warnings);
	}

	public async Task<OfficeMutationResult> DeleteTransactionAsync(Guid transactionId, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return OfficeMutationResult.Denied("권한이 없어 수금/지급 내역을 삭제할 수 없습니다.");
		}
		var transaction = await _db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransaction current) => current.Id == transactionId, ct);
		transaction = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, transaction, ct);
		if (transaction == null)
		{
			return OfficeMutationResult.Missing("수금/지급 내역을 찾을 수 없습니다.");
		}
		var transactionCustomer = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalCustomer current) => current.Id == transaction.CustomerId, ct);
		string transactionFallbackOfficeCode = ResolveResponsibleOfficeScopeForAccess(transactionCustomer?.ResponsibleOfficeCode, transactionCustomer?.OfficeCode ?? transaction.OfficeCode);
		if (!CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transactionFallbackOfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 내역을 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(transaction, expectedRevision, "수금/지급 내역", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		List<LocalTransactionAttachment> attachments = await (from current in _db.TransactionAttachments.IgnoreQueryFilters()
			where current.TransactionId == transactionId
			select current).ToListAsync(ct);
		if (!session.HasAdministrativePrivileges && attachments.Any((LocalTransactionAttachment current) => !current.IsDeleted && string.Equals(current.VerificationStatus, "확인완료", StringComparison.OrdinalIgnoreCase)))
		{
			return OfficeMutationResult.Denied("확인완료된 증빙이 있는 수금/지급 내역은 관리자만 삭제할 수 있습니다.");
		}
		Guid? previousLinkedRentalId = transaction.LinkedRentalBillingProfileId;
		Guid? previousLinkedRentalRunId = transaction.LinkedRentalBillingRunId;
		DateTime now = DateTime.UtcNow;
		transaction.IsDeleted = true;
		transaction.IsDirty = true;
		transaction.UpdatedAtUtc = now;
		foreach (LocalTransactionAttachment attachment in attachments)
		{
			attachment.IsDeleted = true;
			attachment.IsDirty = true;
			attachment.UpdatedAtUtc = now;
		}
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalTransaction",
			EntityId = transaction.Id.ToString("D"),
			Action = "Delete",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = JsonSerializer.Serialize(new
			{
				transaction.CustomerId, transaction.TransactionDate, transaction.TransactionKind, transaction.LinkedInvoiceId, transaction.LinkedRentalBillingProfileId, transaction.LinkedRentalBillingRunId, transaction.ReceiptTotal, transaction.PaymentTotal, transaction.SettlementAmount, transaction.Note,
				transaction.Memo
			}, AuditJsonOptions),
			AfterJson = string.Empty,
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RemoveLinkedInvoicePaymentAsync(transactionId, ct);
		if (previousLinkedRentalId.HasValue && previousLinkedRentalId.Value != Guid.Empty)
		{
			await RecalculateRentalSettlementAsync(previousLinkedRentalId.Value, previousLinkedRentalRunId, ct);
		}
		return OfficeMutationResult.Ok(transactionId, "수금/지급 내역을 삭제했습니다.");
	}

	private static string NormalizeLinkedInvoiceTransactionKind(string? transactionKind, bool preferPayment)
	{
		string text = PaymentFlowConstants.NormalizeTransactionKind(transactionKind, preferPayment);
		string result;
		if (preferPayment)
		{
			if (1 == 0)
			{
			}
			result = text switch
			{
				"일반수금" => "일반지급",
				"전표수금" => "전표지급",
				"선수금차감" => "일반지급",
				_ => text,
			};
			if (1 == 0)
			{
			}
			return result;
		}
		if (1 == 0)
		{
		}
		result = ((text == "일반지급") ? "일반수금" : ((!(text == "전표지급")) ? text : "전표수금"));
		if (1 == 0)
		{
		}
		return result;
	}

	private async Task<decimal> GetInvoiceRemainingAmountForTransactionAsync(Guid invoiceId, Guid transactionId, decimal invoiceTotal, CancellationToken ct)
	{
		return Math.Max(0m, invoiceTotal - (await (from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
			where !payment.IsDeleted && payment.InvoiceId == invoiceId && payment.Id != transactionId
			select payment.Amount).ToListAsync(ct)).Sum());
	}

	private async Task<decimal> GetAdvanceBalanceCoreAsync(Guid customerId, CancellationToken ct)
	{
		return (await (from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
			where !transaction.IsDeleted && transaction.CustomerId == customerId
			select transaction.AdvanceDelta).ToListAsync(ct)).Sum();
	}

	private async Task<decimal> GetInvoiceSettledAmountCoreAsync(Guid invoiceId, CancellationToken ct)
	{
		return (await (from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
			where !payment.IsDeleted && payment.InvoiceId == invoiceId
			select payment.Amount).ToListAsync(ct)).Sum();
	}

	private async Task<decimal> GetRentalSettledAmountCoreAsync(Guid billingProfileId, Guid? billingRunId, CancellationToken ct)
	{
		IQueryable<LocalTransaction> transactionQuery = from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
			where !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId
			select transaction;
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			transactionQuery = transactionQuery.Where((LocalTransaction transaction) => transaction.LinkedRentalBillingRunId == ((Guid?)billingRunId).Value);
		}
		var transactionSettledAmount = (await transactionQuery
			.Select((LocalTransaction transaction) => transaction.SettlementAmount)
			.ToListAsync(ct)).Sum();

		var directPaymentQuery =
			from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
			join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
				on payment.InvoiceId equals invoice.Id
			where !payment.IsDeleted &&
			      !invoice.IsDeleted &&
			      invoice.IsLatestVersion &&
			      invoice.LinkedRentalBillingProfileId == billingProfileId &&
			      !_db.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
				      !transaction.IsDeleted &&
				      transaction.Id == payment.Id &&
				      transaction.LinkedRentalBillingProfileId == billingProfileId)
			select new
			{
				payment.Amount,
				invoice.LinkedRentalBillingRunId
			};
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);
		}

		var directPaymentSettledAmount = (await directPaymentQuery
			.Select(row => row.Amount)
			.ToListAsync(ct)).Sum();

		return transactionSettledAmount + directPaymentSettledAmount;
	}

	private async Task<DateOnly?> GetRentalLastSettledDateCoreAsync(Guid billingProfileId, Guid? billingRunId, CancellationToken ct)
	{
		IQueryable<LocalTransaction> transactionQuery = from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
			where !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId
			select transaction;
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			transactionQuery = transactionQuery.Where((LocalTransaction transaction) => transaction.LinkedRentalBillingRunId == ((Guid?)billingRunId).Value);
		}

		var transactionDates = await transactionQuery
			.Select((LocalTransaction transaction) => transaction.TransactionDate)
			.ToListAsync(ct);

		var directPaymentQuery =
			from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
			join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
				on payment.InvoiceId equals invoice.Id
			where !payment.IsDeleted &&
			      !invoice.IsDeleted &&
			      invoice.IsLatestVersion &&
			      invoice.LinkedRentalBillingProfileId == billingProfileId &&
			      !_db.Transactions.IgnoreQueryFilters().AsNoTracking().Any(transaction =>
				      !transaction.IsDeleted &&
				      transaction.Id == payment.Id &&
				      transaction.LinkedRentalBillingProfileId == billingProfileId)
			select new
			{
				payment.PaymentDate,
				invoice.LinkedRentalBillingRunId
			};
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			directPaymentQuery = directPaymentQuery.Where(row => row.LinkedRentalBillingRunId == billingRunId.Value);
		}

		var directPaymentDates = await directPaymentQuery
			.Select(row => row.PaymentDate)
			.ToListAsync(ct);
		var dates = transactionDates.Concat(directPaymentDates).ToList();
		return dates.Count == 0 ? null : dates.Max();
	}

	private async Task<LocalInvoice?> FindTrackedSalesInvoiceForRentalBillingAsync(Guid billingProfileId, Guid? billingRunId, CancellationToken ct)
	{
		if (billingProfileId == Guid.Empty)
		{
			return null;
		}

		IQueryable<LocalInvoice> query = _db.Invoices.IgnoreQueryFilters()
			.Where((LocalInvoice invoice) =>
				!invoice.IsDeleted &&
				invoice.IsLatestVersion &&
				invoice.VoucherType == VoucherType.Sales &&
				invoice.LinkedRentalBillingProfileId == billingProfileId);
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			query = query.Where((LocalInvoice invoice) => invoice.LinkedRentalBillingRunId == billingRunId.Value);
		}

		return await query
			.OrderByDescending((LocalInvoice invoice) => invoice.InvoiceDate)
			.ThenByDescending((LocalInvoice invoice) => invoice.UpdatedAtUtc)
			.ThenByDescending((LocalInvoice invoice) => invoice.CreatedAtUtc)
			.FirstOrDefaultAsync(ct);
	}

	private async Task SyncInvoicePaymentFromTransactionAsync(LocalTransaction transaction, LocalInvoice invoice, CancellationToken ct, bool markDirty = true)
	{
		decimal amount = Math.Max(0m, transaction.SettlementAmount);
		var payment = await _db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync((LocalPayment current) => current.Id == transaction.Id, ct);
		if (amount <= 0m)
		{
			if (payment != null)
			{
				payment.IsDeleted = true;
				payment.IsDirty = markDirty;
				payment.UpdatedAtUtc = markDirty ? DateTime.UtcNow : transaction.UpdatedAtUtc;
				if (!markDirty)
				{
					payment.Revision = Math.Max(payment.Revision, transaction.Revision);
				}
				await _db.SaveChangesAsync(ct);
			}
			return;
		}
		string transactionKindLabel = PaymentFlowConstants.GetTransactionKindDisplayName(transaction.TransactionKind);
		string note = (string.IsNullOrWhiteSpace(transaction.Note) ? transactionKindLabel : (transactionKindLabel + " - " + transaction.Note.Trim()));
		if (payment == null)
		{
			_db.Payments.Add(new LocalPayment
			{
				Id = transaction.Id,
				InvoiceId = invoice.Id,
				PaymentDate = transaction.TransactionDate,
				Amount = amount,
				Note = note,
				CreatedAtUtc = markDirty ? DateTime.UtcNow : transaction.CreatedAtUtc,
				UpdatedAtUtc = markDirty ? DateTime.UtcNow : transaction.UpdatedAtUtc,
				Revision = markDirty ? 0 : transaction.Revision,
				IsDirty = markDirty
			});
		}
		else
		{
			payment.InvoiceId = invoice.Id;
			payment.PaymentDate = transaction.TransactionDate;
			payment.Amount = amount;
			payment.Note = note;
			payment.IsDeleted = false;
			payment.IsDirty = markDirty;
			payment.UpdatedAtUtc = markDirty ? DateTime.UtcNow : transaction.UpdatedAtUtc;
			if (!markDirty)
			{
				payment.Revision = Math.Max(payment.Revision, transaction.Revision);
			}
		}
		await _db.SaveChangesAsync(ct);
	}

	private async Task RemoveLinkedInvoicePaymentAsync(Guid transactionId, CancellationToken ct, bool markDirty = true, DateTime? updatedAtUtc = null, long? revision = null)
	{
		var payment = await _db.Payments.IgnoreQueryFilters().FirstOrDefaultAsync((LocalPayment current) => current.Id == transactionId, ct);
		if (payment != null)
		{
			payment.IsDeleted = true;
			payment.IsDirty = markDirty;
			payment.UpdatedAtUtc = markDirty ? DateTime.UtcNow : (updatedAtUtc ?? payment.UpdatedAtUtc);
			if (!markDirty && revision.HasValue)
			{
				payment.Revision = Math.Max(payment.Revision, revision.Value);
			}
			await _db.SaveChangesAsync(ct);
		}
	}

	private async Task DetachTransactionsFromInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, DateTime updatedAtUtc, CancellationToken ct, bool markDirty = true, long? revision = null)
	{
		if (invoiceIds == null || invoiceIds.Count == 0)
		{
			return;
		}
		foreach (LocalTransaction transaction in await (from localTransaction in _db.Transactions.IgnoreQueryFilters()
			where localTransaction.LinkedInvoiceId.HasValue && invoiceIds.Contains(localTransaction.LinkedInvoiceId.Value)
			select localTransaction).ToListAsync(ct))
		{
			if (!markDirty && transaction.IsDirty)
			{
				continue;
			}

			transaction.LinkedInvoiceId = null;
			transaction.LinkedInvoiceNumber = string.Empty;
			transaction.LinkedRentalBillingProfileId = null;
			transaction.LinkedRentalBillingRunId = null;
			transaction.SettlementAmount = 0m;
			if (string.Equals(transaction.TransactionKind, PaymentFlowConstants.TransactionKindInvoiceReceipt, StringComparison.OrdinalIgnoreCase))
			{
				transaction.TransactionKind = PaymentFlowConstants.TransactionKindReceipt;
			}
			else if (string.Equals(transaction.TransactionKind, PaymentFlowConstants.TransactionKindInvoicePayment, StringComparison.OrdinalIgnoreCase))
			{
				transaction.TransactionKind = PaymentFlowConstants.TransactionKindPayment;
			}
			else if (string.Equals(transaction.TransactionKind, PaymentFlowConstants.TransactionKindRentalReceipt, StringComparison.OrdinalIgnoreCase))
			{
				transaction.TransactionKind = PaymentFlowConstants.TransactionKindReceipt;
			}
			transaction.IsDirty = markDirty;
			transaction.UpdatedAtUtc = updatedAtUtc;
			if (!markDirty && revision.HasValue)
			{
				transaction.Revision = Math.Max(transaction.Revision, revision.Value);
			}
		}
	}

	private async Task<List<(Guid ProfileId, Guid? RunId)>> LoadRentalSettlementTargetsForInvoiceDeleteAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct)
	{
		if (invoiceIds == null || invoiceIds.Count == 0)
		{
			return new List<(Guid ProfileId, Guid? RunId)>();
		}

		var targets = new List<(Guid ProfileId, Guid? RunId)>();
		foreach (var batchIds in invoiceIds.Where(id => id != Guid.Empty).Distinct().Chunk(LocalQueryContainsBatchSize))
		{
			ct.ThrowIfCancellationRequested();
			var scopedBatchIds = batchIds;
			var invoiceTargets = await (from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
				where scopedBatchIds.Contains(invoice.Id) &&
				      invoice.LinkedRentalBillingProfileId.HasValue &&
				      invoice.LinkedRentalBillingProfileId.Value != Guid.Empty
				select new
				{
					ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
					RunId = invoice.LinkedRentalBillingRunId
				}).ToListAsync(ct);
			targets.AddRange(invoiceTargets.Select(target => (target.ProfileId, target.RunId)));

			var transactionTargets = await (from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
				where !transaction.IsDeleted &&
				      transaction.LinkedInvoiceId.HasValue &&
				      scopedBatchIds.Contains(transaction.LinkedInvoiceId.Value) &&
				      transaction.LinkedRentalBillingProfileId.HasValue &&
				      transaction.LinkedRentalBillingProfileId.Value != Guid.Empty
				select new
				{
					ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
					RunId = transaction.LinkedRentalBillingRunId
				}).ToListAsync(ct);
			targets.AddRange(transactionTargets.Select(target => (target.ProfileId, target.RunId)));
		}

		return targets
			.Where(target => target.ProfileId != Guid.Empty)
			.Distinct()
			.ToList();
	}

	private async Task MarkPaymentsDeletedForInvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, DateTime updatedAtUtc, CancellationToken ct, bool markDirty = true, long? revision = null)
	{
		if (invoiceIds == null || invoiceIds.Count == 0)
		{
			return;
		}
		foreach (LocalPayment payment in await (from localPayment in _db.Payments.IgnoreQueryFilters()
			where invoiceIds.Contains(localPayment.InvoiceId)
			select localPayment).ToListAsync(ct))
		{
			if (!markDirty && payment.IsDirty)
			{
				continue;
			}

			payment.IsDeleted = true;
			payment.IsDirty = markDirty;
			payment.UpdatedAtUtc = updatedAtUtc;
			if (!markDirty && revision.HasValue)
			{
				payment.Revision = Math.Max(payment.Revision, revision.Value);
			}
		}
	}

	private async Task<bool> HasActivePaymentSideEffectsForInvoiceDeleteAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct)
	{
		if (invoiceIds == null || invoiceIds.Count == 0)
		{
			return false;
		}

		foreach (var batchIds in invoiceIds.Where(id => id != Guid.Empty).Distinct().Chunk(LocalQueryContainsBatchSize))
		{
			ct.ThrowIfCancellationRequested();
			var scopedBatchIds = batchIds;
			var hasActivePayment = await _db.Payments.IgnoreQueryFilters()
				.AsNoTracking()
				.AnyAsync(payment => !payment.IsDeleted && scopedBatchIds.Contains(payment.InvoiceId), ct);
			if (hasActivePayment)
			{
				return true;
			}

			var hasActiveTransaction = await _db.Transactions.IgnoreQueryFilters()
				.AsNoTracking()
				.AnyAsync(transaction =>
					!transaction.IsDeleted &&
					transaction.LinkedInvoiceId.HasValue &&
					scopedBatchIds.Contains(transaction.LinkedInvoiceId.Value), ct);
			if (hasActiveTransaction)
			{
				return true;
			}
		}

		return false;
	}

	private async Task RecalculateRentalSettlementAsync(Guid billingProfileId, CancellationToken ct, bool markDirty = true)
	{
		await RecalculateRentalSettlementAsync(billingProfileId, null, ct, markDirty);
	}

	private async Task RecalculateRentalSettlementAsync(Guid billingProfileId, Guid? billingRunId, CancellationToken ct, bool markDirty = true)
	{
		var profile = await _db.RentalBillingProfiles.IgnoreQueryFilters().FirstOrDefaultAsync((LocalRentalBillingProfile current) => current.Id == billingProfileId, ct);
		if (profile == null)
		{
			return;
		}
		decimal settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, billingRunId, ct);
		decimal billedAmount = await ResolveBillingRunAmountAsync(profile, billingRunId, null, ct);
		profile.SettledAmount = settledAmount;
		profile.OutstandingAmount = Math.Max(0m, billedAmount - settledAmount);
		profile.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
		profile.CompletionStatus = ((profile.OutstandingAmount <= 0m) ? "완료" : "미완료");
		if (billingRunId.HasValue && billingRunId.Value != Guid.Empty)
		{
			List<RentalBillingRunModel> runs = DeserializeBillingRuns(profile.BillingRunsJson);
			var run = runs.FirstOrDefault((RentalBillingRunModel current) => current.RunId == billingRunId.Value);
			if (run != null)
			{
				run.BilledAmount = billedAmount;
				run.SettledAmount = settledAmount;
				run.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
				run.Status = ((profile.OutstandingAmount <= 0m) ? "완료" : (string.Equals(run.Status, "보류", StringComparison.OrdinalIgnoreCase) ? "보류" : "청구중"));
				RentalBillingRunModel rentalBillingRunModel = run;
				DateOnly? settledDate = settledAmount > 0m
					? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, ct)
					: null;
				rentalBillingRunModel.SettledDate = settledDate;
				if (profile.OutstandingAmount <= 0m)
				{
					profile.LastBilledDate = run.ScheduledDate;
				}
				profile.BillingRunsJson = JsonSerializer.Serialize(runs, AuditJsonOptions);
			}
		}
		if (profile.CompletionStatus == "완료")
		{
			profile.BillingStatus = "완료";
			LocalRentalBillingProfile localRentalBillingProfile = profile;
			localRentalBillingProfile.LastSettledDate = await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, ct);
		}
		else if (!string.Equals(profile.BillingStatus, "보류", StringComparison.OrdinalIgnoreCase) && !string.Equals(profile.BillingStatus, "취소", StringComparison.OrdinalIgnoreCase))
		{
			profile.BillingStatus = "청구중";
			LocalRentalBillingProfile localRentalBillingProfile2 = profile;
			DateOnly? lastSettledDate = settledAmount > 0m
				? await GetRentalLastSettledDateCoreAsync(billingProfileId, billingRunId, ct)
				: null;
			localRentalBillingProfile2.LastSettledDate = lastSettledDate;
		}
		if (markDirty)
		{
			profile.IsDirty = true;
			profile.UpdatedAtUtc = DateTime.UtcNow;
		}
		await _db.SaveChangesAsync(ct);
	}

	private async Task<decimal> ResolveBillingRunAmountAsync(LocalRentalBillingProfile profile, Guid? billingRunId, decimal? billedAmountOverride, CancellationToken ct)
	{
		if (billedAmountOverride.HasValue && billedAmountOverride.Value > 0m)
		{
			return billedAmountOverride.Value;
		}
		if (!billingRunId.HasValue || billingRunId.Value == Guid.Empty)
		{
			return Math.Max(0m, profile.MonthlyAmount);
		}
		var activeInvoiceAmount = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
			.Where((LocalInvoice invoice) => !invoice.IsDeleted &&
			                                invoice.IsLatestVersion &&
			                                invoice.LinkedRentalBillingProfileId == profile.Id &&
			                                invoice.LinkedRentalBillingRunId == billingRunId.Value)
			.OrderByDescending((LocalInvoice invoice) => invoice.LastSavedAtUtc)
			.ThenByDescending((LocalInvoice invoice) => invoice.UpdatedAtUtc)
			.Select((LocalInvoice invoice) => (decimal?)invoice.TotalAmount)
			.FirstOrDefaultAsync(ct);
		if (activeInvoiceAmount.HasValue && activeInvoiceAmount.Value > 0m)
		{
			return activeInvoiceAmount.Value;
		}
		var rentalBillingRunModel = DeserializeBillingRuns(profile.BillingRunsJson).FirstOrDefault((RentalBillingRunModel current) => current.RunId == billingRunId.Value);
		return (rentalBillingRunModel == null) ? Math.Max(0m, profile.MonthlyAmount) : Math.Max(0m, rentalBillingRunModel.BilledAmount);
	}

	private static List<RentalBillingRunModel> DeserializeBillingRuns(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new List<RentalBillingRunModel>();
		}
		try
		{
			return JsonSerializer.Deserialize<List<RentalBillingRunModel>>(json, AuditJsonOptions) ?? new List<RentalBillingRunModel>();
		}
		catch
		{
			return new List<RentalBillingRunModel>();
		}
	}

	private static string DetermineRentalSettlementStatus(string? billingMethod, decimal settledAmount, decimal billedAmount)
	{
		if (settledAmount <= 0m)
		{
			return PaymentFlowConstants.GetPendingSettlementStatus(billingMethod);
		}
		if (settledAmount < billedAmount)
		{
			return "부분입금";
		}
		return PaymentFlowConstants.GetDisplaySettlementCompleteStatus(billingMethod);
	}

	private bool CanAccessRentalProfile(LocalRentalBillingProfile profile, SessionState session)
	{
		return CanReadRentalEntityScope(session, profile.TenantCode, profile.ResponsibleOfficeCode, profile.ManagementCompanyCode);
	}

	public async Task<List<LocalTransactionAttachment>> GetTransactionAttachmentsAsync(Guid transactionId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		var transaction = await _db.Transactions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync((LocalTransaction current) => current.Id == transactionId, ct);
		if (transaction == null || !CanAccessTransaction(transaction, session))
		{
			return new List<LocalTransactionAttachment>();
		}
		return await (from current in _db.TransactionAttachments.AsNoTracking()
			where current.TransactionId == transactionId
			orderby current.SortOrder, current.UploadedAtUtc descending
			select current).ToListAsync(ct);
	}

	public async Task<OfficeMutationResult> SaveTransactionAttachmentAsync(Guid transactionId, string sourceFilePath, string attachmentType, string? description, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return OfficeMutationResult.Denied("권한이 없어 수금/지급 증빙을 첨부할 수 없습니다.");
		}
		if (transactionId == Guid.Empty)
		{
			return OfficeMutationResult.Denied("증빙을 연결할 수금/지급 내역을 먼저 선택하세요.");
		}
		if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
		{
			return OfficeMutationResult.Missing("첨부할 파일을 찾을 수 없습니다.");
		}
		var transaction = await _db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransaction current) => current.Id == transactionId, ct);
		if (transaction == null)
		{
			return OfficeMutationResult.Missing("수금/지급 내역을 찾을 수 없습니다.");
		}
		if (!CanWriteOperationalScope(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 거래의 증빙을 저장할 수 없습니다.");
		}
		DateTime now = DateTime.UtcNow;
		string originalFileName = Path.GetFileName(sourceFilePath);
		string fileExtension = Path.GetExtension(sourceFilePath);
		string mimeType = ResolveMimeType(fileExtension);
		if (!EvidenceAttachmentFilePolicy.IsAllowedFileType(originalFileName, mimeType))
		{
			return OfficeMutationResult.Denied("증빙 파일은 PDF 또는 이미지 파일만 저장할 수 있습니다.");
		}
		var sourceFileInfo = new FileInfo(sourceFilePath);
		if (sourceFileInfo.Length <= 0)
		{
			return OfficeMutationResult.Denied("빈 증빙 파일은 저장할 수 없습니다.");
		}
		if (sourceFileInfo.Length > EvidenceAttachmentFilePolicy.MaxFileSizeBytes)
		{
			return OfficeMutationResult.Denied("증빙 파일은 15MB 이하만 저장할 수 있습니다. 스캔 해상도를 낮추거나 파일을 압축한 뒤 다시 시도하세요.");
		}
		var sourceBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
		if (!EvidenceAttachmentFilePolicy.ContentMatchesFileType(originalFileName, mimeType, sourceBytes))
		{
			return OfficeMutationResult.Denied("증빙 파일 내용이 PDF 또는 이미지 형식과 일치하지 않습니다.");
		}
		string attachmentDir = Path.Combine(AppPaths.TransactionAttachmentsDir, transactionId.ToString("N"));
		Directory.CreateDirectory(attachmentDir);
		string storedFileName = $"{now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{fileExtension}";
		string storedPath = Path.Combine(attachmentDir, storedFileName);
		await File.WriteAllBytesAsync(storedPath, sourceBytes, ct);
		var storedFileInfo = new FileInfo(storedPath);
		int sortOrder = await (from current in _db.TransactionAttachments.IgnoreQueryFilters()
			where current.TransactionId == transactionId
			select current).CountAsync(ct);
		LocalTransactionAttachment attachment = new LocalTransactionAttachment
		{
			Id = Guid.NewGuid(),
			TransactionId = transactionId,
			AttachmentType = NormalizeAttachmentType(attachmentType),
			FileName = originalFileName,
			StoredFileName = storedFileName,
			StoredPath = storedPath,
			MimeType = mimeType,
			FileSize = storedFileInfo.Length,
			FileHash = ComputeFileHash(storedPath),
			Description = (description ?? string.Empty).Trim(),
			UploadedByUsername = (session.User?.Username ?? "local-user"),
			UploadedAtUtc = now,
			VerificationStatus = "미확인",
			SortOrder = sortOrder,
			CreatedAtUtc = now,
			UpdatedAtUtc = now,
			IsDirty = true
		};
		_db.TransactionAttachments.Add(attachment);
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalTransactionAttachment",
			EntityId = attachment.Id.ToString("D"),
			Action = "Create",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new { attachment.TransactionId, attachment.AttachmentType, attachment.FileName, attachment.VerificationStatus }, AuditJsonOptions),
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(attachment.Id, "수금/지급 증빙을 첨부했습니다.");
	}

	public async Task<OfficeMutationResult> DeleteTransactionAttachmentAsync(Guid attachmentId, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditPayments(session))
		{
			return OfficeMutationResult.Denied("권한이 없어 수금/지급 증빙을 삭제할 수 없습니다.");
		}
		var attachment = await _db.TransactionAttachments.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransactionAttachment current) => current.Id == attachmentId, ct);
		attachment = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, attachment, ct);
		if (attachment == null)
		{
			return OfficeMutationResult.Missing("삭제할 증빙을 찾을 수 없습니다.");
		}
		var transaction = await _db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransaction current) => current.Id == attachment.TransactionId, ct);
		if (transaction == null)
		{
			return OfficeMutationResult.Missing("증빙과 연결된 수금/지급 내역을 찾을 수 없습니다.");
		}
		if (!CanAccessTransaction(transaction, session))
		{
			return OfficeMutationResult.Denied("권한이 없어 해당 증빙을 삭제할 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(attachment, expectedRevision, "수금/지급 증빙", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (string.Equals(attachment.VerificationStatus, "확인완료", StringComparison.OrdinalIgnoreCase) && !session.HasAdministrativePrivileges)
		{
			return OfficeMutationResult.Denied("확인완료된 증빙은 관리자만 삭제할 수 있습니다.");
		}
		attachment.IsDeleted = true;
		attachment.IsDirty = true;
		attachment.UpdatedAtUtc = DateTime.UtcNow;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalTransactionAttachment",
			EntityId = attachment.Id.ToString("D"),
			Action = "Delete",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = JsonSerializer.Serialize(new { attachment.TransactionId, attachment.AttachmentType, attachment.FileName, attachment.VerificationStatus }, AuditJsonOptions),
			AfterJson = string.Empty,
			CreatedAtUtc = DateTime.UtcNow
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(attachment.Id, "수금/지급 증빙을 삭제했습니다.");
	}

	public async Task<OfficeMutationResult> UpdateTransactionAttachmentVerificationAsync(Guid attachmentId, string verificationStatus, string? verificationMemo, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!session.HasAdministrativePrivileges)
		{
			return OfficeMutationResult.Denied("증빙 확인 상태는 관리자만 변경할 수 있습니다.");
		}
		var attachment = await _db.TransactionAttachments.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransactionAttachment current) => current.Id == attachmentId, ct);
		attachment = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, attachment, ct);
		if (attachment == null)
		{
			return OfficeMutationResult.Missing("증빙을 찾을 수 없습니다.");
		}
		if (await _db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync((LocalTransaction current) => current.Id == attachment.TransactionId, ct) == null)
		{
			return OfficeMutationResult.Missing("증빙과 연결된 수금/지급 내역을 찾을 수 없습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(attachment, expectedRevision, "수금/지급 증빙", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		string normalizedStatus = NormalizeAttachmentVerificationStatus(verificationStatus);
		string beforeJson = JsonSerializer.Serialize(new { attachment.VerificationStatus, attachment.VerifiedByUsername, attachment.VerifiedAtUtc, attachment.VerificationMemo }, AuditJsonOptions);
		attachment.VerificationStatus = normalizedStatus;
		attachment.VerificationMemo = (verificationMemo ?? string.Empty).Trim();
		attachment.VerifiedByUsername = session.User?.Username ?? "local-user";
		attachment.VerifiedAtUtc = DateTime.UtcNow;
		attachment.IsDirty = true;
		attachment.UpdatedAtUtc = attachment.VerifiedAtUtc.Value;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalTransactionAttachment",
			EntityId = attachment.Id.ToString("D"),
			Action = normalizedStatus,
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "admin"),
			OfficeCode = session.OfficeCode,
			BeforeJson = beforeJson,
			AfterJson = JsonSerializer.Serialize(new { attachment.VerificationStatus, attachment.VerifiedByUsername, attachment.VerifiedAtUtc, attachment.VerificationMemo }, AuditJsonOptions),
			CreatedAtUtc = attachment.VerifiedAtUtc.Value
		});
		await _db.SaveChangesAsync(ct);
		return OfficeMutationResult.Ok(attachment.Id, "증빙 상태를 " + normalizedStatus + "(으)로 변경했습니다.");
	}

	public Task<List<LocalInventoryTransfer>> GetInventoryTransfersAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default(CancellationToken))
	{
		IQueryable<LocalInventoryTransfer> source = _db.InventoryTransfers.Include((LocalInventoryTransfer transfer) => transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted)).AsNoTracking();
		if (from.HasValue)
		{
			source = source.Where((LocalInventoryTransfer transfer) => transfer.TransferDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			source = source.Where((LocalInventoryTransfer transfer) => transfer.TransferDate <= ((DateOnly?)to).Value);
		}
		return (from transfer in source
			orderby transfer.TransferDate descending, transfer.UpdatedAtUtc descending
			select transfer).ToListAsync(ct);
	}

	public Task<List<LocalInventoryTransfer>> GetInventoryTransfersAsync(SessionState session, DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default(CancellationToken))
	{
		List<string> tenantWarehouseCodes = GetTenantWarehouseCodes(session);
		List<string> readableWarehouseCodes = GetReadableOfficeCodes(session).Select(OfficeCodeCatalog.GetMainWarehouseCode).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		IQueryable<LocalInventoryTransfer> source = from transfer in _db.InventoryTransfers.Include((LocalInventoryTransfer transfer) => transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted)).AsNoTracking()
			where tenantWarehouseCodes.Contains(transfer.FromWarehouseCode) && tenantWarehouseCodes.Contains(transfer.ToWarehouseCode)
			select transfer;
		if (!HasFullAccess(session) && !CanViewAllDeliveryScope(session))
		{
			source = source.Where((LocalInventoryTransfer transfer) => readableWarehouseCodes.Contains(transfer.FromWarehouseCode) || readableWarehouseCodes.Contains(transfer.ToWarehouseCode));
		}
		if (from.HasValue)
		{
			source = source.Where((LocalInventoryTransfer transfer) => transfer.TransferDate >= ((DateOnly?)from).Value);
		}
		if (to.HasValue)
		{
			source = source.Where((LocalInventoryTransfer transfer) => transfer.TransferDate <= ((DateOnly?)to).Value);
		}
		return (from transfer in source
			orderby transfer.TransferDate descending, transfer.UpdatedAtUtc descending
			select transfer).ToListAsync(ct);
	}

	public Task<LocalInventoryTransfer?> GetInventoryTransferAsync(Guid transferId, CancellationToken ct = default(CancellationToken))
	{
		return _db.InventoryTransfers.Include((LocalInventoryTransfer transfer) => transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted)).AsNoTracking().FirstOrDefaultAsync((LocalInventoryTransfer transfer) => transfer.Id == transferId, ct);
	}

	public Task<LocalInventoryTransfer?> GetInventoryTransferAsync(Guid transferId, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		List<string> tenantWarehouseCodes = GetTenantWarehouseCodes(session);
		List<string> readableWarehouseCodes = GetReadableOfficeCodes(session).Select(OfficeCodeCatalog.GetMainWarehouseCode).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		IQueryable<LocalInventoryTransfer> source = from transfer in _db.InventoryTransfers.Include((LocalInventoryTransfer transfer) => transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted)).AsNoTracking()
			where transfer.Id == transferId && tenantWarehouseCodes.Contains(transfer.FromWarehouseCode) && tenantWarehouseCodes.Contains(transfer.ToWarehouseCode)
			select transfer;
		if (!HasFullAccess(session) && !CanViewAllDeliveryScope(session))
		{
			source = source.Where((LocalInventoryTransfer transfer) => readableWarehouseCodes.Contains(transfer.FromWarehouseCode) || readableWarehouseCodes.Contains(transfer.ToWarehouseCode));
		}
		return source.FirstOrDefaultAsync(ct);
	}

	public async Task<OfficeMutationResult> SaveInventoryTransferAsync(LocalInventoryTransfer transfer, SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (transfer == null)
		{
			throw new ArgumentNullException("transfer");
		}
		if (!CanEditDeliveries(session))
		{
			return OfficeMutationResult.Denied("납품/재고이동 편집 권한이 필요합니다.");
		}
		DateTime now = DateTime.UtcNow;
		string fromWarehouseCode = NormalizeWarehouseCode(transfer.FromWarehouseCode, session.OfficeCode, DomainConstants.OfficeUsenet);
		string toWarehouseCode = NormalizeWarehouseCode(transfer.ToWarehouseCode, session.OfficeCode, DomainConstants.OfficeYeonsu);
		string sourceOfficeCode = ResolveOfficeCodeFromWarehouseCode(fromWarehouseCode);
		string targetOfficeCode = ResolveOfficeCodeFromWarehouseCode(toWarehouseCode);
		string sourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, sourceOfficeCode);
		string targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, targetOfficeCode);
		if (string.Equals(fromWarehouseCode, toWarehouseCode, StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("출발창고와 도착창고는 서로 달라야 합니다.");
		}
		if (!string.Equals(sourceTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("재고이동은 같은 업체 내부 지점 간 이동만 지원합니다. 다른 업체로 보내려면 대상 업체에 품목을 먼저 등록/복제한 뒤 처리하세요.");
		}
		if (!CanWriteOfficeScope(session, sourceOfficeCode))
		{
			return OfficeMutationResult.Denied("출발지 담당자 또는 관리자만 재고이동을 저장할 수 있습니다.");
		}
		List<LocalInventoryTransferLine> validLines = (transfer.Lines ?? new List<LocalInventoryTransferLine>()).Where((LocalInventoryTransferLine localInventoryTransferLine) => !localInventoryTransferLine.IsDeleted && localInventoryTransferLine.ItemId.HasValue && !string.IsNullOrWhiteSpace(localInventoryTransferLine.ItemNameOriginal) && localInventoryTransferLine.Quantity > 0m).ToList();
		if (validLines.Count == 0)
		{
			return OfficeMutationResult.Denied("이동 품목을 1개 이상 입력하세요.");
		}
		var existing = await _db.InventoryTransfers.IgnoreQueryFilters().Include((LocalInventoryTransfer current) => current.Lines).FirstOrDefaultAsync((LocalInventoryTransfer current) => current.Id == transfer.Id, ct);
		existing = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, existing, ct);
		if (existing != null)
		{
			var linesEntry = _db.Entry(existing).Collection((LocalInventoryTransfer current) => current.Lines);
			linesEntry.IsLoaded = false;
			await linesEntry.LoadAsync(ct);
		}
		if (existing != null && IsFinalTransferStatus(existing.TransferStatus))
		{
			return OfficeMutationResult.Denied("이미 수령확정 또는 반려된 재고이동 문서는 수정할 수 없습니다.");
		}
		await LocalEntityConcurrencyGuard.TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync(_db, transfer, existing, ct);
		if (!LocalEntityConcurrencyGuard.TryPrepareForSave(transfer, existing, "재고이동 문서", now, out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		string transferStatus = string.IsNullOrWhiteSpace(existing?.TransferStatus) ? "수령대기" : existing.TransferStatus.Trim();
		Dictionary<Guid, string> itemTrackingMap = await BuildItemTrackingMapAsync(ct);
		var stockShortages = await FindTransferStockShortagesAsync(
			existing,
			fromWarehouseCode,
			toWarehouseCode,
			transferStatus,
			validLines,
			itemTrackingMap,
			ct);
		if (stockShortages.Count > 0)
		{
			return OfficeMutationResult.Denied(FormatStockShortageMessage("재고가 부족하여 재고이동 문서를 저장할 수 없습니다.", stockShortages));
		}
		Guid transferId = existing?.Id ?? ((transfer.Id == Guid.Empty) ? Guid.NewGuid() : transfer.Id);
		string text = ((!string.IsNullOrWhiteSpace(transfer.TransferNumber)) ? transfer.TransferNumber.Trim() : (await GenerateTransferNumberAsync(transfer.TransferDate, ct)));
		string transferNumber = text;
		LocalInventoryTransfer entity = existing ?? new LocalInventoryTransfer
		{
			Id = transferId,
			CreatedAtUtc = transfer.CreatedAtUtc
		};
		entity.TransferNumber = transferNumber;
		entity.TransferDate = transfer.TransferDate;
		entity.FromWarehouseCode = fromWarehouseCode;
		entity.ToWarehouseCode = toWarehouseCode;
		entity.Memo = transfer.Memo ?? string.Empty;
		entity.CreatedByUsername = ((!string.IsNullOrWhiteSpace(existing?.CreatedByUsername)) ? existing.CreatedByUsername : (session.User?.Username ?? "local-user"));
		entity.LastSavedByUsername = session.User?.Username ?? "local-user";
		entity.LastSavedAtUtc = now;
		entity.IsDeleted = false;
		entity.IsDirty = true;
		entity.UpdatedAtUtc = now;
		LocalInventoryTransfer localInventoryTransfer = entity;
		localInventoryTransfer.TransferStatus = transferStatus;
		entity.RequestedByUsername = ((!string.IsNullOrWhiteSpace(existing?.RequestedByUsername)) ? existing.RequestedByUsername : (session.User?.Username ?? "local-user"));
		entity.RequestedAtUtc = existing?.RequestedAtUtc ?? now;
		entity.LastStatusChangedByUsername = ((!string.IsNullOrWhiteSpace(existing?.LastStatusChangedByUsername)) ? existing.LastStatusChangedByUsername : (session.User?.Username ?? "local-user"));
		entity.LastStatusChangedAtUtc = existing?.LastStatusChangedAtUtc ?? now;
		entity.ReceivedByUsername = existing?.ReceivedByUsername ?? string.Empty;
		entity.ReceivedAtUtc = existing?.ReceivedAtUtc;
		entity.ReceiveMemo = transfer.ReceiveMemo?.Trim() ?? string.Empty;
		entity.ReceiveEvidencePath = existing?.ReceiveEvidencePath ?? string.Empty;
		entity.RejectedByUsername = existing?.RejectedByUsername ?? string.Empty;
		entity.RejectedAtUtc = existing?.RejectedAtUtc;
		entity.RejectReason = transfer.RejectReason?.Trim() ?? string.Empty;
		entity.CreatedAtUtc = transfer.CreatedAtUtc;
		entity.UpdatedAtUtc = transfer.UpdatedAtUtc;
		entity.Revision = transfer.Revision;
		entity.IsDirty = transfer.IsDirty;
		if (existing == null)
		{
			entity.Lines = CloneTransferLines(validLines, transferId);
			_db.InventoryTransfers.Add(entity);
		}
		else
		{
			_db.Entry(existing).CurrentValues.SetValues(entity);
			_db.InventoryTransferLines.RemoveRange(existing.Lines);
			existing.Lines.Clear();
			foreach (LocalInventoryTransferLine line in CloneTransferLines(validLines, transferId))
			{
				existing.Lines.Add(line);
			}
		}
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInventoryTransfer",
			EntityId = transferId.ToString("D"),
			Action = ((existing == null) ? "Create" : "Update"),
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = string.Empty,
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			ForceOverride = false
		}, ct);
		return OfficeMutationResult.Ok(transferId, (existing == null) ? "재고이동을 저장했습니다." : "재고이동을 수정했습니다.");
	}

	public async Task<OfficeMutationResult> ConfirmInventoryTransferReceiptAsync(Guid transferId, IEnumerable<LocalInventoryTransferLine> receivedLines, string? receiveMemo, SessionState session, CancellationToken ct = default(CancellationToken), long? expectedRevision = null)
	{
		var transfer = await _db.InventoryTransfers.IgnoreQueryFilters().Include((LocalInventoryTransfer current) => current.Lines).FirstOrDefaultAsync((LocalInventoryTransfer current) => current.Id == transferId, ct);
		transfer = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, transfer, ct);
		if (transfer == null)
		{
			return OfficeMutationResult.Missing("재고이동 문서를 찾을 수 없습니다.");
		}
		var transferLinesEntry = _db.Entry(transfer).Collection(current => current.Lines);
		transferLinesEntry.IsLoaded = false;
		await transferLinesEntry.LoadAsync(ct);
		if (!CanReceiveInventoryTransfer(transfer, session))
		{
			return OfficeMutationResult.Denied("도착지 담당자 또는 관리자만 수령확정할 수 있습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(transfer, expectedRevision, "재고이동 문서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("이미 수령확정된 문서입니다.");
		}
		if (string.Equals(transfer.TransferStatus, "반려", StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("반려된 문서는 수령확정할 수 없습니다.");
		}
		DateTime now = DateTime.UtcNow;
		Dictionary<Guid, LocalInventoryTransferLine> receivedLineMap = (receivedLines ?? Array.Empty<LocalInventoryTransferLine>()).ToDictionary((LocalInventoryTransferLine localInventoryTransferLine) => localInventoryTransferLine.Id, (LocalInventoryTransferLine result) => result);
		foreach (LocalInventoryTransferLine line in transfer.Lines.Where((LocalInventoryTransferLine current) => !current.IsDeleted))
		{
			if (!receivedLineMap.TryGetValue(line.Id, out var received))
			{
				line.ReceivedQuantity = line.Quantity;
				line.QuantityDifference = 0m;
				line.ReceiptRemark = string.Empty;
				continue;
			}
			decimal receivedQuantity = received.ReceivedQuantity ?? line.Quantity;
			if (receivedQuantity < 0m)
			{
				receivedQuantity = default(decimal);
			}
			line.ReceivedQuantity = receivedQuantity;
			line.QuantityDifference = receivedQuantity - line.Quantity;
			line.ReceiptRemark = (received.ReceiptRemark ?? string.Empty).Trim();
			received = null;
		}
		transfer.TransferStatus = "수령확정";
		transfer.ReceiveMemo = (receiveMemo ?? string.Empty).Trim();
		transfer.ReceivedByUsername = session.User?.Username ?? "local-user";
		transfer.ReceivedAtUtc = now;
		transfer.LastStatusChangedByUsername = transfer.ReceivedByUsername;
		transfer.LastStatusChangedAtUtc = now;
		transfer.LastSavedByUsername = session.User?.Username ?? "local-user";
		transfer.LastSavedAtUtc = now;
		transfer.IsDirty = true;
		transfer.UpdatedAtUtc = now;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInventoryTransfer",
			EntityId = transferId.ToString("D"),
			Action = "ConfirmReceipt",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new
			{
				TransferStatus = transfer.TransferStatus,
				ReceiveMemo = transfer.ReceiveMemo,
				ReceivedByUsername = transfer.ReceivedByUsername,
				ReceivedAtUtc = transfer.ReceivedAtUtc,
				Lines = from localInventoryTransferLine in transfer.Lines
					where !localInventoryTransferLine.IsDeleted
					select new { localInventoryTransferLine.Id, localInventoryTransferLine.Quantity, localInventoryTransferLine.ReceivedQuantity, localInventoryTransferLine.QuantityDifference, localInventoryTransferLine.ReceiptRemark }
			}, AuditJsonOptions),
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			ForceOverride = false
		}, ct);
		return OfficeMutationResult.Ok(transferId, "재고이동 수령을 확정했습니다.");
	}

	public async Task<OfficeMutationResult> DeleteInventoryTransferAsync(Guid transferId, SessionState session, long? expectedRevision = null, CancellationToken ct = default(CancellationToken))
	{
		if (!CanEditDeliveries(session))
		{
			return OfficeMutationResult.Denied("납품/재고이동 편집 권한이 필요합니다.");
		}
		var transfer = await _db.InventoryTransfers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalInventoryTransfer current) => current.Id == transferId, ct);
		transfer = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, transfer, ct);
		if (transfer == null)
		{
			return OfficeMutationResult.Missing("재고이동 기록을 찾을 수 없습니다.");
		}
		string sourceOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode);
		string targetOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode);
		string normalizedStatus = InventoryTransferStatusNormalizer.Normalize(transfer.TransferStatus, transfer.ReceivedByUsername, transfer.ReceivedAtUtc, transfer.RejectedByUsername, transfer.RejectedAtUtc);
		if (normalizedStatus is InventoryTransferStatusNormalizer.Received or InventoryTransferStatusNormalizer.Rejected &&
		    (!CanWriteOfficeScope(session, sourceOfficeCode) || !CanWriteOfficeScope(session, targetOfficeCode)))
		{
			return OfficeMutationResult.Denied("출발지와 도착지 모두 수정 가능한 사용자만 수령확정 또는 반려된 재고이동을 삭제할 수 있습니다.");
		}
		if (normalizedStatus is not (InventoryTransferStatusNormalizer.Received or InventoryTransferStatusNormalizer.Rejected) && !CanWriteOfficeScope(session, sourceOfficeCode))
		{
			return OfficeMutationResult.Denied("출발지 담당자 또는 관리자만 대기 중인 재고이동을 삭제할 수 있습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureDeleteAllowed(transfer, expectedRevision, "재고이동 문서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		transfer.IsDeleted = true;
		transfer.IsDirty = true;
		transfer.UpdatedAtUtc = DateTime.UtcNow;
		transfer.LastSavedAtUtc = transfer.UpdatedAtUtc;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInventoryTransfer",
			EntityId = transferId.ToString("D"),
			Action = "Delete",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = string.Empty,
			CreatedAtUtc = DateTime.UtcNow
		});
		await _db.SaveChangesAsync(ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			ForceOverride = false
		}, ct);
		return OfficeMutationResult.Ok(transferId, "재고이동을 삭제했습니다.");
	}

	public async Task<OfficeMutationResult> RejectInventoryTransferAsync(Guid transferId, string rejectReason, SessionState session, CancellationToken ct = default(CancellationToken), long? expectedRevision = null)
	{
		var transfer = await _db.InventoryTransfers.IgnoreQueryFilters().FirstOrDefaultAsync((LocalInventoryTransfer current) => current.Id == transferId, ct);
		transfer = await LocalEntityConcurrencyGuard.ReloadTrackedEntityAsync(_db, transfer, ct);
		if (transfer == null)
		{
			return OfficeMutationResult.Missing("재고이동 문서를 찾을 수 없습니다.");
		}
		if (!CanReceiveInventoryTransfer(transfer, session))
		{
			return OfficeMutationResult.Denied("도착지 담당자 또는 관리자만 재고이동을 반려할 수 있습니다.");
		}
		if (!LocalEntityConcurrencyGuard.TryEnsureOperationAllowed(transfer, expectedRevision, "재고이동 문서", out string conflictMessage))
		{
			return OfficeMutationResult.Conflict(conflictMessage);
		}
		if (string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase))
		{
			return OfficeMutationResult.Denied("이미 수령확정된 문서는 반려할 수 없습니다.");
		}
		if (string.IsNullOrWhiteSpace(rejectReason))
		{
			return OfficeMutationResult.Denied("반려 사유를 입력하세요.");
		}
		DateTime now = DateTime.UtcNow;
		transfer.TransferStatus = "반려";
		transfer.RejectReason = rejectReason.Trim();
		transfer.RejectedByUsername = session.User?.Username ?? "local-user";
		transfer.RejectedAtUtc = now;
		transfer.LastStatusChangedByUsername = transfer.RejectedByUsername;
		transfer.LastStatusChangedAtUtc = now;
		transfer.LastSavedByUsername = session.User?.Username ?? "local-user";
		transfer.LastSavedAtUtc = now;
		transfer.IsDirty = true;
		transfer.UpdatedAtUtc = now;
		_db.AuditLogs.Add(new LocalAuditLog
		{
			EntityName = "LocalInventoryTransfer",
			EntityId = transferId.ToString("D"),
			Action = "Reject",
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			BeforeJson = string.Empty,
			AfterJson = JsonSerializer.Serialize(new { transfer.TransferStatus, transfer.RejectReason, transfer.RejectedByUsername, transfer.RejectedAtUtc }, AuditJsonOptions),
			CreatedAtUtc = now
		});
		await _db.SaveChangesAsync(ct);
		await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
		{
			Username = (session.User?.Username ?? "local-user"),
			Role = (session.User?.Role ?? "user"),
			OfficeCode = session.OfficeCode,
			ForceOverride = false
		}, ct);
		return OfficeMutationResult.Ok(transferId, "재고이동을 반려 처리했습니다.");
	}

	public Task<List<LocalItemWarehouseStock>> GetItemWarehouseStocksAsync(CancellationToken ct = default(CancellationToken))
	{
		return (from stock in _db.ItemWarehouseStocks.AsNoTracking()
			join item in _db.Items.IgnoreQueryFilters().AsNoTracking() on stock.ItemId equals item.Id
			where !item.IsDeleted
			orderby stock.ItemId, stock.WarehouseCode
			select stock).ToListAsync(ct);
	}

	public Task<List<LocalItemWarehouseStock>> GetItemWarehouseStocksForInventoryTransferAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		string tenantCode = ResolveCurrentTenantCode(session);
		List<string> tenantOfficeCodes = GetTenantOfficeCodes(session);
		List<string> tenantWarehouseCodes = GetTenantWarehouseCodes(session);
		return (from stock in _db.ItemWarehouseStocks.AsNoTracking()
			join item in _db.Items.IgnoreQueryFilters().AsNoTracking() on stock.ItemId equals item.Id
			where !item.IsDeleted && tenantWarehouseCodes.Contains(stock.WarehouseCode) && item.TenantCode == tenantCode && (item.OfficeCode == "ALL" || tenantOfficeCodes.Contains(item.OfficeCode))
			orderby stock.ItemId, stock.WarehouseCode
			select stock).ToListAsync(ct);
	}

	public Task<List<LocalInventoryMovement>> GetInventoryMovementsAsync(Guid itemId, int take = 200, CancellationToken ct = default(CancellationToken))
	{
		return (from movement in _db.InventoryMovements.AsNoTracking()
			where movement.ItemId == itemId && movement.IsActive
			orderby movement.OccurredDate descending, movement.CreatedAtUtc descending
			select movement).Take(Math.Max(1, take)).ToListAsync(ct);
	}

	public Task<List<LocalCostAllocation>> GetCostAllocationsForInvoiceAsync(Guid salesInvoiceId, CancellationToken ct = default(CancellationToken))
	{
		return (from allocation in _db.CostAllocations.AsNoTracking()
			where allocation.SalesInvoiceId == salesInvoiceId
			orderby allocation.CreatedAtUtc
			select allocation).ToListAsync(ct);
	}

	public async Task<List<LocalCostAllocation>> GetCostAllocationsForInvoicesAsync(IEnumerable<Guid> salesInvoiceIds, CancellationToken ct = default(CancellationToken))
	{
		List<Guid> ids = salesInvoiceIds.Where((Guid id) => id != Guid.Empty).Distinct().ToList();
		if (ids.Count == 0)
		{
			return new List<LocalCostAllocation>();
		}
		return await (from allocation in _db.CostAllocations.AsNoTracking()
			where ids.Contains(allocation.SalesInvoiceId)
			orderby allocation.CreatedAtUtc
			select allocation).ToListAsync(ct);
	}

	public Task<List<LocalAuditLog>> GetAuditLogsAsync(string entityName, string entityId, CancellationToken ct = default(CancellationToken))
	{
		return (from log in _db.AuditLogs.AsNoTracking()
			where log.EntityName == entityName && log.EntityId == entityId
			orderby log.CreatedAtUtc descending
			select log).ToListAsync(ct);
	}

	public async Task<int> CountDirtyAsync(CancellationToken ct = default(CancellationToken))
	{
		int count = 0;
		int num = count;
		count = num + await _db.CompanyProfiles.IgnoreQueryFilters().CountAsync((LocalCompanyProfile entity) => entity.IsDirty, ct);
		int num2 = count;
		count = num2 + await _db.Units.IgnoreQueryFilters().CountAsync((LocalUnit entity) => entity.IsDirty, ct);
		int num3 = count;
		count = num3 + await _db.CustomerCategories.IgnoreQueryFilters().CountAsync((LocalCustomerCategory entity) => entity.IsDirty, ct);
		int num4 = count;
		count = num4 + await _db.PriceGradeOptions.IgnoreQueryFilters().CountAsync((LocalPriceGradeOption entity) => entity.IsDirty, ct);
		int num5 = count;
		count = num5 + await _db.TradeTypeOptions.IgnoreQueryFilters().CountAsync((LocalTradeTypeOption entity) => entity.IsDirty, ct);
		int num6 = count;
		count = num6 + await _db.ItemCategoryOptions.IgnoreQueryFilters().CountAsync((LocalItemCategoryOption entity) => entity.IsDirty, ct);
		int num7 = count;
		count = num7 + await _db.CustomerMasters.IgnoreQueryFilters().CountAsync((LocalCustomerMaster entity) => entity.IsDirty, ct);
		int num8 = count;
		count = num8 + await _db.Customers.IgnoreQueryFilters().CountAsync((LocalCustomer entity) => entity.IsDirty, ct);
		int num9 = count;
		count = num9 + await _db.CustomerContracts.IgnoreQueryFilters().CountAsync((LocalCustomerContract entity) => entity.IsDirty, ct);
		int num10 = count;
		count = num10 + await _db.Items.IgnoreQueryFilters().CountAsync((LocalItem entity) => entity.IsDirty, ct);
		int num11 = count;
		count = num11 + await _db.Invoices.IgnoreQueryFilters().CountAsync((LocalInvoice entity) => entity.IsDirty, ct);
		int num12 = count;
		count = num12 + await _db.Payments.IgnoreQueryFilters().CountAsync((LocalPayment entity) => entity.IsDirty, ct);
		int num13 = count;
		count = num13 + await _db.Transactions.IgnoreQueryFilters().CountAsync((LocalTransaction entity) => entity.IsDirty, ct);
		int num14 = count;
		count = num14 + await _db.TransactionAttachments.IgnoreQueryFilters().CountAsync((LocalTransactionAttachment entity) => entity.IsDirty, ct);
		int num15 = count;
		count = num15 + await _db.InventoryTransfers.IgnoreQueryFilters().CountAsync((LocalInventoryTransfer entity) => entity.IsDirty, ct);
		int num16 = count;
		count = num16 + await _db.RentalManagementCompanies.IgnoreQueryFilters().CountAsync((LocalRentalManagementCompany entity) => entity.IsDirty, ct);
		int num17 = count;
		count = num17 + await _db.RentalBillingProfiles.IgnoreQueryFilters().CountAsync((LocalRentalBillingProfile entity) => entity.IsDirty, ct);
		int num18 = count;
		count = num18 + await _db.RentalAssets.IgnoreQueryFilters().CountAsync((LocalRentalAsset entity) => entity.IsDirty, ct);
		int num19 = count;
		count = num19 + await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters().CountAsync((LocalRentalAssetAssignmentHistory entity) => entity.IsDirty, ct);
		int num20 = count;
		return num20 + await _db.RentalBillingLogs.IgnoreQueryFilters().CountAsync((LocalRentalBillingLog entity) => entity.IsDirty, ct);
	}

	public async Task<int> CountDirtyAsync(SessionState session, CancellationToken ct = default(CancellationToken))
	{
		if (session == null || !session.IsLoggedIn)
		{
			return await CountDirtyAsync(ct);
		}
		int count = await CountDirtyAsync(ct);
		if (!session.HasPermission(AppPermissionNames.CompanyProfileEdit))
		{
			count -= await _db.CompanyProfiles.IgnoreQueryFilters().CountAsync((LocalCompanyProfile entity) => entity.IsDirty, ct);
		}
		if (!session.HasPermission(AppPermissionNames.SettingsEdit))
		{
			count -= await _db.Units.IgnoreQueryFilters().CountAsync((LocalUnit entity) => entity.IsDirty, ct);
			count -= await _db.CustomerCategories.IgnoreQueryFilters().CountAsync((LocalCustomerCategory entity) => entity.IsDirty, ct);
			count -= await _db.PriceGradeOptions.IgnoreQueryFilters().CountAsync((LocalPriceGradeOption entity) => entity.IsDirty, ct);
			count -= await _db.TradeTypeOptions.IgnoreQueryFilters().CountAsync((LocalTradeTypeOption entity) => entity.IsDirty, ct);
			count -= await _db.ItemCategoryOptions.IgnoreQueryFilters().CountAsync((LocalItemCategoryOption entity) => entity.IsDirty, ct);
		}
		if (!session.HasPermission(AppPermissionNames.RentalSettingsEdit))
		{
			count -= await _db.RentalManagementCompanies.IgnoreQueryFilters().CountAsync((LocalRentalManagementCompany entity) => entity.IsDirty, ct);
		}
		int dirtyCustomerMasterCount = await _db.CustomerMasters.IgnoreQueryFilters().CountAsync((LocalCustomerMaster entity) => entity.IsDirty, ct);
		if (dirtyCustomerMasterCount > 0)
		{
			int syncableDirtyCustomerMasterCount = (await GetDirtyCustomerMastersForSyncAsync(session, ct)).Count;
			count = count - dirtyCustomerMasterCount + syncableDirtyCustomerMasterCount;
		}
		int dirtyCustomerCount = await _db.Customers.IgnoreQueryFilters().CountAsync((LocalCustomer entity) => entity.IsDirty, ct);
		if (dirtyCustomerCount > 0)
		{
			int syncableDirtyCustomerCount = (await GetDirtyCustomersForSyncAsync(session, ct)).Count;
			count = count - dirtyCustomerCount + syncableDirtyCustomerCount;
		}
		int dirtyCustomerContractCount = await _db.CustomerContracts.IgnoreQueryFilters().CountAsync((LocalCustomerContract entity) => entity.IsDirty, ct);
		if (dirtyCustomerContractCount > 0)
		{
			int syncableDirtyCustomerContractCount = (await GetDirtyCustomerContractsForSyncAsync(session, ct)).Count;
			count = count - dirtyCustomerContractCount + syncableDirtyCustomerContractCount;
		}
		int dirtyItemCount = await _db.Items.IgnoreQueryFilters().CountAsync((LocalItem entity) => entity.IsDirty, ct);
		if (dirtyItemCount > 0)
		{
			int syncableDirtyItemCount = (await GetDirtyItemsForSyncAsync(session, ct)).Count;
			count = count - dirtyItemCount + syncableDirtyItemCount;
		}
		int dirtyRentalBillingProfileCount = await _db.RentalBillingProfiles.IgnoreQueryFilters().CountAsync((LocalRentalBillingProfile entity) => entity.IsDirty, ct);
		if (dirtyRentalBillingProfileCount > 0)
		{
			int syncableDirtyRentalBillingProfileCount = (await GetDirtyRentalBillingProfilesForSyncAsync(session, ct)).Count;
			count = count - dirtyRentalBillingProfileCount + syncableDirtyRentalBillingProfileCount;
		}
		int dirtyRentalAssetCount = await _db.RentalAssets.IgnoreQueryFilters().CountAsync((LocalRentalAsset entity) => entity.IsDirty, ct);
		if (dirtyRentalAssetCount > 0)
		{
			int syncableDirtyRentalAssetCount = (await GetDirtyRentalAssetsForSyncAsync(session, ct)).Count;
			count = count - dirtyRentalAssetCount + syncableDirtyRentalAssetCount;
		}
		int dirtyRentalAssetAssignmentHistoryCount = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters().CountAsync((LocalRentalAssetAssignmentHistory entity) => entity.IsDirty, ct);
		if (dirtyRentalAssetAssignmentHistoryCount > 0)
		{
			int syncableDirtyRentalAssetAssignmentHistoryCount = (await GetDirtyRentalAssetAssignmentHistoriesForSyncAsync(session, ct)).Count;
			count = count - dirtyRentalAssetAssignmentHistoryCount + syncableDirtyRentalAssetAssignmentHistoryCount;
		}
		int dirtyRentalBillingLogCount = await _db.RentalBillingLogs.IgnoreQueryFilters().CountAsync((LocalRentalBillingLog entity) => entity.IsDirty, ct);
		if (dirtyRentalBillingLogCount > 0)
		{
			int syncableDirtyRentalBillingLogCount = (await GetDirtyRentalBillingLogsForSyncAsync(session, ct)).Count;
			count = count - dirtyRentalBillingLogCount + syncableDirtyRentalBillingLogCount;
		}
		int dirtyTransactionCount = await _db.Transactions.IgnoreQueryFilters().CountAsync((LocalTransaction entity) => entity.IsDirty, ct);
		if (dirtyTransactionCount > 0)
		{
			int syncableDirtyTransactionCount = (await GetDirtyTransactionsForSyncAsync(session, ct)).Count;
			count = count - dirtyTransactionCount + syncableDirtyTransactionCount;
		}
		int dirtyTransactionAttachmentCount = await _db.TransactionAttachments.IgnoreQueryFilters().CountAsync((LocalTransactionAttachment entity) => entity.IsDirty, ct);
		if (dirtyTransactionAttachmentCount > 0)
		{
			int syncableDirtyTransactionAttachmentCount = (await GetDirtyTransactionAttachmentsForSyncAsync(session, ct)).Count;
			count = count - dirtyTransactionAttachmentCount + syncableDirtyTransactionAttachmentCount;
		}
		int dirtyInvoiceCount = await _db.Invoices.IgnoreQueryFilters().CountAsync((LocalInvoice entity) => entity.IsDirty, ct);
		if (dirtyInvoiceCount > 0)
		{
			int syncableDirtyInvoiceCount = (await GetDirtyInvoicesForSyncAsync(session, ct)).Count;
			count = count - dirtyInvoiceCount + syncableDirtyInvoiceCount;
		}
		int dirtyPaymentCount = await _db.Payments.IgnoreQueryFilters().CountAsync((LocalPayment entity) => entity.IsDirty, ct);
		if (dirtyPaymentCount > 0)
		{
			int syncableDirtyPaymentCount = (await GetDirtyPaymentsForSyncAsync(session, ct)).Count;
			count = count - dirtyPaymentCount + syncableDirtyPaymentCount;
		}
		int dirtyInventoryTransferCount = await _db.InventoryTransfers.IgnoreQueryFilters().CountAsync((LocalInventoryTransfer entity) => entity.IsDirty, ct);
		if (dirtyInventoryTransferCount > 0)
		{
			int syncableDirtyInventoryTransferCount = (await GetDirtyInventoryTransfersForSyncAsync(session, ct)).Count;
			count = count - dirtyInventoryTransferCount + syncableDirtyInventoryTransferCount;
		}
		return count;
	}

	private static InvoiceSaveContext NormalizeSaveContext(InvoiceSaveContext context)
	{
		return new InvoiceSaveContext
		{
			Username = (string.IsNullOrWhiteSpace(context?.Username) ? "system" : context.Username.Trim()),
			Role = (string.IsNullOrWhiteSpace(context?.Role) ? "user" : context.Role.Trim()),
			OfficeCode = NormalizeOfficeCode(context?.OfficeCode, DomainConstants.OfficeUsenet),
			ForceOverride = (context?.ForceOverride ?? false),
			AutoRebaseWhenLatestSavedBySameUser = context?.AutoRebaseWhenLatestSavedBySameUser ?? false,
			ExpectedConcurrencyStamp = context?.ExpectedConcurrencyStamp
		};
	}

	private IQueryable<LocalCustomer> ApplyCustomerScope(IQueryable<LocalCustomer> query, SessionState session)
	{
		if (HasFullAccess(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableOfficeCodes(session);
		string currentTenantCode = ResolveCurrentTenantCode(session);
		List<Guid> temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
		return query.Where((LocalCustomer customer) => customer.TenantCode == currentTenantCode && (customer.ResponsibleOfficeCode == "ALL" || readableOfficeCodes.Contains(customer.ResponsibleOfficeCode) || ((customer.ResponsibleOfficeCode == null || customer.ResponsibleOfficeCode == string.Empty) && readableOfficeCodes.Contains(customer.OfficeCode)) || temporaryCustomerIds.Contains(customer.Id)));
	}

	private IQueryable<LocalCustomer> ApplyRentalCustomerScope(IQueryable<LocalCustomer> query, SessionState session)
	{
		if (HasFullAccess(session) || CanAdministrativelyViewAllRentalScope(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableRentalOfficeCodes(session);
		string currentTenantCode = ResolveCurrentTenantCode(session);
		List<Guid> temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
		return query.Where((LocalCustomer customer) =>
			customer.TenantCode == currentTenantCode &&
			(customer.ResponsibleOfficeCode == "ALL" ||
			 readableOfficeCodes.Contains(customer.ResponsibleOfficeCode) ||
			 ((customer.ResponsibleOfficeCode == null || customer.ResponsibleOfficeCode == string.Empty) &&
			  readableOfficeCodes.Contains(customer.OfficeCode)) ||
			 temporaryCustomerIds.Contains(customer.Id)));
	}

	private static string ResolveCurrentTenantCode(SessionState? session)
	{
		return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session?.TenantCode, session?.OfficeCode);
	}

	private static string ResolveCustomerTenantCodeForOffice(string? officeCode, string? tenantCode = null, SessionState? session = null)
	{
		return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode, session?.TenantCode, session?.OfficeCode);
	}

	private static bool CanReadCustomerScope(SessionState? session, string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (HasFullAccess(session))
		{
			return true;
		}
		string resolvedOfficeCode = ResolveResponsibleOfficeScopeForAccess(officeCode, fallbackOfficeCode);
		string a = ResolveCustomerTenantCodeForOffice(resolvedOfficeCode, tenantCode, session);
		if (!string.Equals(a, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string text = NormalizeOfficeScope(resolvedOfficeCode, DomainConstants.OfficeUsenet);
		return IsSharedOfficeScope(text) || GetReadableOfficeCodes(session).Contains(text);
	}

	private static bool CanReadRentalCustomerScope(SessionState? session, string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (HasFullAccess(session) || CanAdministrativelyViewAllRentalScope(session))
		{
			return true;
		}
		string text = ResolveResponsibleOfficeScopeForAccess(officeCode, fallbackOfficeCode);
		return IsSharedOfficeScope(text) || GetReadableAssetOfficeCodes(session).Contains(text);
	}

	private static bool CanWriteCustomerScope(SessionState? session, string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (CanWriteAllScopedData(session))
		{
			return true;
		}
		string resolvedOfficeCode = ResolveResponsibleOfficeScopeForAccess(officeCode, fallbackOfficeCode);
		string a = ResolveCustomerTenantCodeForOffice(resolvedOfficeCode, tenantCode, session);
		if (!string.Equals(a, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return CanWriteOfficeScope(session, officeCode, fallbackOfficeCode);
	}

	private IQueryable<LocalItem> ApplyItemScope(IQueryable<LocalItem> query, SessionState session)
	{
		if (HasFullAccess(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableOfficeCodes(session);
		string tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
		return query.Where((LocalItem item) => item.TenantCode == tenantCode && (item.OfficeCode == "ALL" || readableOfficeCodes.Contains(item.OfficeCode)));
	}

	private async Task<InvoiceSaveResult?> ValidateInvoiceLineItemScopeAsync(
		IEnumerable<LocalInvoiceLine> lines,
		SessionState? session,
		CancellationToken ct)
	{
		var itemIds = lines
			.Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
			.Select(line => line.ItemId!.Value)
			.Distinct()
			.ToList();
		if (itemIds.Count == 0)
		{
			return null;
		}

		var items = await _db.Items
			.IgnoreQueryFilters()
			.AsNoTracking()
			.Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
			.ToDictionaryAsync(item => item.Id, ct);

		foreach (var itemId in itemIds)
		{
			if (!items.TryGetValue(itemId, out var item))
			{
				return InvoiceSaveResult.Missing($"전표 품목 정보를 찾을 수 없습니다: {itemId:D}");
			}

			if (!CanReadItemScope(item, session))
			{
				return InvoiceSaveResult.Denied("권한이 없어 현재 담당지점/회사 범위 밖의 품목을 전표에 저장할 수 없습니다.");
			}
		}

		return null;
	}

	private static bool CanReadItemScope(LocalItem item, SessionState? session)
	{
		if (HasFullAccess(session))
		{
			return true;
		}

		string itemOfficeCode = NormalizeOfficeScope(item.OfficeCode, "ALL");
		string itemTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
			item.TenantCode,
			itemOfficeCode,
			session?.TenantCode,
			session?.OfficeCode);
		if (!string.Equals(itemTenantCode, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return IsSharedOfficeScope(itemOfficeCode) || GetReadableOfficeCodes(session).Contains(itemOfficeCode);
	}

	private static string ResolveInvoiceResponsibleOfficeForSave(
		SessionState? session,
		string? requestedOfficeCode,
		string? fallbackOfficeCode)
	{
		if (session == null || !session.IsLoggedIn || session.HasGlobalDataScope)
		{
			return NormalizeOfficeScope(
				requestedOfficeCode,
				NormalizeOfficeScope(fallbackOfficeCode, session?.OfficeCode ?? DomainConstants.OfficeUsenet));
		}

		var writableOfficeCodes = GetWritableOfficeCodes(session);
		if (TryResolveWritableOperationalOffice(session, requestedOfficeCode, writableOfficeCodes, out var requestedOffice))
		{
			return requestedOffice;
		}

		if (TryResolveWritableOperationalOffice(session, fallbackOfficeCode, writableOfficeCodes, out var fallbackOffice))
		{
			return fallbackOffice;
		}

		return writableOfficeCodes.FirstOrDefault() ?? NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);
	}

	private static bool TryResolveWritableOperationalOffice(
		SessionState session,
		string? officeCode,
		IReadOnlySet<string> writableOfficeCodes,
		out string resolvedOfficeCode)
	{
		resolvedOfficeCode = string.Empty;
		if (!OfficeCodeCatalog.TryNormalizeScope(officeCode, out var normalizedOfficeCode))
		{
			return false;
		}

		if (IsSharedOfficeScope(normalizedOfficeCode))
		{
			if (!CanWriteSharedOfficeScope(session))
			{
				return false;
			}

			resolvedOfficeCode = normalizedOfficeCode;
			return true;
		}

		if (!writableOfficeCodes.Contains(normalizedOfficeCode))
		{
			return false;
		}

		resolvedOfficeCode = normalizedOfficeCode;
		return true;
	}

	private static string ResolveOperationalTenantForSave(
		SessionState? session,
		string? requestedTenantCode,
		string? ownerOfficeCode,
		string? fallbackTenantCode,
		string? fallbackOfficeCode)
	{
		if (session != null && session.IsLoggedIn && !session.HasGlobalDataScope)
		{
			return ResolveCurrentTenantCode(session);
		}

		return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
			requestedTenantCode,
			ownerOfficeCode,
			fallbackTenantCode,
			fallbackOfficeCode);
	}

	private IQueryable<LocalItem> ApplyRentalItemScope(IQueryable<LocalItem> query, SessionState session)
	{
		if (HasFullAccess(session) || CanAdministrativelyViewAllRentalScope(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableAssetOfficeCodes(session);
		return query.Where((LocalItem item) => item.OfficeCode == "ALL" || readableOfficeCodes.Contains(item.OfficeCode));
	}

	private IQueryable<LocalInvoice> ApplyInvoiceScope(IQueryable<LocalInvoice> query, SessionState session)
	{
		if (HasFullAccess(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableOfficeCodes(session);
		string currentTenantCode = ResolveCurrentTenantCode(session);
		List<Guid> temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
		return query.Where((LocalInvoice invoice) =>
			invoice.TenantCode == currentTenantCode &&
			(invoice.ResponsibleOfficeCode == "ALL" ||
			 readableOfficeCodes.Contains(invoice.ResponsibleOfficeCode) ||
			 ((invoice.ResponsibleOfficeCode == null || invoice.ResponsibleOfficeCode == string.Empty) &&
			  readableOfficeCodes.Contains(invoice.OfficeCode)) ||
			 temporaryCustomerIds.Contains(invoice.CustomerId)));
	}

	private IQueryable<LocalTransaction> ApplyTransactionScope(IQueryable<LocalTransaction> query, SessionState session)
	{
		if (HasFullAccess(session))
		{
			return query;
		}
		HashSet<string> readableOfficeCodes = GetReadableOfficeCodes(session);
		string currentTenantCode = ResolveCurrentTenantCode(session);
		List<Guid> temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
		return query.Where((LocalTransaction transaction) =>
			transaction.TenantCode == currentTenantCode &&
			(transaction.ResponsibleOfficeCode == "ALL" ||
			 readableOfficeCodes.Contains(transaction.ResponsibleOfficeCode) ||
			 ((transaction.ResponsibleOfficeCode == null || transaction.ResponsibleOfficeCode == string.Empty) &&
			  readableOfficeCodes.Contains(transaction.OfficeCode)) ||
			 temporaryCustomerIds.Contains(transaction.CustomerId)));
	}

	public bool CanWriteItemScope(LocalItem item, SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (CanWriteAllScopedData(session))
		{
			return true;
		}
		string a = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode, session.TenantCode, session.OfficeCode);
		string b = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
		if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string text = NormalizeOfficeScope(item.OfficeCode, "ALL");
		if (text == "ALL")
		{
			return CanWriteSharedOfficeScope(session);
		}
		return GetWritableOfficeCodes(session).Contains(text);
	}

	private static bool HasFullAccess(SessionState? session)
	{
		return session == null || !session.IsLoggedIn || session.HasGlobalDataScope;
	}

	private static bool CanWriteAllScopedData(SessionState? session)
	{
		return session != null && session.IsLoggedIn && session.HasGlobalDataScope;
	}

	private static bool CanAdministrativelyViewAllRentalScope(SessionState? session)
	{
		return session != null && session.IsLoggedIn && (session.HasAdministrativePrivileges || session.HasGlobalDataScope);
	}

	private static bool CanViewAllRentalScope(SessionState? session)
	{
		return session != null && session.IsLoggedIn && (session.HasAdministrativePrivileges || session.HasAssignedPermission("Rental.ViewAll") || session.HasAssignedPermission("Rental.EditAll"));
	}

	private static bool CanManageAllRentalScope(SessionState? session)
	{
		return session != null && session.IsLoggedIn && (session.HasAdministrativePrivileges || session.HasPermission("Rental.EditAll"));
	}

	private static bool CanViewAllDeliveryScope(SessionState? session)
	{
		return session != null && session.IsLoggedIn && (session.HasAdministrativePrivileges || session.HasAssignedPermission("Delivery.ViewAll"));
	}

	private static bool CanViewAllTenantOperationalCustomers(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		string tenantCode = ResolveCurrentTenantCode(session);
		string normalizedOfficeCode = NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet);
		string? b = (from code in TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode)
			select NormalizeOfficeCode(code, normalizedOfficeCode)).FirstOrDefault();
		return session.HasGlobalDataScope || string.Equals(session.ScopeType, "TenantAll", StringComparison.OrdinalIgnoreCase) || string.Equals(normalizedOfficeCode, b, StringComparison.OrdinalIgnoreCase);
	}

	private static bool CanWriteSharedOfficeScope(SessionState? session)
	{
		return session != null && session.IsLoggedIn && (session.HasGlobalDataScope || string.Equals(session.ScopeType, "TenantAll", StringComparison.OrdinalIgnoreCase));
	}

	private static IReadOnlyList<string> OrderOfficeCodes(IEnumerable<string> officeCodes)
	{
		return (from officeCode in officeCodes
			select NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet) into officeCode
			where !string.IsNullOrWhiteSpace(officeCode)
			select officeCode).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy(GetOfficeSortOrder).ThenBy<string, string>((string officeCode) => officeCode, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static string ResolveOperationalEntityTenantCode(string? tenantCode, string? responsibleOfficeCode, string? fallbackOfficeCode, SessionState? session = null)
	{
		if (TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out string normalizedTenantCode))
		{
			return normalizedTenantCode;
		}
		string resolvedOfficeCode = ResolveResponsibleOfficeScopeForAccess(responsibleOfficeCode, fallbackOfficeCode);
		string? officeCodeForTenant = IsSharedOfficeScope(resolvedOfficeCode) ? fallbackOfficeCode : resolvedOfficeCode;
		return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCodeForTenant, session?.TenantCode, session?.OfficeCode);
	}

	private static bool IsOperationalEntityInCurrentTenant(SessionState? session, string? tenantCode, string? responsibleOfficeCode, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		string entityTenantCode = ResolveOperationalEntityTenantCode(tenantCode, responsibleOfficeCode, fallbackOfficeCode, session);
		return string.Equals(entityTenantCode, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase);
	}

	private static bool CanWriteOperationalScope(SessionState? session, string? tenantCode, string? responsibleOfficeCode, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (CanWriteAllScopedData(session))
		{
			return true;
		}
		if (!IsOperationalEntityInCurrentTenant(session, tenantCode, responsibleOfficeCode, fallbackOfficeCode))
		{
			return false;
		}
		return CanWriteOfficeScope(session, responsibleOfficeCode, fallbackOfficeCode);
	}

	private static bool CanWriteOfficeScope(SessionState? session, string? officeCode, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (CanWriteAllScopedData(session))
		{
			return true;
		}
		HashSet<string> writableOfficeCodes = GetWritableOfficeCodes(session);
		string?[] array = new string?[2] { officeCode, fallbackOfficeCode };
		foreach (string? officeCode2 in array)
		{
			if (OfficeCodeCatalog.TryNormalizeScope(officeCode2, out string text))
			{
				if (IsSharedOfficeScope(text))
				{
					return CanWriteSharedOfficeScope(session);
				}
				if (writableOfficeCodes.Contains(text))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool CanWriteRentalScope(SessionState? session, string? officeCode, string? fallbackOfficeCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (CanWriteAllScopedData(session))
		{
			return true;
		}
		string b = ResolveCurrentTenantCode(session);
		string?[] array = new string?[2] { officeCode, fallbackOfficeCode };
		foreach (string? officeCode2 in array)
		{
			string text = NormalizeOfficeScope(officeCode2, string.Empty);
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			string a = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, text);
			if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
			{
				if (IsSharedOfficeScope(text))
				{
					return CanWriteSharedOfficeScope(session);
				}
				if (GetWritableRentalOfficeCodes(session).Contains(text))
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool CanWriteRentalBillingLogScope(SessionState? session, LocalRentalBillingLog log)
	{
		var scope = RentalScopeNormalizer.ResolveScope(
			log.TenantCode,
			log.OfficeCode,
			responsibleOfficeCode: log.ResponsibleOfficeCode);

		return CanWriteRentalEntityScope(
			session,
			scope.TenantCode,
			scope.ResponsibleOfficeCode,
			scope.OwnerOfficeCode);
	}

	private static bool CanWriteRentalEntityScope(SessionState? session, string? tenantCode, string? responsibleOfficeCode, string? managementCompanyCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		string sessionTenantCode = ResolveCurrentTenantCode(session);
		string entityTenantCode = ResolveRentalEntityTenantCode(tenantCode, managementCompanyCode, responsibleOfficeCode);
		if (!string.Equals(entityTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		HashSet<string> writableOfficeCodes = GetWritableRentalOfficeCodes(session);
		string?[] array = new string?[2] { responsibleOfficeCode, managementCompanyCode };
		foreach (string? officeCode in array)
		{
			string text = NormalizeOfficeScope(officeCode, string.Empty);
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			if (IsSharedOfficeScope(text))
			{
				if (CanWriteSharedOfficeScope(session))
				{
					return true;
				}
				continue;
			}
			string officeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, text);
			if (string.Equals(officeTenantCode, sessionTenantCode, StringComparison.OrdinalIgnoreCase) && writableOfficeCodes.Contains(text))
			{
				return true;
			}
		}
		return false;
	}

	private static bool CanReadRentalEntityScope(SessionState? session, string? tenantCode, string? responsibleOfficeCode, string? managementCompanyCode = null)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return false;
		}
		if (session.HasGlobalDataScope || CanAdministrativelyViewAllRentalScope(session))
		{
			return true;
		}
		string entityTenantCode = ResolveRentalEntityTenantCode(tenantCode, managementCompanyCode, responsibleOfficeCode);
		if (!string.Equals(entityTenantCode, ResolveCurrentTenantCode(session), StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (CanViewAllRentalScope(session))
		{
			return true;
		}
		string officeCode = ResolveResponsibleOfficeScopeForAccess(responsibleOfficeCode, managementCompanyCode);
		return IsSharedOfficeScope(officeCode) || GetReadableRentalOfficeCodes(session).Contains(officeCode);
	}

	private static string ResolveRentalEntityTenantCode(string? tenantCode, string? managementCompanyCode, string? responsibleOfficeCode)
	{
		if (TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out string normalizedTenantCode))
		{
			return normalizedTenantCode;
		}
		if (!string.IsNullOrWhiteSpace(managementCompanyCode))
		{
			return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, managementCompanyCode, null, responsibleOfficeCode);
		}
		return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, responsibleOfficeCode);
	}

	private static HashSet<string> GetReadableRentalOfficeCodes(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return TenantScopeCatalog.ResolveScopedOfficeCodes(session.OfficeCode, ResolveCurrentTenantCode(session), session.ScopeType, CanAdministrativelyViewAllRentalScope(session), CanViewAllRentalScope(session));
	}

	private static HashSet<string> GetReadableAssetOfficeCodes(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return (from officeCode in OfficeCodeCatalog.All
			select NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet) into officeCode
			where !string.IsNullOrWhiteSpace(officeCode)
			select officeCode).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	private static HashSet<string> GetWritableRentalOfficeCodes(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return TenantScopeCatalog.ResolveScopedOfficeCodes(session.OfficeCode, ResolveCurrentTenantCode(session), session.ScopeType, session.HasGlobalDataScope, CanManageAllRentalScope(session));
	}

	private static HashSet<string> GetReadableOfficeCodes(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return TenantScopeCatalog.ResolveScopedOfficeCodes(session.OfficeCode, ResolveCurrentTenantCode(session), session.ScopeType, session.HasGlobalDataScope);
	}

	private static HashSet<string> GetWritableOfficeCodes(SessionState? session)
	{
		if (session == null || !session.IsLoggedIn)
		{
			return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}
		return TenantScopeCatalog.ResolveScopedOfficeCodes(session.OfficeCode, ResolveCurrentTenantCode(session), session.ScopeType, session.HasGlobalDataScope);
	}

	private static List<string> GetTenantOfficeCodes(SessionState? session)
	{
		return (from officeCode in TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(ResolveCurrentTenantCode(session))
			select NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet)).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static List<string> GetTenantWarehouseCodes(SessionState? session)
	{
		return GetTenantOfficeCodes(session).Select(OfficeCodeCatalog.GetMainWarehouseCode).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool HasOfficeReadAccess(SessionState? session, string? officeCode)
	{
		string text = NormalizeOfficeScope(officeCode, DomainConstants.OfficeUsenet);
		return IsSharedOfficeScope(text) || GetReadableOfficeCodes(session).Contains(text);
	}

	private bool CanAccessCustomer(LocalCustomer customer, SessionState? session)
	{
		return CanAccessCustomer(customer.Id, customer.ResponsibleOfficeCode, customer.TenantCode, session, session?.User?.Role, customer.OfficeCode);
	}

	private bool CanAccessCustomer(Guid customerId, string? customerOfficeCode, string? customerTenantCode, SessionState? session, string? role, string? fallbackOfficeCode = null)
	{
		if (HasFullAccess(session))
		{
			return true;
		}
		if (CanReadCustomerScope(session, customerOfficeCode, customerTenantCode, fallbackOfficeCode))
		{
			return true;
		}
		return session != null && _officeAccess.HasTemporaryCustomerAccess(session, customerId);
	}

	private bool CanAccessInvoice(LocalInvoice invoice, SessionState? session)
	{
		if (HasFullAccess(session))
		{
			return true;
		}
		if (!IsOperationalEntityInCurrentTenant(session, invoice.TenantCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode))
		{
			return false;
		}
		string officeCode = ResolveResponsibleOfficeScopeForAccess(invoice.ResponsibleOfficeCode, invoice.OfficeCode);
		if (IsSharedOfficeScope(officeCode))
		{
			return true;
		}
		if (HasOfficeReadAccess(session, officeCode))
		{
			return true;
		}
		return session != null && _officeAccess.HasTemporaryCustomerAccess(session, invoice.CustomerId);
	}

	private bool CanAccessTransaction(LocalTransaction transaction, SessionState? session)
	{
		if (HasFullAccess(session))
		{
			return true;
		}
		if (!IsOperationalEntityInCurrentTenant(session, transaction.TenantCode, transaction.ResponsibleOfficeCode, transaction.OfficeCode))
		{
			return false;
		}
		string officeCode = ResolveResponsibleOfficeScopeForAccess(transaction.ResponsibleOfficeCode, transaction.OfficeCode);
		if (IsSharedOfficeScope(officeCode))
		{
			return true;
		}
		if (HasOfficeReadAccess(session, officeCode))
		{
			return true;
		}
		return session != null && _officeAccess.HasTemporaryCustomerAccess(session, transaction.CustomerId);
	}

	private static string ResolveResponsibleOfficeScopeForAccess(string? responsibleOfficeCode, string? ownerOfficeCode)
	{
		if (OfficeCodeCatalog.TryNormalizeScope(responsibleOfficeCode, out string responsibleScope))
		{
			return responsibleScope;
		}
		if (OfficeCodeCatalog.TryNormalizeScope(ownerOfficeCode, out string ownerScope))
		{
			return ownerScope;
		}
		return DomainConstants.OfficeUsenet;
	}

	private static string NormalizeOfficeCode(string? officeCode, string? fallback)
	{
		return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallback);
	}

	private static string NormalizeOfficeScope(string? officeCode, string? fallback)
	{
		return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, fallback);
	}

	private static bool IsSharedOfficeScope(string? officeCode)
	{
		return string.Equals(NormalizeOfficeScope(officeCode, "ALL"), "ALL", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeWarehouseCode(string? warehouseCode, string? officeCode, string? fallbackOfficeCode)
	{
		return OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, officeCode, fallbackOfficeCode);
	}

	private static int GetOfficeSortOrder(string? officeCode)
	{
		string text = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
		if (1 == 0)
		{
		}
		int result;
		if (string.Equals(text, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase))
		{
			result = 0;
		}
		else
		{
			string a = text;
			if (string.Equals(a, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase))
			{
				result = 1;
			}
			else
			{
				string a2 = text;
				result = ((!string.Equals(a2, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase)) ? 99 : 2);
			}
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static Guid ResolveVersionGroupId(LocalInvoice invoice, LocalInvoice? latest)
	{
		if (invoice.VersionGroupId != Guid.Empty)
		{
			return invoice.VersionGroupId;
		}
		if (latest != null)
		{
			if (latest.VersionGroupId != Guid.Empty)
			{
				return latest.VersionGroupId;
			}
			return latest.Id;
		}
		return (invoice.Id == Guid.Empty) ? Guid.NewGuid() : invoice.Id;
	}

	private async Task<LocalInvoice?> ResolveLatestVersionAsync(LocalInvoice invoice, CancellationToken ct)
	{
		Guid? versionGroupId = ((invoice.VersionGroupId == Guid.Empty) ? ((Guid?)null) : new Guid?(invoice.VersionGroupId));
		LocalInvoice? existingById = null;
		if (invoice.Id != Guid.Empty)
		{
			existingById = await _db.Invoices.Include((LocalInvoice i) => i.Lines).Include((LocalInvoice i) => i.Payments).FirstOrDefaultAsync((LocalInvoice i) => i.Id == invoice.Id, ct);
			if (existingById != null && existingById.VersionGroupId != Guid.Empty)
			{
				versionGroupId.GetValueOrDefault();
				if (!versionGroupId.HasValue)
				{
					Guid versionGroupId2 = existingById.VersionGroupId;
					versionGroupId = versionGroupId2;
				}
			}
		}
		if (!versionGroupId.HasValue)
		{
			return existingById;
		}
		return (await _db.Invoices.Include((LocalInvoice i) => i.Lines).Include((LocalInvoice i) => i.Payments).FirstOrDefaultAsync((LocalInvoice i) => i.VersionGroupId == ((Guid?)versionGroupId).Value && i.IsLatestVersion, ct)) ?? existingById;
	}

	private static List<LocalInvoiceLine> CloneLines(IEnumerable<LocalInvoiceLine> source, Guid invoiceId)
	{
		return source.Select((LocalInvoiceLine line, int index) => new LocalInvoiceLine
		{
			Id = Guid.NewGuid(),
			InvoiceId = invoiceId,
			ItemId = line.ItemId,
			ItemNameOriginal = (line.ItemNameOriginal ?? string.Empty),
			SpecificationOriginal = (line.SpecificationOriginal ?? string.Empty),
			Unit = (line.Unit ?? string.Empty),
			Quantity = line.Quantity,
			UnitPrice = line.UnitPrice,
			LineAmount = line.LineAmount,
			Remark = (line.Remark ?? string.Empty),
			SerialNumber = (line.SerialNumber ?? string.Empty),
			MaterialNumber = (line.MaterialNumber ?? string.Empty),
			InstallLocation = (line.InstallLocation ?? string.Empty),
			RentalStartDate = line.RentalStartDate,
			RentalEndDate = line.RentalEndDate,
			OrderIndex = index + 1,
			ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType),
			IsDeleted = false
		}).ToList();
	}

	private static List<LocalPayment> ClonePayments(IEnumerable<LocalPayment> source, Guid invoiceId, DateTime now)
	{
		return source.Select((LocalPayment payment) => new LocalPayment
		{
			Id = Guid.NewGuid(),
			InvoiceId = invoiceId,
			PaymentDate = payment.PaymentDate,
			Amount = payment.Amount,
			Note = payment.Note,
			IsDeleted = false,
			IsDirty = true,
			CreatedAtUtc = now,
			UpdatedAtUtc = now,
			Revision = payment.Revision
		}).ToList();
	}

	private async Task<string> GenerateLocalTempNumberAsync(DateOnly invoiceDate, CancellationToken ct)
	{
		string yearMonth = invoiceDate.ToString("yyyyMM");
		string prefix = "L" + yearMonth + "-";
		int count = await _db.Invoices.IgnoreQueryFilters().CountAsync((LocalInvoice invoice) => invoice.LocalTempNumber.StartsWith(prefix), ct);
		return $"{prefix}{count + 1:D4}";
	}

	private async Task<string> GenerateTransferNumberAsync(DateOnly transferDate, CancellationToken ct)
	{
		string yearMonth = transferDate.ToString("yyyyMM");
		string prefix = "TR" + yearMonth + "-";
		int count = await _db.InventoryTransfers.IgnoreQueryFilters().CountAsync((LocalInventoryTransfer transfer) => transfer.TransferNumber.StartsWith(prefix), ct);
		return $"{prefix}{count + 1:D4}";
	}

	private static List<LocalInventoryTransferLine> CloneTransferLines(IEnumerable<LocalInventoryTransferLine> source, Guid transferId)
	{
		return source.Select((LocalInventoryTransferLine line) => new LocalInventoryTransferLine
		{
			Id = ((line.Id == Guid.Empty) ? Guid.NewGuid() : line.Id),
			TransferId = transferId,
			ItemId = line.ItemId,
			ItemNameOriginal = (line.ItemNameOriginal ?? string.Empty),
			SpecificationOriginal = (line.SpecificationOriginal ?? string.Empty),
			Unit = (line.Unit ?? string.Empty),
			Quantity = line.Quantity,
			ReceivedQuantity = (line.ReceivedQuantity ?? line.Quantity),
			QuantityDifference = (line.QuantityDifference ?? ((line.ReceivedQuantity ?? line.Quantity) - line.Quantity)),
			Remark = (line.Remark ?? string.Empty),
			ReceiptRemark = (line.ReceiptRemark ?? string.Empty),
			IsDeleted = false
		}).ToList();
	}

	private static bool IsFinalTransferStatus(string? status)
	{
		return string.Equals(status, "수령확정", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "반려", StringComparison.OrdinalIgnoreCase);
	}

	private bool CanReceiveInventoryTransfer(LocalInventoryTransfer transfer, SessionState? session)
	{
		if (!CanEditDeliveries(session))
		{
			return false;
		}
		if (HasFullAccess(session))
		{
			return true;
		}
		string a = ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode);
		string b = NormalizeOfficeCode(session?.OfficeCode, DomainConstants.OfficeUsenet);
		return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
	}

	private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
	{
		string text = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
		string text2 = text;
		if (1 == 0)
		{
		}
		string result;
		if (string.Equals(text2, DomainConstants.WarehouseItworldMain, StringComparison.OrdinalIgnoreCase))
		{
			result = DomainConstants.OfficeItworld;
		}
		else
		{
			string a = text2;
			result = ((!string.Equals(a, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase)) ? DomainConstants.OfficeUsenet : DomainConstants.OfficeYeonsu);
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string NormalizeAttachmentType(string? attachmentType)
	{
		string text = (attachmentType ?? string.Empty).Trim();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"입금확인증" => "입금확인증",
			"영수증" => "영수증",
			"세금계산서" => "세금계산서",
			"계좌이체" => "계좌이체",
			"카드전표" => "카드전표",
			_ => "기타",
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string NormalizeCustomerContractType(string? contractType)
	{
		string text = (contractType ?? string.Empty).Trim();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"거래계약서" => "거래계약서",
			"렌탈계약서" => "렌탈계약서",
			"유지보수계약서" => "유지보수계약서",
			"특약서" => "특약서",
			_ => "기타",
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string NormalizeAttachmentVerificationStatus(string? verificationStatus)
	{
		string text = (verificationStatus ?? string.Empty).Trim();
		if (1 == 0)
		{
		}
		string result = ((text == "확인완료") ? "확인완료" : ((!(text == "반려")) ? "미확인" : "반려"));
		if (1 == 0)
		{
		}
		return result;
	}

	private static string ResolveMimeType(string? extension)
	{
		string text = (extension ?? string.Empty).Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result;
		switch (text)
		{
		case ".pdf":
			result = "application/pdf";
			break;
		case ".png":
			result = "image/png";
			break;
		case ".jpg":
		case ".jpeg":
			result = "image/jpeg";
			break;
		case ".bmp":
			result = "image/bmp";
			break;
		case ".gif":
			result = "image/gif";
			break;
		case ".webp":
			result = "image/webp";
			break;
		case ".tif":
		case ".tiff":
			result = "image/tiff";
			break;
		case ".heic":
			result = "image/heic";
			break;
		case ".heif":
			result = "image/heif";
			break;
		default:
			result = "application/octet-stream";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string ComputeFileHash(string filePath)
	{
		using FileStream inputStream = File.OpenRead(filePath);
		using SHA256 sHA = SHA256.Create();
		byte[] inArray = sHA.ComputeHash(inputStream);
		return Convert.ToHexString(inArray);
	}

	private static string ComputeFileHash(byte[] fileContent)
	{
		using SHA256 sHA = SHA256.Create();
		byte[] inArray = sHA.ComputeHash(fileContent);
		return Convert.ToHexString(inArray);
	}

	private async Task ClearPrimaryCustomerContractAsync(Guid customerId, Guid? exceptContractId, CancellationToken ct)
	{
		foreach (LocalCustomerContract current in await (from localCustomerContract in _db.CustomerContracts.IgnoreQueryFilters()
			where localCustomerContract.CustomerId == customerId && !localCustomerContract.IsDeleted && localCustomerContract.IsPrimary && (!((Guid?)exceptContractId).HasValue || localCustomerContract.Id != ((Guid?)exceptContractId).Value)
			select localCustomerContract).ToListAsync(ct))
		{
			current.IsPrimary = false;
			current.IsDirty = true;
			current.UpdatedAtUtc = DateTime.UtcNow;
		}
	}

	private async Task SoftDeleteCustomerContractsAsync(Guid customerId, CancellationToken ct)
	{
		foreach (LocalCustomerContract contract in await (from current in _db.CustomerContracts.IgnoreQueryFilters()
			where current.CustomerId == customerId && !current.IsDeleted
			select current).ToListAsync(ct))
		{
			contract.IsDeleted = true;
			contract.IsDirty = true;
			contract.IsPrimary = false;
			contract.UpdatedAtUtc = DateTime.UtcNow;
		}
	}

	private async Task<string?> BuildCustomerDeletionReferenceBlockMessageAsync(Guid customerId, CancellationToken ct)
	{
		var activeInvoiceCount = await _db.Invoices
			.IgnoreQueryFilters()
			.CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, ct);
		var activeTransactionCount = await _db.Transactions
			.IgnoreQueryFilters()
			.CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, ct);
		var activeRentalProfileCount = await _db.RentalBillingProfiles
			.IgnoreQueryFilters()
			.CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, ct);
		var activeRentalAssetCount = await _db.RentalAssets
			.IgnoreQueryFilters()
			.CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, ct);
		var activeCurrentAssignmentHistoryCount = await _db.RentalAssetAssignmentHistories
			.IgnoreQueryFilters()
			.CountAsync(current => current.CustomerId == customerId && !current.IsDeleted && current.IsCurrent, ct);

		var parts = new List<string>();
		AddReferenceCount(parts, "전표", activeInvoiceCount);
		AddReferenceCount(parts, "거래내역", activeTransactionCount);
		AddReferenceCount(parts, "렌탈 청구", activeRentalProfileCount);
		AddReferenceCount(parts, "렌탈 자산", activeRentalAssetCount);
		AddReferenceCount(parts, "현재 설치이력", activeCurrentAssignmentHistoryCount);

		return parts.Count == 0
			? null
			: $"연결된 활성 데이터({string.Join(", ", parts)})가 남아 있어 거래처를 삭제할 수 없습니다. 먼저 전표/거래내역/렌탈 연결을 다른 거래처로 옮기거나 삭제·해제한 뒤 다시 시도하세요.";
	}

	private static void AddReferenceCount(ICollection<string> parts, string label, int count)
	{
		if (count > 0)
		{
			parts.Add($"{label} {count:N0}건");
		}
	}

	private static object BuildAuditInvoice(LocalInvoice invoice)
	{
		return new
		{
			Id = invoice.Id,
			VersionGroupId = invoice.VersionGroupId,
			VersionNumber = invoice.VersionNumber,
			InvoiceNumber = invoice.InvoiceNumber,
			LocalTempNumber = invoice.LocalTempNumber,
			CustomerId = invoice.CustomerId,
			VoucherType = invoice.VoucherType,
			InvoiceDate = invoice.InvoiceDate,
			TotalAmount = invoice.TotalAmount,
			SupplyAmount = invoice.SupplyAmount,
			VatAmount = invoice.VatAmount,
			VatMode = invoice.VatMode,
			TaxInvoiceIssued = invoice.TaxInvoiceIssued,
			PurchaseReceivingRequired = invoice.PurchaseReceivingRequired,
			PurchaseReceivingStatus = invoice.PurchaseReceivingStatus,
			PurchaseReceivedAtUtc = invoice.PurchaseReceivedAtUtc,
			PurchaseReceivedByUsername = invoice.PurchaseReceivedByUsername,
			PurchaseReceivingOfficeCode = invoice.PurchaseReceivingOfficeCode,
			PurchaseReceivingWarehouseCode = invoice.PurchaseReceivingWarehouseCode,
			PurchaseReceivingMemo = invoice.PurchaseReceivingMemo,
			Memo = invoice.Memo,
			ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
			SourceWarehouseCode = invoice.SourceWarehouseCode,
			DeliveryGroupId = invoice.DeliveryGroupId,
			ParentInvoiceId = invoice.ParentInvoiceId,
			IsLatestVersion = invoice.IsLatestVersion,
			IsConfirmed = invoice.IsConfirmed,
			ConcurrencyStamp = invoice.ConcurrencyStamp,
			CostStatus = invoice.CostStatus,
			Lines = (from line in invoice.Lines
				where !line.IsDeleted
				orderby (line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue), line.Id
				select new { line.Id, line.OrderIndex, line.ItemId, line.ItemNameOriginal, line.Quantity, line.UnitPrice, line.LineAmount, line.SerialNumber }).ToList()
		};
	}

	private async Task RebuildInventorySnapshotsAsync(InvoiceSaveContext context, CancellationToken ct)
	{
		List<LocalInventoryMovement> manualStockAdjustments = await _db.InventoryMovements.AsNoTracking()
			.Where((LocalInventoryMovement movement) => movement.IsActive && movement.ItemId.HasValue && (movement.MovementType == ManualStockAdjustmentMovementType || movement.MovementType == InventoryResetToZeroMovementType))
			.OrderBy((LocalInventoryMovement movement) => movement.OccurredDate)
			.ThenBy((LocalInventoryMovement movement) => movement.CreatedAtUtc)
			.ToListAsync(ct);
		await _db.InventoryMovements.ExecuteDeleteAsync(ct);
		await _db.StockLayers.ExecuteDeleteAsync(ct);
		await _db.CostAllocations.ExecuteDeleteAsync(ct);
		await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
		await _db.SerialLedgers.ExecuteDeleteAsync(ct);
		await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
		_db.ChangeTracker.Clear();
		List<LocalInvoice> invoices = await (from invoice in _db.Invoices.Include((LocalInvoice invoice) => invoice.Lines.Where((LocalInvoiceLine line) => !line.IsDeleted))
			where !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed
			orderby invoice.InvoiceDate, invoice.CreatedAtUtc, invoice.VersionNumber
			select invoice).ToListAsync(ct);
		List<LocalInventoryTransfer> transfers = await (from transfer in _db.InventoryTransfers.Include((LocalInventoryTransfer transfer) => transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted))
			where !transfer.IsDeleted
			orderby transfer.TransferDate, transfer.CreatedAtUtc
			select transfer).ToListAsync(ct);
		Dictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap = new Dictionary<(Guid, string), decimal>();
		Dictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap = new Dictionary<(Guid, string), List<LocalStockLayer>>();
		Dictionary<string, LocalSerialLedger> serialMap = new Dictionary<string, LocalSerialLedger>(StringComparer.OrdinalIgnoreCase);
		Dictionary<Guid, string> itemTrackingMap = await BuildItemTrackingMapAsync(ct);
		List<InventoryTimelineEntry> timeline = (from inventoryTimelineEntry in invoices
					.Select((LocalInvoice invoice) => new InventoryTimelineEntry(invoice.InvoiceDate, (invoice.LastSavedAtUtc == default(DateTime)) ? invoice.CreatedAtUtc : invoice.LastSavedAtUtc, 0, invoice, null, null))
					.Concat(transfers.Select((LocalInventoryTransfer transfer) => new InventoryTimelineEntry(transfer.TransferDate, (transfer.LastSavedAtUtc == default(DateTime)) ? transfer.CreatedAtUtc : transfer.LastSavedAtUtc, 1, null, transfer, null)))
					.Concat(manualStockAdjustments.Select((LocalInventoryMovement movement) => new InventoryTimelineEntry(movement.OccurredDate, movement.CreatedAtUtc, 2, null, null, movement)))
			orderby inventoryTimelineEntry.OccurredDate, inventoryTimelineEntry.SortUtc, inventoryTimelineEntry.Sequence
			select inventoryTimelineEntry).ToList();
		foreach (InventoryTimelineEntry entry in timeline)
		{
			if (entry.Invoice != null)
			{
				ApplyInvoiceInventoryEntry(entry.Invoice, context, stockMap, layerMap, serialMap, itemTrackingMap);
			}
			else if (entry.Transfer != null)
			{
				ApplyInventoryTransferEntry(entry.Transfer, stockMap, layerMap);
			}
			else if (entry.ManualAdjustment != null)
			{
				if (string.Equals(entry.ManualAdjustment.MovementType, InventoryResetToZeroMovementType, StringComparison.Ordinal))
				{
					ApplyInventoryResetToZeroEntry(entry.ManualAdjustment, stockMap, layerMap);
				}
				else
				{
					ApplyManualStockAdjustmentEntry(entry.ManualAdjustment, stockMap, layerMap);
				}
			}
		}
		var normalizedStocks = (from anon in Enumerable.Select(stockMap, (KeyValuePair<(Guid ItemId, string WarehouseCode), decimal> keyValuePair) => new
			{
				ItemId = keyValuePair.Key.ItemId,
				WarehouseCode = NormalizeWarehouseCode(keyValuePair.Key.WarehouseCode, ResolveOfficeCodeFromWarehouseCode(keyValuePair.Key.WarehouseCode), DomainConstants.OfficeUsenet),
				Value = keyValuePair.Value
			})
			group anon by (ItemId: anon.ItemId, WarehouseCode: anon.WarehouseCode) into @group
			select new
			{
				ItemId = @group.Key.ItemId,
				WarehouseCode = @group.Key.WarehouseCode,
				Quantity = @group.Sum(anon => anon.Value)
			}).ToList();
		foreach (LocalSerialLedger ledger in serialMap.Values)
		{
			_db.SerialLedgers.Add(ledger);
		}
		Dictionary<Guid, decimal> itemStockTotals = (from anon in normalizedStocks
			group anon by anon.ItemId).ToDictionary(group => group.Key, group => group.Sum(anon => anon.Quantity));
		foreach (LocalItem item in await _db.Items.ToListAsync(ct))
		{
			decimal totalStock;
			decimal recalculatedStock = (itemStockTotals.TryGetValue(item.Id, out totalStock) ? totalStock : 0m);
			if (!(item.CurrentStock == recalculatedStock))
			{
				item.CurrentStock = recalculatedStock;
				item.IsDirty = true;
				item.UpdatedAtUtc = DateTime.UtcNow;
			}
		}
		await _db.SaveChangesAsync(ct);
		foreach (var stock in normalizedStocks)
		{
			await _db.Database.ExecuteSqlInterpolatedAsync($"\nINSERT INTO ItemWarehouseStocks (ItemId, WarehouseCode, Quantity, UpdatedAtUtc, Revision)\nVALUES ({stock.ItemId}, {stock.WarehouseCode}, {stock.Quantity}, {DateTime.UtcNow}, {0L})\nON CONFLICT(ItemId, WarehouseCode) DO UPDATE SET\n    Quantity = excluded.Quantity,\n    UpdatedAtUtc = excluded.UpdatedAtUtc;", ct);
		}
		RaiseInventoryStateChanged();
	}

	private async Task SyncItemWarehouseStocksAsync(Guid itemId, decimal currentStock, string? preferredOfficeCode, bool removeStocks, CancellationToken ct)
	{
		List<LocalItemWarehouseStock> stocks = await _db.ItemWarehouseStocks.Where((LocalItemWarehouseStock stock) => stock.ItemId == itemId).ToListAsync(ct);
		if (removeStocks)
		{
			if (stocks.Count > 0)
			{
				_db.ItemWarehouseStocks.RemoveRange(stocks);
			}
		}
		else if (stocks.Count <= 0 && !(currentStock == 0m))
		{
			_db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
			{
				ItemId = itemId,
				WarehouseCode = ResolvePrimaryWarehouseCode(preferredOfficeCode),
				Quantity = currentStock,
				UpdatedAtUtc = DateTime.UtcNow
			});
		}
	}

	private static string ResolvePrimaryWarehouseCode(string? preferredOfficeCode)
	{
		string text = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(preferredOfficeCode, DomainConstants.OfficeUsenet);
		if (1 == 0)
		{
		}
		string result = ((text == "ITWORLD") ? DomainConstants.WarehouseItworldMain : ((!(text == "YEONSU")) ? DomainConstants.WarehouseUsenetMain : DomainConstants.WarehouseYeonsuMain));
		if (1 == 0)
		{
		}
		return result;
	}

	private static void NormalizeItemOperationalState(LocalItem item)
	{
		item.TrackingType = ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental);
		item.ItemKind = ItemOperationalPolicy.NormalizeItemKind(item.ItemKind, item.TrackingType, item.CategoryName, item.IsRental);
		string trackingType = item.TrackingType;
		string text = trackingType;
		if (!(text == "자산"))
		{
			if (text == "비재고")
			{
				item.IsRental = false;
				item.IsSale = true;
				item.CurrentStock = 0m;
			}
			else
			{
				item.IsRental = false;
				item.IsSale = true;
			}
		}
		else
		{
			item.IsRental = true;
			item.IsSale = false;
			item.CurrentStock = 0m;
		}
	}

	private static void NormalizeItemScope(LocalItem item, string? preferredOfficeCode)
	{
		string a = ItemTrackingTypes.Normalize(item.TrackingType);
		item.OfficeCode = (string.Equals(a, "자산", StringComparison.OrdinalIgnoreCase) ? NormalizeOfficeCode(item.OfficeCode, NormalizeOfficeCode(preferredOfficeCode, DomainConstants.OfficeUsenet)) : NormalizeOfficeScope(item.OfficeCode, "ALL"));
		item.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode, null, preferredOfficeCode);
	}

	private async Task<string> EnsureItemCategoryOptionExistsAsync(string? categoryName, CancellationToken ct, bool allowCreateOrReactivate = false)
	{
		string normalizedName = SelectionOptionDefaults.NormalizeItemCategoryName(categoryName);
		if (string.IsNullOrWhiteSpace(normalizedName))
		{
			return string.Empty;
		}
		string normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
		IEnumerable<LocalItemCategoryOption> local = _db.ItemCategoryOptions.Local;
		List<LocalItemCategoryOption> options = (from option in local.Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
			group option by option.Id into @group
			select @group.First()).ToList();
		var existing = options.FirstOrDefault((LocalItemCategoryOption option) => string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name), normalizedKey, StringComparison.OrdinalIgnoreCase));
		if (existing != null)
		{
			normalizedName = (string.IsNullOrWhiteSpace(existing.Name) ? normalizedName : existing.Name);
			if (existing.IsActive && !existing.IsDeleted)
			{
				return normalizedName;
			}
			if (!allowCreateOrReactivate)
			{
				throw new InvalidOperationException("등록되지 않았거나 삭제된 품목분류 '" + normalizedName + "'입니다. 선택값 관리에서 먼저 복구하거나 다시 추가하세요.");
			}
			if (!existing.IsActive || existing.IsDeleted)
			{
				existing.IsActive = true;
				existing.IsDeleted = false;
				existing.IsDirty = true;
				existing.UpdatedAtUtc = DateTime.UtcNow;
			}
			return normalizedName;
		}
		if (!allowCreateOrReactivate)
		{
			throw new InvalidOperationException("등록되지 않은 품목분류 '" + normalizedName + "'입니다. 선택값 관리에서 먼저 추가하세요.");
		}
		DateTime now = DateTime.UtcNow;
		int nextSortOrder = (from option in options
			where !option.IsDeleted
			select option.SortOrder).DefaultIfEmpty(0).Max() + 10;
		_db.ItemCategoryOptions.Add(new LocalItemCategoryOption
		{
			Id = Guid.NewGuid(),
			Name = normalizedName,
			SortOrder = nextSortOrder,
			IsSystemDefault = false,
			IsActive = true,
			IsDeleted = false,
			IsDirty = true,
			CreatedAtUtc = now,
			UpdatedAtUtc = now
		});
		return normalizedName;
	}

	private void RaiseInventoryStateChanged()
	{
		this.InventoryStateChanged?.Invoke(this, EventArgs.Empty);
	}

	private async Task<Dictionary<Guid, string>> BuildItemTrackingMapAsync(CancellationToken ct)
	{
		return await (from item in _db.Items.IgnoreQueryFilters().AsNoTracking()
			where !item.IsDeleted
			select item).ToDictionaryAsync((LocalItem item) => item.Id, (LocalItem item) => ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental), ct);
	}

	private static string ResolveInvoiceLineTrackingType(LocalInvoiceLine line, IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		if (!string.IsNullOrWhiteSpace(line.ItemTrackingType))
		{
			return ItemTrackingTypes.Normalize(line.ItemTrackingType, line.ItemId.HasValue ? "재고" : "비재고");
		}
		if (line.ItemId.HasValue && itemTrackingMap.TryGetValue(line.ItemId.Value, out var value) && !string.IsNullOrWhiteSpace(value))
		{
			return ItemTrackingTypes.Normalize(value);
		}
		return line.ItemId.HasValue ? "재고" : "비재고";
	}

	private async Task<IReadOnlyList<LocalStockShortage>> FindInvoiceStockShortagesAsync(
		LocalInvoice? previousInvoice,
		VoucherType currentVoucherType,
		string currentWarehouseCode,
		IReadOnlyList<LocalInvoiceLine> currentLines,
		IReadOnlyDictionary<Guid, string> itemTrackingMap,
		CancellationToken ct)
	{
		Dictionary<LocalStockChangeKey, decimal> previous = BuildInvoiceStockDeltas(previousInvoice, itemTrackingMap);
		Dictionary<LocalStockChangeKey, decimal> current = BuildInvoiceStockDeltas(currentVoucherType, currentWarehouseCode, currentLines, itemTrackingMap);
		return await FindStockShortagesAsync(previous, current, ct);
	}

	private async Task<IReadOnlyList<LocalStockShortage>> FindTransferStockShortagesAsync(
		LocalInventoryTransfer? previousTransfer,
		string currentFromWarehouseCode,
		string currentToWarehouseCode,
		string currentTransferStatus,
		IReadOnlyList<LocalInventoryTransferLine> currentLines,
		IReadOnlyDictionary<Guid, string> itemTrackingMap,
		CancellationToken ct)
	{
		Dictionary<LocalStockChangeKey, decimal> previous = BuildTransferStockDeltas(previousTransfer, itemTrackingMap);
		Dictionary<LocalStockChangeKey, decimal> current = BuildTransferStockDeltas(currentFromWarehouseCode, currentToWarehouseCode, currentTransferStatus, currentLines, itemTrackingMap);
		return await FindStockShortagesAsync(previous, current, ct);
	}

	private async Task<IReadOnlyList<LocalStockShortage>> FindStockShortagesAsync(
		IReadOnlyDictionary<LocalStockChangeKey, decimal> previous,
		IReadOnlyDictionary<LocalStockChangeKey, decimal> current,
		CancellationToken ct)
	{
		List<LocalStockChangeKey> keys = previous.Keys.Concat(current.Keys).Distinct().ToList();
		if (keys.Count == 0)
		{
			return Array.Empty<LocalStockShortage>();
		}

		var appliedDeltas = keys
			.Select((LocalStockChangeKey key) =>
			{
				previous.TryGetValue(key, out decimal previousQuantity);
				current.TryGetValue(key, out decimal currentQuantity);
				return new
				{
					Key = key,
					Delta = currentQuantity - previousQuantity
				};
			})
			.Where(row => row.Delta < 0m)
			.ToList();
		if (appliedDeltas.Count == 0)
		{
			return Array.Empty<LocalStockShortage>();
		}

		List<Guid> itemIds = appliedDeltas.Select(row => row.Key.ItemId).Distinct().ToList();
		var items = await _db.Items
			.IgnoreQueryFilters()
			.AsNoTracking()
			.Where((LocalItem item) => itemIds.Contains(item.Id) && !item.IsDeleted)
			.Select((LocalItem item) => new
			{
				item.Id,
				item.NameOriginal,
				item.SpecificationOriginal,
				item.TrackingType,
				item.ItemKind,
				item.CategoryName,
				item.IsRental
			})
			.ToDictionaryAsync(item => item.Id, ct);
		List<LocalItemWarehouseStock> stocks = await _db.ItemWarehouseStocks
			.AsNoTracking()
			.Where((LocalItemWarehouseStock stock) => itemIds.Contains(stock.ItemId))
			.ToListAsync(ct);

		List<LocalStockShortage> shortages = new();
		foreach (var row in appliedDeltas)
		{
			if (!items.TryGetValue(row.Key.ItemId, out var item))
			{
				continue;
			}

			string trackingType = ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental);
			if (!ItemOperationalPolicy.SupportsInventory(trackingType))
			{
				continue;
			}

			decimal currentQuantity = stocks
				.Where((LocalItemWarehouseStock stock) => stock.ItemId == row.Key.ItemId && string.Equals(stock.WarehouseCode, row.Key.WarehouseCode, StringComparison.OrdinalIgnoreCase))
				.Select((LocalItemWarehouseStock stock) => stock.Quantity)
				.DefaultIfEmpty(0m)
				.Sum();
			decimal finalQuantity = currentQuantity + row.Delta;
			if (finalQuantity >= 0m)
			{
				continue;
			}

			shortages.Add(new LocalStockShortage(
				row.Key.ItemId,
				row.Key.WarehouseCode,
				item.NameOriginal,
				item.SpecificationOriginal,
				currentQuantity,
				Math.Abs(row.Delta),
				Math.Abs(finalQuantity)));
		}

		return shortages;
	}

	private static Dictionary<LocalStockChangeKey, decimal> BuildTransferStockDeltas(
		LocalInventoryTransfer? transfer,
		IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		if (transfer == null || transfer.IsDeleted)
		{
			return new Dictionary<LocalStockChangeKey, decimal>();
		}

		string fromWarehouseCode = NormalizeWarehouseCode(transfer.FromWarehouseCode, ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode), DomainConstants.OfficeUsenet);
		string toWarehouseCode = NormalizeWarehouseCode(transfer.ToWarehouseCode, ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode), DomainConstants.OfficeYeonsu);
		return BuildTransferStockDeltas(
			fromWarehouseCode,
			toWarehouseCode,
			transfer.TransferStatus,
			transfer.Lines.Where((LocalInventoryTransferLine line) => !line.IsDeleted).ToList(),
			itemTrackingMap);
	}

	private static Dictionary<LocalStockChangeKey, decimal> BuildTransferStockDeltas(
		string fromWarehouseCode,
		string toWarehouseCode,
		string transferStatus,
		IEnumerable<LocalInventoryTransferLine> lines,
		IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		Dictionary<LocalStockChangeKey, decimal> deltas = new();
		if (string.Equals(transferStatus, "반려", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(fromWarehouseCode, toWarehouseCode, StringComparison.OrdinalIgnoreCase))
		{
			return deltas;
		}

		bool isReceived = string.Equals(transferStatus, "수령확정", StringComparison.OrdinalIgnoreCase);
		foreach (LocalInventoryTransferLine line in lines)
		{
			if (!line.ItemId.HasValue || line.ItemId.Value == Guid.Empty || line.Quantity <= 0m || line.IsDeleted)
			{
				continue;
			}

			if (!itemTrackingMap.TryGetValue(line.ItemId.Value, out string? trackingType) ||
				!ItemOperationalPolicy.SupportsInventory(trackingType))
			{
				continue;
			}

			AddStockDelta(deltas, new LocalStockChangeKey(line.ItemId.Value, fromWarehouseCode), -Math.Abs(line.Quantity));
			if (isReceived)
			{
				decimal receivedQuantity = Math.Min(Math.Abs(line.Quantity), Math.Max(0m, line.ReceivedQuantity ?? line.Quantity));
				if (receivedQuantity > 0m)
				{
					AddStockDelta(deltas, new LocalStockChangeKey(line.ItemId.Value, toWarehouseCode), receivedQuantity);
				}
			}
		}

		return deltas;
	}

	private static Dictionary<LocalStockChangeKey, decimal> BuildInvoiceStockDeltas(
		LocalInvoice? invoice,
		IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		if (invoice == null || invoice.IsDeleted || !invoice.IsLatestVersion)
		{
			return new Dictionary<LocalStockChangeKey, decimal>();
		}

		string warehouseCode = NormalizeWarehouseCode(invoice.SourceWarehouseCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode);
		return BuildInvoiceStockDeltas(
			invoice.VoucherType,
			warehouseCode,
			invoice.PurchaseReceivingStatus,
			invoice.Lines.Where((LocalInvoiceLine line) => !line.IsDeleted).ToList(),
			itemTrackingMap);
	}

	private static Dictionary<LocalStockChangeKey, decimal> BuildInvoiceStockDeltas(
		VoucherType voucherType,
		string warehouseCode,
		IEnumerable<LocalInvoiceLine> lines,
		IReadOnlyDictionary<Guid, string> itemTrackingMap)
		=> BuildInvoiceStockDeltas(voucherType, warehouseCode, null, lines, itemTrackingMap);

	private static Dictionary<LocalStockChangeKey, decimal> BuildInvoiceStockDeltas(
		VoucherType voucherType,
		string warehouseCode,
		string? purchaseReceivingStatus,
		IEnumerable<LocalInvoiceLine> lines,
		IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		Dictionary<LocalStockChangeKey, decimal> deltas = new();
		if (voucherType is not (VoucherType.Sales or VoucherType.Purchase or VoucherType.Procurement))
		{
			return deltas;
		}
		if (voucherType == VoucherType.Purchase &&
			!InvoiceReceivingStatuses.IsConfirmed(purchaseReceivingStatus))
		{
			return deltas;
		}

		foreach (LocalInvoiceLine line in lines)
		{
			if (!line.ItemId.HasValue || line.ItemId.Value == Guid.Empty || line.Quantity == 0m || line.IsDeleted)
			{
				continue;
			}

			string trackingType = ResolveInvoiceLineTrackingType(line, itemTrackingMap);
			if (!ItemOperationalPolicy.SupportsInventory(trackingType))
			{
				continue;
			}

			decimal quantity = Math.Abs(line.Quantity);
			decimal signedQuantity = voucherType == VoucherType.Sales ? -quantity : quantity;
			if (signedQuantity == 0m)
			{
				continue;
			}

			LocalStockChangeKey key = new(line.ItemId.Value, warehouseCode);
			deltas[key] = deltas.TryGetValue(key, out decimal current)
				? current + signedQuantity
				: signedQuantity;
		}

		return deltas;
	}

	private static void AddStockDelta(IDictionary<LocalStockChangeKey, decimal> deltas, LocalStockChangeKey key, decimal delta)
	{
		if (delta == 0m)
		{
			return;
		}

		deltas[key] = deltas.TryGetValue(key, out decimal current)
			? current + delta
			: delta;
	}

	private static string FormatInvoiceStockShortageMessage(IReadOnlyList<LocalStockShortage> shortages)
		=> FormatStockShortageMessage("재고가 부족하여 판매/전표 변경을 저장할 수 없습니다.", shortages);

	private static string FormatStockShortageMessage(string prefix, IReadOnlyList<LocalStockShortage> shortages)
	{
		if (shortages.Count == 0)
		{
			return string.Empty;
		}

		IEnumerable<string> rows = shortages
			.Take(3)
			.Select((LocalStockShortage shortage) =>
			{
				string specification = string.IsNullOrWhiteSpace(shortage.Specification) ? string.Empty : $" / 규격 {shortage.Specification}";
				return $"{shortage.ItemName}{specification} / 창고 {shortage.WarehouseCode} / 현재 {shortage.CurrentQuantity:N0} / 차감 {shortage.RequestedDecrease:N0} / 부족 {shortage.ShortageQuantity:N0}";
			});
		string suffix = shortages.Count > 3 ? $" 외 {shortages.Count - 3:N0}건" : string.Empty;
		return prefix.TrimEnd() + " " + string.Join("; ", rows) + suffix;
	}

	private void ApplyInventoryResetToZeroEntry(LocalInventoryMovement reset, IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
	{
		if (!reset.ItemId.HasValue || !reset.IsActive)
		{
			return;
		}

		Guid itemId = reset.ItemId.Value;
		string warehouseCode = NormalizeWarehouseCode(
			reset.WarehouseCode,
			ResolveOfficeCodeFromWarehouseCode(reset.WarehouseCode),
			DomainConstants.OfficeUsenet);
		reset.WarehouseCode = warehouseCode;
		(Guid, string) key = (itemId, warehouseCode);
		EnsureStockKey(stockMap, key);

		decimal currentQuantity = stockMap[key];
		foreach (LocalStockLayer layer in EnsureLayerList(layerMap, key))
		{
			layer.RemainingQuantity = 0m;
		}

		stockMap[key] = 0m;
		reset.QuantityDelta = -currentQuantity;
		reset.Amount = Math.Round(Math.Abs(currentQuantity) * Math.Max(0m, reset.UnitCost), 2, MidpointRounding.AwayFromZero);
		_db.InventoryMovements.Add(reset);
	}

	private void ApplyManualStockAdjustmentEntry(LocalInventoryMovement adjustment, IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
	{
		if (!adjustment.ItemId.HasValue || adjustment.QuantityDelta == 0m || !adjustment.IsActive)
		{
			return;
		}

		Guid itemId = adjustment.ItemId.Value;
		string warehouseCode = NormalizeWarehouseCode(
			adjustment.WarehouseCode,
			ResolveOfficeCodeFromWarehouseCode(adjustment.WarehouseCode),
			DomainConstants.OfficeUsenet);
		adjustment.WarehouseCode = warehouseCode;
		(Guid, string) key = (itemId, warehouseCode);
		EnsureStockKey(stockMap, key);

		if (adjustment.QuantityDelta > 0m)
		{
			stockMap[key] += adjustment.QuantityDelta;
			var layer = new LocalStockLayer
			{
				Id = Guid.NewGuid(),
				ItemId = itemId,
				WarehouseCode = warehouseCode,
				SourceInvoiceId = null,
				SourceInvoiceLineId = null,
				ReceiptDate = adjustment.OccurredDate,
				UnitCost = Math.Max(0m, adjustment.UnitCost),
				OriginalQuantity = adjustment.QuantityDelta,
				RemainingQuantity = adjustment.QuantityDelta,
				IsNegativePlaceholder = adjustment.UnitCost <= 0m,
				CreatedAtUtc = adjustment.CreatedAtUtc == default(DateTime) ? DateTime.UtcNow : adjustment.CreatedAtUtc
			};
			_db.StockLayers.Add(layer);
			EnsureLayerList(layerMap, key).Add(layer);
		}
		else
		{
			decimal remaining = Math.Abs(adjustment.QuantityDelta);
			foreach (LocalStockLayer layer in EnsureLayerList(layerMap, key)
				         .Where((LocalStockLayer currentLayer) => currentLayer.RemainingQuantity > 0m)
				         .OrderBy((LocalStockLayer currentLayer) => currentLayer.ReceiptDate)
				         .ThenBy((LocalStockLayer currentLayer) => currentLayer.CreatedAtUtc)
				         .ToList())
			{
				if (remaining <= 0m)
				{
					break;
				}

				decimal consumed = Math.Min(layer.RemainingQuantity, remaining);
				layer.RemainingQuantity -= consumed;
				remaining -= consumed;
			}

			stockMap[key] -= Math.Abs(adjustment.QuantityDelta);
		}

		_db.InventoryMovements.Add(adjustment);
	}

	private void ApplyInvoiceInventoryEntry(LocalInvoice invoice, InvoiceSaveContext context, IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap, IDictionary<string, LocalSerialLedger> serialMap, IReadOnlyDictionary<Guid, string> itemTrackingMap)
	{
		string text = NormalizeWarehouseCode(invoice.SourceWarehouseCode, invoice.ResponsibleOfficeCode, context.OfficeCode);
		bool flag = false;
		VoucherType voucherType;
		foreach (LocalInvoiceLine line in invoice.Lines)
		{
			if (!line.ItemId.HasValue)
			{
				continue;
			}
			string text2 = ResolveInvoiceLineTrackingType(line, itemTrackingMap);
			if (!string.Equals(line.ItemTrackingType, text2, StringComparison.Ordinal))
			{
				line.ItemTrackingType = text2;
			}
			if (!ItemOperationalPolicy.SupportsInventory(text2))
			{
				continue;
			}
			decimal num = Math.Abs(line.Quantity);
			if (num <= 0m)
			{
				continue;
			}
			Guid value = line.ItemId.Value;
			(Guid, string) key = (value, text);
			EnsureStockKey(stockMap, key);
			List<string> list = ParseSerialTokens(line.SerialNumber);
			foreach (string item in list)
			{
				_db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
				{
					Id = Guid.NewGuid(),
					InvoiceId = invoice.Id,
					InvoiceLineId = line.Id,
					ItemId = value,
					SerialNumber = item
				});
			}
			voucherType = invoice.VoucherType;
			if (voucherType == VoucherType.Purchase &&
				!InvoiceReceivingStatuses.IsConfirmed(invoice.PurchaseReceivingStatus))
			{
				continue;
			}
			if ((uint)(voucherType - 1) <= 1u)
			{
				decimal num2 = ResolveUnitCost(line);
				LocalStockLayer localStockLayer = new LocalStockLayer
				{
					Id = Guid.NewGuid(),
					ItemId = value,
					WarehouseCode = text,
					SourceInvoiceId = invoice.Id,
					SourceInvoiceLineId = line.Id,
					ReceiptDate = invoice.InvoiceDate,
					UnitCost = num2,
					OriginalQuantity = num,
					RemainingQuantity = num,
					IsNegativePlaceholder = false,
					CreatedAtUtc = ((invoice.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : invoice.LastSavedAtUtc)
				};
				_db.StockLayers.Add(localStockLayer);
				EnsureLayerList(layerMap, key).Add(localStockLayer);
				stockMap[key] += num;
				_db.InventoryMovements.Add(new LocalInventoryMovement
				{
					Id = Guid.NewGuid(),
					InvoiceId = invoice.Id,
					InvoiceLineId = line.Id,
					ItemId = value,
					WarehouseCode = text,
					MovementType = "PurchaseIn",
					QuantityDelta = num,
					UnitCost = num2,
					Amount = Math.Round(num * num2, 2, MidpointRounding.AwayFromZero),
					OccurredDate = invoice.InvoiceDate,
					IsSettledCost = true,
					IsActive = true,
					Note = line.ItemNameOriginal,
					CreatedByUsername = invoice.LastSavedByUsername,
					CreatedAtUtc = ((invoice.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : invoice.LastSavedAtUtc)
				});
				foreach (string item2 in list)
				{
					LocalSerialLedger orCreateSerialLedger = GetOrCreateSerialLedger(serialMap, item2);
					orCreateSerialLedger.ItemId = value;
					orCreateSerialLedger.WarehouseCode = text;
					orCreateSerialLedger.Status = "InStock";
					orCreateSerialLedger.SourcePurchaseInvoiceId = invoice.Id;
					orCreateSerialLedger.LastInvoiceId = invoice.Id;
					orCreateSerialLedger.LastMovementType = "IN";
					orCreateSerialLedger.SourceSalesInvoiceId = null;
					orCreateSerialLedger.UpdatedAtUtc = DateTime.UtcNow;
				}
			}
			else
			{
				if (invoice.VoucherType != VoucherType.Sales)
				{
					continue;
				}
				decimal num3 = num;
				decimal num4 = num3;
				bool flag2 = true;
				List<LocalStockLayer> source = EnsureLayerList(layerMap, key);
				if (string.Equals(text, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
				{
					decimal num5 = source.Where((LocalStockLayer existingLayer) => existingLayer.RemainingQuantity > 0m).Sum((LocalStockLayer existingLayer) => existingLayer.RemainingQuantity);
					if (num5 < num4)
					{
						decimal requiredQuantity = num4 - num5;
						AutoTransferFromUsenetToYeonsu(invoice, line, value, requiredQuantity, stockMap, layerMap);
						source = EnsureLayerList(layerMap, key);
					}
				}
				foreach (LocalStockLayer item3 in (from existingLayer in source
					where existingLayer.RemainingQuantity > 0m
					orderby existingLayer.ReceiptDate, existingLayer.CreatedAtUtc
					select existingLayer).ToList())
				{
					if (num4 <= 0m)
					{
						break;
					}
					decimal num6 = Math.Min(item3.RemainingQuantity, num4);
					if (!(num6 <= 0m))
					{
						item3.RemainingQuantity -= num6;
						num4 -= num6;
						_db.CostAllocations.Add(new LocalCostAllocation
						{
							Id = Guid.NewGuid(),
							SalesInvoiceId = invoice.Id,
							SalesInvoiceLineId = line.Id,
							PurchaseInvoiceId = item3.SourceInvoiceId,
							PurchaseInvoiceLineId = item3.SourceInvoiceLineId,
							WarehouseCode = text,
							Quantity = num6,
							UnitCost = item3.UnitCost,
							CostAmount = Math.Round(num6 * item3.UnitCost, 2, MidpointRounding.AwayFromZero),
							IsUnsettled = false,
							Note = line.ItemNameOriginal,
							CreatedAtUtc = DateTime.UtcNow
						});
						_db.InventoryMovements.Add(new LocalInventoryMovement
						{
							Id = Guid.NewGuid(),
							InvoiceId = invoice.Id,
							InvoiceLineId = line.Id,
							ItemId = value,
							WarehouseCode = text,
							MovementType = "SalesOut",
							QuantityDelta = -num6,
							UnitCost = item3.UnitCost,
							Amount = Math.Round(num6 * item3.UnitCost, 2, MidpointRounding.AwayFromZero),
							OccurredDate = invoice.InvoiceDate,
							IsSettledCost = true,
							IsActive = true,
							Note = line.ItemNameOriginal,
							CreatedByUsername = invoice.LastSavedByUsername,
							CreatedAtUtc = ((invoice.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : invoice.LastSavedAtUtc)
						});
					}
				}
				if (num4 > 0m)
				{
					flag2 = false;
					flag = true;
					_db.CostAllocations.Add(new LocalCostAllocation
					{
						Id = Guid.NewGuid(),
						SalesInvoiceId = invoice.Id,
						SalesInvoiceLineId = line.Id,
						PurchaseInvoiceId = null,
						PurchaseInvoiceLineId = null,
						WarehouseCode = text,
						Quantity = num4,
						UnitCost = 0m,
						CostAmount = 0m,
						IsUnsettled = true,
						Note = "재고 부족(마이너스)로 인한 미정산",
						CreatedAtUtc = DateTime.UtcNow
					});
					_db.InventoryMovements.Add(new LocalInventoryMovement
					{
						Id = Guid.NewGuid(),
						InvoiceId = invoice.Id,
						InvoiceLineId = line.Id,
						ItemId = value,
						WarehouseCode = text,
						MovementType = "SalesOut",
						QuantityDelta = -num4,
						UnitCost = 0m,
						Amount = 0m,
						OccurredDate = invoice.InvoiceDate,
						IsSettledCost = false,
						IsActive = true,
						Note = "재고 부족(마이너스)",
						CreatedByUsername = invoice.LastSavedByUsername,
						CreatedAtUtc = ((invoice.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : invoice.LastSavedAtUtc)
					});
				}
				stockMap[key] -= num3;
				foreach (string item4 in list)
				{
					LocalSerialLedger orCreateSerialLedger2 = GetOrCreateSerialLedger(serialMap, item4);
					LocalSerialLedger localSerialLedger = orCreateSerialLedger2;
					Guid? itemId = localSerialLedger.ItemId;
					Guid valueOrDefault = itemId.GetValueOrDefault();
					if (!itemId.HasValue)
					{
						valueOrDefault = value;
						Guid? guid = (localSerialLedger.ItemId = valueOrDefault);
					}
					orCreateSerialLedger2.WarehouseCode = text;
					orCreateSerialLedger2.Status = (flag2 ? "Outbound" : "PendingInboundOutbound");
					orCreateSerialLedger2.SourceSalesInvoiceId = invoice.Id;
					orCreateSerialLedger2.LastInvoiceId = invoice.Id;
					orCreateSerialLedger2.LastMovementType = "OUT";
					orCreateSerialLedger2.UpdatedAtUtc = DateTime.UtcNow;
				}
			}
		}
		if (invoice.VoucherType == VoucherType.Sales)
		{
			invoice.CostStatus = (flag ? "Unsettled" : "Settled");
			return;
		}
		voucherType = invoice.VoucherType;
		if ((uint)(voucherType - 1) <= 1u)
		{
			invoice.CostStatus = "Settled";
		}
	}

	private void ApplyInventoryTransferEntry(LocalInventoryTransfer transfer, IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
	{
		string text = NormalizeWarehouseCode(transfer.FromWarehouseCode, DomainConstants.OfficeUsenet, DomainConstants.OfficeUsenet);
		string text2 = NormalizeWarehouseCode(transfer.ToWarehouseCode, DomainConstants.OfficeYeonsu, DomainConstants.OfficeYeonsu);
		if (string.Equals(text, text2, StringComparison.OrdinalIgnoreCase) || string.Equals(transfer.TransferStatus, "반려", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		bool flag = string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase);
		foreach (LocalInventoryTransferLine line in transfer.Lines)
		{
			if (!line.ItemId.HasValue || line.Quantity <= 0m)
			{
				continue;
			}
			Guid value = line.ItemId.Value;
			(Guid, string) key = (value, text);
			(Guid, string) key2 = (value, text2);
			EnsureStockKey(stockMap, key);
			EnsureStockKey(stockMap, key2);
			List<LocalStockLayer> source = EnsureLayerList(layerMap, key);
			List<LocalStockLayer> list = EnsureLayerList(layerMap, key2);
			decimal quantity = line.Quantity;
			decimal num = (flag ? Math.Min(line.Quantity, Math.Max(0m, line.ReceivedQuantity ?? line.Quantity)) : 0m);
			DateTime createdAtUtc = ((transfer.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : transfer.LastSavedAtUtc);
			string text3 = (string.IsNullOrWhiteSpace(line.Remark) ? (line.ItemNameOriginal + " (" + transfer.TransferNumber + ")") : (line.ItemNameOriginal + " | " + line.Remark));
			foreach (LocalStockLayer item in (from layer in source
				where layer.RemainingQuantity > 0m
				orderby layer.ReceiptDate, layer.CreatedAtUtc
				select layer).ToList())
			{
				if (quantity <= 0m)
				{
					break;
				}
				decimal num2 = Math.Min(item.RemainingQuantity, quantity);
				if (!(num2 <= 0m))
				{
					item.RemainingQuantity -= num2;
					quantity -= num2;
					stockMap[key] -= num2;
					decimal amount = Math.Round(num2 * item.UnitCost, 2, MidpointRounding.AwayFromZero);
					AddInventoryTransferOutMovement(transfer, value, text, num2, item.UnitCost, amount, isSettledCost: true, text3, createdAtUtc);
					if (flag && !(num <= 0m))
					{
						decimal num3 = Math.Min(num2, num);
						num -= num3;
						stockMap[key2] += num3;
						LocalStockLayer localStockLayer = new LocalStockLayer
						{
							Id = Guid.NewGuid(),
							ItemId = value,
							WarehouseCode = text2,
							SourceInvoiceId = item.SourceInvoiceId,
							SourceInvoiceLineId = item.SourceInvoiceLineId,
							ReceiptDate = transfer.TransferDate,
							UnitCost = item.UnitCost,
							OriginalQuantity = num3,
							RemainingQuantity = num3,
							IsNegativePlaceholder = false,
							CreatedAtUtc = createdAtUtc
						};
						_db.StockLayers.Add(localStockLayer);
						list.Add(localStockLayer);
						AddInventoryTransferInMovement(transfer, value, text2, num3, item.UnitCost, Math.Round(num3 * item.UnitCost, 2, MidpointRounding.AwayFromZero), isSettledCost: true, text3, createdAtUtc);
					}
				}
			}
			if (quantity > 0m)
			{
				stockMap[key] -= quantity;
				AddInventoryTransferOutMovement(transfer, value, text, quantity, 0m, 0m, isSettledCost: false, text3 + " | 재고 부족 이동", createdAtUtc);
			}
			if (flag && num > 0m)
			{
				stockMap[key2] += num;
				LocalStockLayer localStockLayer2 = new LocalStockLayer
				{
					Id = Guid.NewGuid(),
					ItemId = value,
					WarehouseCode = text2,
					SourceInvoiceId = null,
					SourceInvoiceLineId = null,
					ReceiptDate = transfer.TransferDate,
					UnitCost = 0m,
					OriginalQuantity = num,
					RemainingQuantity = num,
					IsNegativePlaceholder = true,
					CreatedAtUtc = createdAtUtc
				};
				_db.StockLayers.Add(localStockLayer2);
				list.Add(localStockLayer2);
				AddInventoryTransferInMovement(transfer, value, text2, num, 0m, 0m, isSettledCost: false, text3 + " | 수령확정 차이/미정산", createdAtUtc);
			}
		}
	}

	private void AddInventoryTransferOutMovement(LocalInventoryTransfer transfer, Guid itemId, string fromWarehouseCode, decimal quantity, decimal unitCost, decimal amount, bool isSettledCost, string note, DateTime createdAtUtc)
	{
		_db.InventoryMovements.Add(new LocalInventoryMovement
		{
			Id = Guid.NewGuid(),
			ItemId = itemId,
			WarehouseCode = fromWarehouseCode,
			MovementType = "TransferOutManual",
			QuantityDelta = -quantity,
			UnitCost = unitCost,
			Amount = amount,
			OccurredDate = transfer.TransferDate,
			IsSettledCost = isSettledCost,
			IsActive = true,
			Note = note,
			CreatedByUsername = transfer.LastSavedByUsername,
			CreatedAtUtc = createdAtUtc
		});
	}

	private void AddInventoryTransferInMovement(LocalInventoryTransfer transfer, Guid itemId, string toWarehouseCode, decimal quantity, decimal unitCost, decimal amount, bool isSettledCost, string note, DateTime createdAtUtc)
	{
		_db.InventoryMovements.Add(new LocalInventoryMovement
		{
			Id = Guid.NewGuid(),
			ItemId = itemId,
			WarehouseCode = toWarehouseCode,
			MovementType = "TransferInManual",
			QuantityDelta = quantity,
			UnitCost = unitCost,
			Amount = amount,
			OccurredDate = transfer.TransferDate,
			IsSettledCost = isSettledCost,
			IsActive = true,
			Note = note,
			CreatedByUsername = transfer.LastSavedByUsername,
			CreatedAtUtc = createdAtUtc
		});
	}

	private static void EnsureStockKey(IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, (Guid ItemId, string WarehouseCode) key)
	{
		if (!stockMap.ContainsKey(key))
		{
			stockMap[key] = 0m;
		}
	}

	private static List<LocalStockLayer> EnsureLayerList(IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap, (Guid ItemId, string WarehouseCode) key)
	{
		if (!layerMap.TryGetValue(key, out var value))
		{
			value = (layerMap[key] = new List<LocalStockLayer>());
		}
		return value;
	}

	private static decimal ResolveUnitCost(LocalInvoiceLine line)
	{
		decimal num = Math.Abs(line.Quantity);
		if (num <= 0m)
		{
			return line.UnitPrice;
		}
		if (line.LineAmount > 0m)
		{
			return Math.Round(line.LineAmount / num, 4, MidpointRounding.AwayFromZero);
		}
		return line.UnitPrice;
	}

	private void AutoTransferFromUsenetToYeonsu(LocalInvoice salesInvoice, LocalInvoiceLine salesLine, Guid itemId, decimal requiredQuantity, IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap, IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
	{
		if (requiredQuantity <= 0m)
		{
			return;
		}
		string warehouseUsenetMain = DomainConstants.WarehouseUsenetMain;
		string warehouseYeonsuMain = DomainConstants.WarehouseYeonsuMain;
		(Guid, string) key = (itemId, warehouseUsenetMain);
		(Guid, string) key2 = (itemId, warehouseYeonsuMain);
		if (!layerMap.TryGetValue(key, out var value))
		{
			return;
		}
		if (!layerMap.TryGetValue(key2, out var value2))
		{
			value2 = (layerMap[key2] = new List<LocalStockLayer>());
		}
		if (!stockMap.ContainsKey(key))
		{
			stockMap[key] = 0m;
		}
		if (!stockMap.ContainsKey(key2))
		{
			stockMap[key2] = 0m;
		}
		decimal num = Math.Max(stockMap[key], 0m);
		if (num <= 0m)
		{
			return;
		}
		List<LocalStockLayer> list2 = (from layer in value
			where layer.RemainingQuantity > 0m
			orderby layer.ReceiptDate, layer.CreatedAtUtc
			select layer).ToList();
		decimal num2 = Math.Min(requiredQuantity, num);
		DateTime createdAtUtc = ((salesInvoice.LastSavedAtUtc == default(DateTime)) ? DateTime.UtcNow : salesInvoice.LastSavedAtUtc);
		string text = (string.IsNullOrWhiteSpace(salesInvoice.InvoiceNumber) ? salesInvoice.LocalTempNumber : salesInvoice.InvoiceNumber);
		foreach (LocalStockLayer item in list2)
		{
			if (num2 <= 0m)
			{
				break;
			}
			decimal num3 = Math.Min(item.RemainingQuantity, num2);
			if (!(num3 <= 0m))
			{
				item.RemainingQuantity -= num3;
				num2 -= num3;
				LocalStockLayer localStockLayer = new LocalStockLayer
				{
					Id = Guid.NewGuid(),
					ItemId = itemId,
					WarehouseCode = warehouseYeonsuMain,
					SourceInvoiceId = item.SourceInvoiceId,
					SourceInvoiceLineId = item.SourceInvoiceLineId,
					ReceiptDate = salesInvoice.InvoiceDate,
					UnitCost = item.UnitCost,
					OriginalQuantity = num3,
					RemainingQuantity = num3,
					IsNegativePlaceholder = false,
					CreatedAtUtc = createdAtUtc
				};
				_db.StockLayers.Add(localStockLayer);
				value2.Add(localStockLayer);
				stockMap[key] -= num3;
				stockMap[key2] += num3;
				decimal amount = Math.Round(num3 * item.UnitCost, 2, MidpointRounding.AwayFromZero);
				string note = (string.IsNullOrWhiteSpace(text) ? "연수구 자동 재고이동" : ("연수구 자동 재고이동 (" + text + ")"));
				_db.InventoryMovements.Add(new LocalInventoryMovement
				{
					Id = Guid.NewGuid(),
					InvoiceId = salesInvoice.Id,
					InvoiceLineId = salesLine.Id,
					ItemId = itemId,
					WarehouseCode = warehouseUsenetMain,
					MovementType = "TransferOutAuto",
					QuantityDelta = -num3,
					UnitCost = item.UnitCost,
					Amount = amount,
					OccurredDate = salesInvoice.InvoiceDate,
					IsSettledCost = true,
					IsActive = true,
					Note = note,
					CreatedByUsername = salesInvoice.LastSavedByUsername,
					CreatedAtUtc = createdAtUtc
				});
				_db.InventoryMovements.Add(new LocalInventoryMovement
				{
					Id = Guid.NewGuid(),
					InvoiceId = salesInvoice.Id,
					InvoiceLineId = salesLine.Id,
					ItemId = itemId,
					WarehouseCode = warehouseYeonsuMain,
					MovementType = "TransferInAuto",
					QuantityDelta = num3,
					UnitCost = item.UnitCost,
					Amount = amount,
					OccurredDate = salesInvoice.InvoiceDate,
					IsSettledCost = true,
					IsActive = true,
					Note = note,
					CreatedByUsername = salesInvoice.LastSavedByUsername,
					CreatedAtUtc = createdAtUtc
				});
			}
		}
	}

	private static LocalSerialLedger GetOrCreateSerialLedger(IDictionary<string, LocalSerialLedger> serialMap, string serialNumber)
	{
		if (serialMap.TryGetValue(serialNumber, out var value))
		{
			return value;
		}
		return serialMap[serialNumber] = new LocalSerialLedger
		{
			Id = Guid.NewGuid(),
			SerialNumber = serialNumber,
			WarehouseCode = string.Empty,
			Status = "Unknown",
			LastMovementType = string.Empty,
			UpdatedAtUtc = DateTime.UtcNow
		};
	}

	private static List<string> ParseSerialTokens(string? serialText)
	{
		if (string.IsNullOrWhiteSpace(serialText))
		{
			return new List<string>();
		}
		return (from token in serialText.Split(new char[7] { ',', ';', '|', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
			select token.Trim() into token
			where token.Length > 0
			select token).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string NormalizeUsername(string? username)
	{
		return (username ?? string.Empty).Trim().ToLowerInvariant();
	}

	private static string GetCompanyProfileAssignmentKey(string username)
	{
		return "CompanyProfile.Assigned." + NormalizeUsername(username);
	}

}
