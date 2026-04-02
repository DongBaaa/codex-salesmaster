using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class RentalStateService
{
    public async Task<RentalWorkbookAuditResult> AuditAssetWorkbookAsync(string path, CancellationToken ct = default)
    {
        var context = await BuildWorkbookAuditContextAsync(path, ct);
        return context.Result;
    }

    public async Task<RentalWorkbookRebuildResult> RebuildAssetsFromWorkbookAsync(
        string path,
        SessionState session,
        CancellationToken ct = default)
    {
        var backupPath = CreateLocalDbBackup("before-rental-workbook-rebuild");
        var context = await BuildWorkbookAuditContextAsync(path, ct);
        var now = DateTime.UtcNow;
        var repairResult = new RentalCatalogRepairResult();
        var rebuildResult = new RentalWorkbookRebuildResult
        {
            WorkbookPath = path,
            BackupPath = backupPath,
            ProcessedAtUtc = now,
            MissingInWorkbookAssets = context.Result.MissingInWorkbookAssets,
            MissingInWorkbookCount = context.Result.MissingInWorkbookCount
        };

        await AssetSaveLock.WaitAsync(ct);
        try
        {
            var activeItems = await GetActiveItemsAsync(ct);
            var rebuildOperations = new List<RentalWorkbookRebuildOperation>();

            foreach (var entry in context.Result.Entries.OrderBy(current => current.RowNumber))
            {
                var row = context.RowsByRowNumber[entry.RowNumber];
                switch (entry.Action)
                {
                    case "ExactMatch":
                        continue;
                    case "Ambiguous":
                        rebuildResult.AmbiguousCount++;
                        rebuildResult.AmbiguousEntries.Add(CloneAuditEntry(entry));
                        continue;
                    case "CreateNew":
                    case "UpdateSafe":
                        break;
                    default:
                        continue;
                }

                LocalRentalAsset asset;
                var isCreate = !entry.ExistingAssetId.HasValue || entry.ExistingAssetId.Value == Guid.Empty;
                if (entry.ExistingAssetId.HasValue && entry.ExistingAssetId.Value != Guid.Empty)
                {
                    asset = await _db.RentalAssets.IgnoreQueryFilters()
                        .FirstAsync(current => current.Id == entry.ExistingAssetId.Value, ct);
                }
                else
                {
                    asset = new LocalRentalAsset
                    {
                        Id = Guid.NewGuid(),
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        IsDeleted = false,
                        IsDirty = true
                    };
                }

                rebuildOperations.Add(new RentalWorkbookRebuildOperation(
                    entry,
                    row,
                    asset,
                    isCreate,
                    asset.ManagementId,
                    asset.ManagementNumber));
            }

            ValidateRebuildTargets(rebuildOperations, rebuildResult);

            var executableOperations = rebuildOperations
                .Where(current => !rebuildResult.AmbiguousEntries.Any(entry => entry.RowNumber == current.Entry.RowNumber))
                .ToList();

            await ReserveRentalAssetUniqueValuesAsync(executableOperations, now, ct);

            var touchedAssets = new List<LocalRentalAsset>();
            try
            {
                foreach (var operation in executableOperations.OrderBy(current => current.Entry.RowNumber))
                {
                    if (operation.IsCreate)
                    {
                        _db.RentalAssets.Add(operation.Asset);
                        rebuildResult.CreatedCount++;
                    }
                    else
                    {
                        rebuildResult.UpdatedCount++;
                    }

                    ApplyWorkbookRowToAsset(operation.Asset, operation.Row, session, now);
                    operation.Asset.CustomerId = await ResolveCustomerIdAsync(operation.Row.CustomerName, operation.Row.CustomerBusinessNumber, ct);
                    await EnrichAssetReferencesAsync(operation.Asset, ct, repairResult, activeItems, allowCategoryRecovery: false, allowDerivedAssetBackfill: false);
                    operation.Asset.BillingProfileId = await FindMatchingBillingProfileIdAsync(operation.Asset, ct);
                    if (operation.Asset.BillingProfileId.HasValue && operation.Asset.BillingProfileId.Value != Guid.Empty)
                        rebuildResult.LinkedBillingProfileCount++;

                    touchedAssets.Add(operation.Asset);
                    rebuildResult.UpdatedEntries.Add(CloneAuditEntry(operation.Entry));
                }

                var activeAssets = await _db.RentalAssets.IgnoreQueryFilters()
                    .Where(asset => !asset.IsDeleted)
                    .ToListAsync(ct);
                var assetBaseKeyById = activeAssets.ToDictionary(
                    asset => asset.Id,
                    asset => BuildAssetKey(asset.ManagementCompanyCode, asset.ManagementNumber, asset.ManagementId, asset.MachineNumber, asset.CustomerName, asset.ItemName));
                AssignUniqueAssetKeysForRepair(activeAssets, assetBaseKeyById);

                await _db.SaveChangesAsync(ct);
            }
            catch
            {
                await RestoreReservedRentalAssetUniqueValuesAsync(executableOperations, ct);
                throw;
            }

            rebuildResult.AutoCreatedCategoryCount = repairResult.AddedCategoryCount;
            rebuildResult.AutoCreatedItemCount = repairResult.AddedItemCount;
            rebuildResult.LinkedBillingProfileCount = touchedAssets.Count(asset => asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty);
            return rebuildResult;
        }
        finally
        {
            AssetSaveLock.Release();
        }
    }

    private async Task<WorkbookAuditContext> BuildWorkbookAuditContextAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("렌탈 자산 기준 workbook 파일을 찾을 수 없습니다.", path);

        var workbook = ReadWorkbook(path);
        var customerBusinessNumberByName = ReadWorkbookCustomerBusinessNumberMap(workbook);
        var rows = ReadWorkbookAssetRows(workbook, customerBusinessNumberByName);
        var assets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted)
            .OrderBy(asset => asset.ManagementCompanyCode)
            .ThenBy(asset => asset.ManagementNumber)
            .ThenBy(asset => asset.MachineNumber)
            .ToListAsync(ct);

        var entries = new List<RentalWorkbookAuditEntry>(rows.Count);
        var rowsByNumber = new Dictionary<int, WorkbookRentalAssetRow>();
        var matchedAssetIds = new HashSet<Guid>();
        var unresolvedCustomerCount = 0;

        foreach (var row in rows)
        {
            rowsByNumber[row.RowNumber] = row;
            var (matchedAsset, matchedBy, ambiguous, warnings) = MatchWorkbookRowToAsset(row, assets);
            var differences = matchedAsset is null ? new List<string>() : BuildWorkbookDifferences(row, matchedAsset);
            var resolvedCustomerId = await ResolveCustomerIdAsync(row.CustomerName, row.CustomerBusinessNumber, ct);
            if (!string.IsNullOrWhiteSpace(row.CustomerName) && (!resolvedCustomerId.HasValue || resolvedCustomerId.Value == Guid.Empty))
            {
                warnings.Add($"고객 마스터를 찾지 못했습니다: {row.CustomerName}");
                unresolvedCustomerCount++;
            }

            var action = DetermineWorkbookAction(row, matchedAsset, ambiguous, differences);
            if (matchedAsset is not null && !ambiguous)
                matchedAssetIds.Add(matchedAsset.Id);

            entries.Add(new RentalWorkbookAuditEntry
            {
                RowNumber = row.RowNumber,
                Action = action,
                MatchedBy = matchedBy,
                ExistingAssetId = ambiguous ? null : matchedAsset?.Id,
                ExistingManagementNumber = ambiguous ? string.Empty : matchedAsset?.ManagementNumber ?? string.Empty,
                WorkbookManagementNumber = row.ManagementNumber,
                WorkbookManagementId = row.ManagementId,
                WorkbookOfficeCode = row.OfficeCode,
                WorkbookCustomerName = row.CustomerName,
                WorkbookItemName = row.ItemName,
                WorkbookMachineNumber = row.MachineNumber,
                WorkbookInstallLocation = row.InstallLocation,
                Differences = differences,
                Warnings = warnings
            });
        }

        var missingAssets = assets
            .Where(asset => !matchedAssetIds.Contains(asset.Id))
            .Select(asset => new RentalWorkbookMissingAssetEntry
            {
                AssetId = asset.Id,
                ManagementNumber = asset.ManagementNumber,
                ManagementId = asset.ManagementId,
                OfficeCode = NormalizeOfficeCode(asset.ManagementCompanyCode, asset.ResponsibleOfficeCode),
                CustomerName = asset.CustomerName,
                ItemName = asset.ItemName,
                MachineNumber = asset.MachineNumber,
                InstallLocation = asset.InstallLocation
            })
            .OrderBy(asset => asset.OfficeCode)
            .ThenBy(asset => asset.ManagementNumber)
            .ThenBy(asset => asset.MachineNumber)
            .ToList();

        var result = new RentalWorkbookAuditResult
        {
            WorkbookPath = path,
            SheetName = "렌탈재고관리",
            GeneratedAtUtc = DateTime.UtcNow,
            ProcessedRowCount = rows.Count,
            ExactMatchCount = entries.Count(entry => string.Equals(entry.Action, "ExactMatch", StringComparison.Ordinal)),
            UpdateSafeCount = entries.Count(entry => string.Equals(entry.Action, "UpdateSafe", StringComparison.Ordinal)),
            CreateNewCount = entries.Count(entry => string.Equals(entry.Action, "CreateNew", StringComparison.Ordinal)),
            AmbiguousCount = entries.Count(entry => string.Equals(entry.Action, "Ambiguous", StringComparison.Ordinal)),
            MissingInWorkbookCount = missingAssets.Count,
            UnresolvedCustomerCount = unresolvedCustomerCount,
            Entries = entries,
            MissingInWorkbookAssets = missingAssets
        };

        return new WorkbookAuditContext(result, rowsByNumber);
    }

    private static string DetermineWorkbookAction(
        WorkbookRentalAssetRow row,
        LocalRentalAsset? matchedAsset,
        bool ambiguous,
        IReadOnlyCollection<string> differences)
    {
        if (ambiguous)
            return "Ambiguous";

        if (matchedAsset is null)
            return row.HasStrongIdentifier ? "CreateNew" : "Ambiguous";

        return differences.Count == 0 ? "ExactMatch" : "UpdateSafe";
    }

    private static RentalWorkbookAuditEntry CloneAuditEntry(RentalWorkbookAuditEntry entry)
        => new()
        {
            RowNumber = entry.RowNumber,
            Action = entry.Action,
            MatchedBy = entry.MatchedBy,
            ExistingAssetId = entry.ExistingAssetId,
            ExistingManagementNumber = entry.ExistingManagementNumber,
            WorkbookManagementNumber = entry.WorkbookManagementNumber,
            WorkbookManagementId = entry.WorkbookManagementId,
            WorkbookOfficeCode = entry.WorkbookOfficeCode,
            WorkbookCustomerName = entry.WorkbookCustomerName,
            WorkbookItemName = entry.WorkbookItemName,
            WorkbookMachineNumber = entry.WorkbookMachineNumber,
            WorkbookInstallLocation = entry.WorkbookInstallLocation,
            Differences = entry.Differences.ToList(),
            Warnings = entry.Warnings.ToList()
        };

    private static Dictionary<string, string> ReadWorkbookCustomerBusinessNumberMap(DataSet workbook)
    {
        var table = workbook.Tables.Cast<DataTable>()
            .FirstOrDefault(current => string.Equals(current.TableName, "거래처", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var headerRowIndex = FindHeaderRow(table, "상호명", "사업자번호");
        if (headerRowIndex < 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var headerMap = BuildHeaderMap(table, headerRowIndex);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var name = RentalCatalogValueNormalizer.NormalizeDisplayText(GetCellString(row, headerMap, "상호명"));
            var businessNumber = RentalCatalogValueNormalizer.NormalizeDisplayText(GetCellString(row, headerMap, "사업자번호"));
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(businessNumber))
                continue;

            RegisterWorkbookCustomerBusinessNumber(map, name, businessNumber);
        }

        return map;
    }

    private static List<WorkbookRentalAssetRow> ReadWorkbookAssetRows(
        DataSet workbook,
        IReadOnlyDictionary<string, string> customerBusinessNumberByName)
    {
        var table = workbook.Tables.Cast<DataTable>()
            .FirstOrDefault(current => string.Equals(current.TableName, "렌탈재고관리", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            throw new InvalidOperationException("렌탈재고관리 시트를 찾지 못했습니다.");

        var headerRowIndex = FindHeaderRow(table, "관리번호", "고객명");
        if (headerRowIndex < 0)
            throw new InvalidOperationException("렌탈재고관리 시트에서 헤더를 찾지 못했습니다.");

        var headerMap = BuildHeaderMap(table, headerRowIndex);
        var rows = new List<WorkbookRentalAssetRow>();
        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var officeValue = GetCellString(row, headerMap, "관리업체", "담당지점");
            if (!TryResolveImportManagementOfficeCode(officeValue, out var officeCode, out _))
                officeCode = DomainConstants.OfficeUsenet;

            var currentLocation = GetCellString(row, headerMap, "현재위치").Trim();
            var disposalDate = ParseDateValue(GetCellValue(row, headerMap, "폐기일"));
            if (!TryResolveImportAssetStatus(currentLocation, disposalDate, out var assetStatus, out _))
                assetStatus = string.Empty;

            var sourceRow = new WorkbookRentalAssetRow
            {
                RowNumber = rowIndex + 1,
                ManagementId = GetCellString(row, headerMap, "관리ID").Trim(),
                ManagementNumber = GetCellString(row, headerMap, "관리번호").Trim(),
                OfficeCode = officeCode,
                CurrentLocation = currentLocation,
                ItemCategoryName = GetCellString(row, headerMap, "품목분류", "상품분류").Trim(),
                  Manufacturer = GetCellString(row, headerMap, "제조사").Trim(),
                  CustomerName = GetCellString(row, headerMap, "고객명").Trim(),
                  CustomerBusinessNumber = ResolveWorkbookCustomerBusinessNumber(customerBusinessNumberByName, GetCellString(row, headerMap, "고객명")),
                  ItemName = GetCellString(row, headerMap, "품명", "모델명").Trim(),
                MachineNumber = GetCellString(row, headerMap, "기계번호").Trim(),
                InstallLocation = GetCellString(row, headerMap, "설치위치").Trim(),
                PurchaseVendor = GetCellString(row, headerMap, "매입처").Trim(),
                PurchaseDate = ParseDateValue(GetCellValue(row, headerMap, "매입일")),
                DisposalDate = disposalDate,
                PurchasePrice = ParseDecimalValue(GetCellValue(row, headerMap, "매입가")),
                SalePrice = ParseDecimalValue(GetCellValue(row, headerMap, "판매가")),
                DepositText = GetCellString(row, headerMap, "보증금").Trim(),
                MonthlyFee = ParseDecimalValue(GetCellValue(row, headerMap, "렌탈요금")),
                ContractMonths = ParseIntValue(GetCellValue(row, headerMap, "계약기간")) ?? 0,
                ContractDate = ParseDateValue(GetCellValue(row, headerMap, "계약일")),
                InstallDate = ParseDateValue(GetCellValue(row, headerMap, "설치일")),
                ContractStartDate = ParseDateValue(GetCellValue(row, headerMap, "계약시작")),
                RentalEndDate = ParseDateValue(GetCellValue(row, headerMap, "렌탈만료")),
                FreeSupplyItems = GetCellString(row, headerMap, "무상품목").Trim(),
                PaidSupplyItems = GetCellString(row, headerMap, "유상품목").Trim(),
                KRestriction = GetCellString(row, headerMap, "K제한", "K 제한").Trim(),
                CRestriction = GetCellString(row, headerMap, "C제한", "C 제한").Trim(),
                KAdditional = GetCellString(row, headerMap, "K추가", "K 추가").Trim(),
                CAdditional = GetCellString(row, headerMap, "C추가", "C 추가").Trim(),
                Remarks = GetCellString(row, headerMap, "기타사항", "비고").Trim(),
                AssetStatus = assetStatus
            };

            if (sourceRow.IsEmpty || IsWorkbookAssetSummaryRow(sourceRow))
                continue;

            rows.Add(sourceRow);
        }

        return rows;
    }

    private static string ResolveWorkbookCustomerBusinessNumber(
        IReadOnlyDictionary<string, string> customerBusinessNumberByName,
        string? customerName)
    {
        foreach (var candidate in BuildWorkbookCustomerNameCandidates(customerName))
        {
            if (customerBusinessNumberByName.TryGetValue(candidate, out var businessNumber))
                return businessNumber;
        }

        return string.Empty;
    }

    private static bool IsWorkbookAssetSummaryRow(WorkbookRentalAssetRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ManagementNumber) ||
            !string.IsNullOrWhiteSpace(row.ManagementId) ||
            !string.IsNullOrWhiteSpace(row.CustomerName) ||
            !string.IsNullOrWhiteSpace(row.MachineNumber) ||
            !string.IsNullOrWhiteSpace(row.InstallLocation))
        {
            return false;
        }

        static bool IsNumericLike(string value)
            => !string.IsNullOrWhiteSpace(value) && value.All(ch => char.IsDigit(ch) || ch == ',' || ch == '.');

        return IsNumericLike(row.CurrentLocation) ||
               IsNumericLike(row.ItemName);
    }

    private static (LocalRentalAsset? Asset, string MatchedBy, bool Ambiguous, List<string> Warnings) MatchWorkbookRowToAsset(
        WorkbookRentalAssetRow row,
        IReadOnlyList<LocalRentalAsset> assets)
    {
        var warnings = new List<string>();
        var candidateSets = new List<(string Reason, List<LocalRentalAsset> Matches)>
        {
            ("ManagementNumber", FilterMatches(assets, asset => string.Equals(NormalizeProfileKeyPart(asset.ManagementNumber), NormalizeProfileKeyPart(row.ManagementNumber), StringComparison.Ordinal))),
            ("ManagementId", FilterMatches(assets, asset => string.Equals(NormalizeProfileKeyPart(asset.ManagementId), NormalizeProfileKeyPart(row.ManagementId), StringComparison.Ordinal))),
            ("MachineNumber", FilterMatches(assets, asset => string.Equals(NormalizeProfileKeyPart(asset.MachineNumber), NormalizeProfileKeyPart(row.MachineNumber), StringComparison.Ordinal))),
            ("SourceManagementNumber", FilterMatches(assets, asset => HasImportedSourceIdentifier(asset.Notes, "원본 관리번호", row.ManagementNumber))),
            ("SourceManagementId", FilterMatches(assets, asset => HasImportedSourceIdentifier(asset.Notes, "원본 관리ID", row.ManagementId)))
        };

        var populatedSets = candidateSets
            .Where(current => current.Matches.Count > 0)
            .ToList();

        if (populatedSets.Count == 0)
        {
            if (!row.HasStrongIdentifier)
                warnings.Add("관리번호, 관리ID, 기계번호 중 일치 기준이 없어 자동 판정할 수 없습니다.");
            return (null, string.Empty, !row.HasStrongIdentifier, warnings);
        }

        foreach (var candidateSet in populatedSets)
        {
            if (candidateSet.Matches.Count == 1)
            {
                var matched = candidateSet.Matches[0];
                var conflictingHigherOrEqualMatches = populatedSets
                    .Where(current => !string.Equals(current.Reason, candidateSet.Reason, StringComparison.Ordinal))
                    .SelectMany(current => current.Matches)
                    .Select(current => current.Id)
                    .Distinct()
                    .Where(id => id != matched.Id)
                    .ToList();
                if (conflictingHigherOrEqualMatches.Count == 0)
                    return (matched, candidateSet.Reason, false, warnings);
            }

            var ranked = RankWorkbookMatches(row, candidateSet.Matches);
            if (ranked.Count > 0)
            {
                var best = ranked[0];
                var secondScore = ranked.Count > 1 ? ranked[1].Score : int.MinValue;
                if (best.Score > 0 && best.Score > secondScore)
                    return (best.Asset, $"{candidateSet.Reason}[Scored]", false, warnings);
            }
        }

        var distinctMatchCount = populatedSets
            .SelectMany(current => current.Matches)
            .Select(current => current.Id)
            .Distinct()
            .Count();
        warnings.Add($"강한 식별자 기준으로 {distinctMatchCount}건이 매칭되어 수동 확인이 필요합니다.");
        return (null, string.Join('+', populatedSets.Select(current => current.Reason).Distinct().OrderBy(value => value, StringComparer.OrdinalIgnoreCase)), true, warnings);
    }

    private static List<LocalRentalAsset> FilterMatches(
        IReadOnlyList<LocalRentalAsset> assets,
        Func<LocalRentalAsset, bool> predicate)
        => assets.Where(predicate).ToList();

    private static List<(LocalRentalAsset Asset, int Score)> RankWorkbookMatches(
        WorkbookRentalAssetRow row,
        IReadOnlyList<LocalRentalAsset> candidates)
    {
        return candidates
            .Select(asset =>
            {
                var score = 0;
                if (string.Equals(NormalizeProfileKeyPart(NormalizeOfficeCode(asset.ManagementCompanyCode, asset.ResponsibleOfficeCode)), NormalizeProfileKeyPart(row.OfficeCode), StringComparison.Ordinal))
                    score += 8;
                if (string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.CustomerName), RentalCatalogValueNormalizer.NormalizeLooseKey(row.CustomerName), StringComparison.OrdinalIgnoreCase))
                    score += 5;
                if (string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemName), RentalCatalogValueNormalizer.NormalizeLooseKey(row.ItemName), StringComparison.OrdinalIgnoreCase))
                    score += 5;
                if (string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.InstallLocation), RentalCatalogValueNormalizer.NormalizeLooseKey(row.InstallLocation), StringComparison.OrdinalIgnoreCase))
                    score += 5;
                if (string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.Manufacturer), RentalCatalogValueNormalizer.NormalizeLooseKey(row.Manufacturer), StringComparison.OrdinalIgnoreCase))
                    score += 2;
                if (asset.MonthlyFee == row.MonthlyFee && row.MonthlyFee > 0m)
                    score += 2;
                if (asset.ContractMonths == row.ContractMonths && row.ContractMonths > 0)
                    score += 2;

                return (Asset: asset, Score: score);
            })
            .OrderByDescending(current => current.Score)
            .ThenByDescending(current => current.Asset.UpdatedAtUtc)
            .ToList();
    }

    private static List<string> BuildWorkbookDifferences(WorkbookRentalAssetRow row, LocalRentalAsset asset)
    {
        var differences = new List<string>();
        CompareString(differences, "관리번호", asset.ManagementNumber, row.ManagementNumber, NormalizeProfileKeyPart);
        CompareString(differences, "관리ID", asset.ManagementId, row.ManagementId, NormalizeProfileKeyPart);
        CompareString(differences, "관리업체", NormalizeOfficeCode(asset.ManagementCompanyCode, asset.ResponsibleOfficeCode), row.OfficeCode, NormalizeProfileKeyPart);
        CompareString(differences, "현재위치", asset.CurrentLocation, row.CurrentLocation, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "품목분류", asset.ItemCategoryName, row.ItemCategoryName, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "제조사", asset.Manufacturer, row.Manufacturer, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "거래처", asset.CustomerName, row.CustomerName, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "품명", asset.ItemName, row.ItemName, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "기계번호", asset.MachineNumber, row.MachineNumber, NormalizeProfileKeyPart);
        CompareOptionalWorkbookString(differences, "설치위치", asset.InstallLocation, row.InstallLocation, RentalCatalogValueNormalizer.NormalizeLooseKey, ignoreWhenWorkbookBlank: true);
        CompareString(differences, "매입처", asset.PurchaseVendor, row.PurchaseVendor, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareDate(differences, "매입일", asset.PurchaseDate, row.PurchaseDate);
        CompareDate(differences, "폐기일", asset.DisposalDate, row.DisposalDate);
        CompareDecimal(differences, "매입가", asset.PurchasePrice, row.PurchasePrice);
        CompareDecimal(differences, "판매가", asset.SalePrice, row.SalePrice);
        CompareString(differences, "보증금", asset.DepositText, row.DepositText, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareDecimal(differences, "렌탈요금", asset.MonthlyFee, row.MonthlyFee);
        CompareInt(differences, "계약기간", asset.ContractMonths, row.ContractMonths);
        CompareDate(differences, "계약일", asset.ContractDate, row.ContractDate);
        CompareDate(differences, "설치일", asset.InstallDate, row.InstallDate);
        CompareDate(differences, "계약시작", asset.ContractStartDate, row.ContractStartDate);
        CompareDate(differences, "렌탈만료", asset.RentalEndDate, row.RentalEndDate);
        CompareString(differences, "무상품목", asset.FreeSupplyItems, row.FreeSupplyItems, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "유상품목", asset.PaidSupplyItems, row.PaidSupplyItems, RentalCatalogValueNormalizer.NormalizeLooseKey);
        CompareString(differences, "자산상태", asset.AssetStatus, row.AssetStatus, RentalCatalogValueNormalizer.NormalizeLooseKey);
        return differences;
    }

    private static void ApplyWorkbookRowToAsset(LocalRentalAsset asset, WorkbookRentalAssetRow row, SessionState session, DateTime now)
    {
        asset.ManagementCompanyCode = row.OfficeCode;
        asset.ResponsibleOfficeCode = row.OfficeCode;
        asset.AssignedUsername = string.Empty;
        asset.ManagementId = row.ManagementId;
        asset.ManagementNumber = row.ManagementNumber;
        asset.CurrentLocation = row.CurrentLocation;
        asset.ItemCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(row.ItemCategoryName);
        asset.Manufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(row.Manufacturer);
        asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(row.CustomerName);
        asset.CurrentCustomerName = asset.CustomerName;
        asset.BillToCustomerName = asset.CustomerName;
        asset.ItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(row.ItemName);
        asset.MachineNumber = row.MachineNumber;
        var normalizedInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(row.InstallLocation);
        if (!string.IsNullOrWhiteSpace(normalizedInstallLocation) || string.IsNullOrWhiteSpace(asset.InstallLocation))
        {
            asset.InstallLocation = normalizedInstallLocation;
            asset.InstallSiteName = normalizedInstallLocation;
        }
        else if (string.IsNullOrWhiteSpace(asset.InstallSiteName))
        {
            asset.InstallSiteName = asset.InstallLocation;
        }
        asset.PurchaseVendor = RentalCatalogValueNormalizer.NormalizeDisplayText(row.PurchaseVendor);
        asset.PurchaseDate = row.PurchaseDate;
        asset.DisposalDate = row.DisposalDate;
        asset.PurchasePrice = row.PurchasePrice;
        asset.SalePrice = row.SalePrice;
        asset.DepositText = row.DepositText;
        asset.MonthlyFee = row.MonthlyFee;
        asset.ContractMonths = row.ContractMonths;
        asset.ContractDate = row.ContractDate;
        asset.InstallDate = row.InstallDate;
        asset.ContractStartDate = row.ContractStartDate;
        asset.RentalEndDate = row.RentalEndDate;
        asset.FreeSupplyItems = row.FreeSupplyItems;
        asset.PaidSupplyItems = row.PaidSupplyItems;
        asset.AssetStatus = ResolveAssetStatus(row.AssetStatus, row.CurrentLocation, row.DisposalDate);
        asset.BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus)
            ? GetDefaultBillingEligibilityStatus(asset)
            : asset.BillingEligibilityStatus;
        asset.BillingExclusionReason = (asset.BillingExclusionReason ?? string.Empty).Trim();
        asset.CustomerId = null;
        asset.ItemId = null;
        asset.AssetKey = string.Empty;
        asset.Notes = BuildWorkbookAssetNotes(row);
        asset.IsDeleted = false;
        asset.IsDirty = true;
        asset.UpdatedAtUtc = now;
        if (asset.CreatedAtUtc == default)
            asset.CreatedAtUtc = now;
    }

    private static string BuildWorkbookAssetNotes(WorkbookRentalAssetRow row)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.ManagementId))
            lines.Add($"원본 관리ID: {row.ManagementId}");
        if (!string.IsNullOrWhiteSpace(row.ManagementNumber))
            lines.Add($"원본 관리번호: {row.ManagementNumber}");
        if (!string.IsNullOrWhiteSpace(row.KRestriction))
            lines.Add($"K제한: {row.KRestriction}");
        if (!string.IsNullOrWhiteSpace(row.CRestriction))
            lines.Add($"C제한: {row.CRestriction}");
        if (!string.IsNullOrWhiteSpace(row.KAdditional))
            lines.Add($"K추가: {row.KAdditional}");
        if (!string.IsNullOrWhiteSpace(row.CAdditional))
            lines.Add($"C추가: {row.CAdditional}");
        if (!string.IsNullOrWhiteSpace(row.Remarks))
            lines.Add($"기타사항: {row.Remarks}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string CreateLocalDbBackup(string prefix)
    {
        var source = AppPaths.LocalDbFile;
        if (!File.Exists(source))
            throw new FileNotFoundException("로컬 DB 파일을 찾을 수 없습니다.", source);

        Directory.CreateDirectory(AppPaths.BackupDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = Path.Combine(AppPaths.BackupDir, $"거래플랜-{prefix}-{timestamp}.db");
        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    private static void CompareString(List<string> differences, string label, string? existing, string? workbook, Func<string?, string> normalize)
    {
        if (string.Equals(normalize(existing), normalize(workbook), StringComparison.OrdinalIgnoreCase))
            return;

        differences.Add(label);
    }

    private static void CompareOptionalWorkbookString(
        List<string> differences,
        string label,
        string? existing,
        string? workbook,
        Func<string?, string> normalize,
        bool ignoreWhenWorkbookBlank)
    {
        if (ignoreWhenWorkbookBlank && string.IsNullOrWhiteSpace(workbook))
            return;

        CompareString(differences, label, existing, workbook, normalize);
    }

    private static void CompareDate(List<string> differences, string label, DateOnly? existing, DateOnly? workbook)
    {
        if (existing == workbook)
            return;

        differences.Add(label);
    }

    private static void CompareDecimal(List<string> differences, string label, decimal existing, decimal workbook)
    {
        if (existing == workbook)
            return;

        differences.Add(label);
    }

    private static void CompareInt(List<string> differences, string label, int existing, int workbook)
    {
        if (existing == workbook)
            return;

        differences.Add(label);
    }

    private static void RegisterWorkbookCustomerBusinessNumber(
        IDictionary<string, string> map,
        string? customerName,
        string businessNumber)
    {
        foreach (var candidate in BuildWorkbookCustomerNameCandidates(customerName))
            map[candidate] = businessNumber;
    }

    private static IEnumerable<string> BuildWorkbookCustomerNameCandidates(string? customerName)
    {
        var normalizedName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> Expand(string seed)
        {
            yield return seed;

            var normalizedBracketSeed = seed
                .Replace('｛', '[')
                .Replace('｝', ']')
                .Replace('{', '[')
                .Replace('}', ']');
            if (!string.Equals(normalizedBracketSeed, seed, StringComparison.Ordinal))
                yield return normalizedBracketSeed.Trim();

            var invertedBracketMatch = Regex.Match(seed, @"^(?<prefix>[^\[\]]+)\[(?<body>[^\[\]]+)\]$");
            if (invertedBracketMatch.Success)
                yield return $"[{invertedBracketMatch.Groups["prefix"].Value.Trim()}]{invertedBracketMatch.Groups["body"].Value.Trim()}";

            var withoutLeadingTags = Regex.Replace(seed, @"^(?:\[[^\]]+\]|\{[^\}]+\})+", string.Empty).Trim();
            if (!string.Equals(withoutLeadingTags, seed, StringComparison.Ordinal))
                yield return withoutLeadingTags;

            var hyphenIndex = seed.IndexOf('-', StringComparison.Ordinal);
            if (hyphenIndex > 0)
                yield return seed[..hyphenIndex].Trim();

            if (!string.IsNullOrWhiteSpace(withoutLeadingTags))
            {
                var withoutLeadingTagHyphenIndex = withoutLeadingTags.IndexOf('-', StringComparison.Ordinal);
                if (withoutLeadingTagHyphenIndex > 0)
                    yield return withoutLeadingTags[..withoutLeadingTagHyphenIndex].Trim();
            }

            if (seed.Contains("㈜", StringComparison.Ordinal))
                yield return seed.Replace("㈜", "주식회사", StringComparison.Ordinal).Trim();

            if (seed.Contains("주식회사", StringComparison.Ordinal))
                yield return seed.Replace("주식회사", "㈜", StringComparison.Ordinal).Trim();
        }

        foreach (var expanded in Expand(normalizedName))
        {
            var candidate = RentalCatalogValueNormalizer.NormalizeDisplayText(expanded);
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    private void ValidateRebuildTargets(
        IReadOnlyList<RentalWorkbookRebuildOperation> operations,
        RentalWorkbookRebuildResult rebuildResult)
    {
        if (operations.Count == 0)
            return;

        var allActiveAssets = _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted)
            .Select(asset => new
            {
                asset.Id,
                ManagementId = NormalizeProfileKeyPart(asset.ManagementId),
                ManagementNumber = NormalizeProfileKeyPart(asset.ManagementNumber)
            })
            .ToList();

        var addedAmbiguity = true;
        while (addedAmbiguity)
        {
            var beforeCount = rebuildResult.AmbiguousEntries.Count;
            var ambiguousRowNumbers = rebuildResult.AmbiguousEntries
                .Select(current => current.RowNumber)
                .ToHashSet();
            var candidateOperations = operations
                .Where(current => !ambiguousRowNumbers.Contains(current.Entry.RowNumber))
                .ToList();
            if (candidateOperations.Count == 0)
                break;

            var touchedAssetIds = candidateOperations
                .Where(current => !current.IsCreate)
                .Select(current => current.Asset.Id)
                .ToHashSet();
            var untouchedAssets = allActiveAssets
                .Where(current => !touchedAssetIds.Contains(current.Id))
                .ToList();

            FlagDuplicateTargetGroups(
                candidateOperations,
                rebuildResult,
                current => NormalizeProfileKeyPart(current.Row.ManagementId),
                "관리ID");
            FlagDuplicateTargetGroups(
                candidateOperations,
                rebuildResult,
                current => NormalizeProfileKeyPart(current.Row.ManagementNumber),
                "관리번호");

            foreach (var operation in candidateOperations)
            {
                if (rebuildResult.AmbiguousEntries.Any(current => current.RowNumber == operation.Entry.RowNumber))
                    continue;

                var targetManagementId = NormalizeProfileKeyPart(operation.Row.ManagementId);
                if (!string.IsNullOrWhiteSpace(targetManagementId))
                {
                    var conflictingAsset = untouchedAssets.FirstOrDefault(current => string.Equals(current.ManagementId, targetManagementId, StringComparison.Ordinal));
                    if (conflictingAsset is not null)
                    {
                        AddRebuildAmbiguity(rebuildResult, operation.Entry, $"관리ID '{operation.Row.ManagementId}' 가 다른 활성 자산과 충돌합니다.");
                        continue;
                    }
                }

                var targetManagementNumber = NormalizeProfileKeyPart(operation.Row.ManagementNumber);
                if (string.IsNullOrWhiteSpace(targetManagementNumber))
                    continue;

                var conflictingByNumber = untouchedAssets.FirstOrDefault(current => string.Equals(current.ManagementNumber, targetManagementNumber, StringComparison.Ordinal));
                if (conflictingByNumber is not null)
                    AddRebuildAmbiguity(rebuildResult, operation.Entry, $"관리번호 '{operation.Row.ManagementNumber}' 가 다른 활성 자산과 충돌합니다.");
            }

            addedAmbiguity = rebuildResult.AmbiguousEntries.Count > beforeCount;
        }
    }

    private static void FlagDuplicateTargetGroups(
        IReadOnlyList<RentalWorkbookRebuildOperation> operations,
        RentalWorkbookRebuildResult rebuildResult,
        Func<RentalWorkbookRebuildOperation, string> keySelector,
        string label)
    {
        foreach (var group in operations
                     .GroupBy(keySelector, StringComparer.Ordinal)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            foreach (var operation in group)
                AddRebuildAmbiguity(rebuildResult, operation.Entry, $"{label} '{group.Key}' 대상이 workbook 안에서 중복됩니다.");
        }
    }

    private static void AddRebuildAmbiguity(
        RentalWorkbookRebuildResult rebuildResult,
        RentalWorkbookAuditEntry entry,
        string warning)
    {
        if (rebuildResult.AmbiguousEntries.Any(current => current.RowNumber == entry.RowNumber))
            return;

        var ambiguousEntry = CloneAuditEntry(entry);
        ambiguousEntry.Action = "Ambiguous";
        if (!ambiguousEntry.Warnings.Contains(warning, StringComparer.Ordinal))
            ambiguousEntry.Warnings.Add(warning);
        rebuildResult.AmbiguousEntries.Add(ambiguousEntry);
        rebuildResult.AmbiguousCount++;
    }

    private async Task ReserveRentalAssetUniqueValuesAsync(
        IReadOnlyList<RentalWorkbookRebuildOperation> operations,
        DateTime now,
        CancellationToken ct)
    {
        var existingOperations = operations
            .Where(current => !current.IsCreate)
            .Where(current =>
                !string.Equals(NormalizeProfileKeyPart(current.OriginalManagementId), NormalizeProfileKeyPart(current.Row.ManagementId), StringComparison.Ordinal) ||
                !string.Equals(NormalizeProfileKeyPart(current.OriginalManagementNumber), NormalizeProfileKeyPart(current.Row.ManagementNumber), StringComparison.Ordinal))
            .ToList();

        if (existingOperations.Count == 0)
            return;

        foreach (var operation in existingOperations)
        {
            operation.Asset.ManagementId = $"__REBUILD_TMP_ID__{operation.Asset.Id:N}";
            operation.Asset.ManagementNumber = $"__REBUILD_TMP_NO__{operation.Asset.Id:N}";
            operation.Asset.UpdatedAtUtc = now;
            operation.Asset.IsDirty = true;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RestoreReservedRentalAssetUniqueValuesAsync(
        IReadOnlyList<RentalWorkbookRebuildOperation> operations,
        CancellationToken ct)
    {
        foreach (var operation in operations)
        {
            if (operation.IsCreate)
            {
                var entry = _db.Entry(operation.Asset);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
                continue;
            }

            operation.Asset.ManagementId = operation.OriginalManagementId;
            operation.Asset.ManagementNumber = operation.OriginalManagementNumber;
            operation.Asset.AssetKey = BuildAssetKey(
                operation.Asset.ManagementCompanyCode,
                operation.OriginalManagementNumber,
                operation.OriginalManagementId,
                operation.Asset.MachineNumber,
                operation.Asset.CustomerName,
                operation.Asset.ItemName);
        }

        await _db.SaveChangesAsync(ct);
    }

    private sealed record WorkbookAuditContext(
        RentalWorkbookAuditResult Result,
        IReadOnlyDictionary<int, WorkbookRentalAssetRow> RowsByRowNumber);

    private sealed record RentalWorkbookRebuildOperation(
        RentalWorkbookAuditEntry Entry,
        WorkbookRentalAssetRow Row,
        LocalRentalAsset Asset,
        bool IsCreate,
        string OriginalManagementId,
        string OriginalManagementNumber);

    private sealed class WorkbookRentalAssetRow
    {
        public int RowNumber { get; init; }
        public string ManagementId { get; init; } = string.Empty;
        public string ManagementNumber { get; init; } = string.Empty;
        public string OfficeCode { get; init; } = string.Empty;
        public string CurrentLocation { get; init; } = string.Empty;
        public string ItemCategoryName { get; init; } = string.Empty;
        public string Manufacturer { get; init; } = string.Empty;
        public string CustomerName { get; init; } = string.Empty;
        public string CustomerBusinessNumber { get; init; } = string.Empty;
        public string ItemName { get; init; } = string.Empty;
        public string MachineNumber { get; init; } = string.Empty;
        public string InstallLocation { get; init; } = string.Empty;
        public string PurchaseVendor { get; init; } = string.Empty;
        public DateOnly? PurchaseDate { get; init; }
        public DateOnly? DisposalDate { get; init; }
        public decimal PurchasePrice { get; init; }
        public decimal SalePrice { get; init; }
        public string DepositText { get; init; } = string.Empty;
        public decimal MonthlyFee { get; init; }
        public int ContractMonths { get; init; }
        public DateOnly? ContractDate { get; init; }
        public DateOnly? InstallDate { get; init; }
        public DateOnly? ContractStartDate { get; init; }
        public DateOnly? RentalEndDate { get; init; }
        public string FreeSupplyItems { get; init; } = string.Empty;
        public string PaidSupplyItems { get; init; } = string.Empty;
        public string KRestriction { get; init; } = string.Empty;
        public string CRestriction { get; init; } = string.Empty;
        public string KAdditional { get; init; } = string.Empty;
        public string CAdditional { get; init; } = string.Empty;
        public string Remarks { get; init; } = string.Empty;
        public string AssetStatus { get; init; } = string.Empty;
        public bool HasStrongIdentifier =>
            !string.IsNullOrWhiteSpace(ManagementNumber) ||
            !string.IsNullOrWhiteSpace(ManagementId) ||
            !string.IsNullOrWhiteSpace(MachineNumber);
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(ManagementNumber) &&
            string.IsNullOrWhiteSpace(ManagementId) &&
            string.IsNullOrWhiteSpace(CustomerName) &&
            string.IsNullOrWhiteSpace(ItemName) &&
            string.IsNullOrWhiteSpace(MachineNumber);
    }
}

