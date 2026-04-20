using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LegacyDataMigrationService
{
    private const string LegacyLocalDbFileName = "salesmaster.db";
    private const string DefaultCustomerExcelFileName = "거래처 목록.xlsx";
    private const string DefaultItemExcelFileName = "제품 목록.xlsx";

    private readonly record struct LegacyExcelPathPair(string CustomerExcelPath, string ItemExcelPath);

    private static string? ResolveLegacyLocalDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "SalesMaster", "data", LegacyLocalDbFileName),
            Path.Combine(localAppData, "거래플랜", "data", LegacyLocalDbFileName),
        };

        var currentDbPath = Path.GetFullPath(AppPaths.LocalDbFile);
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            var fullPath = Path.GetFullPath(candidate);
            if (string.Equals(fullPath, currentDbPath, StringComparison.OrdinalIgnoreCase))
                continue;

            return fullPath;
        }

        return null;
    }

    private async Task<LegacyExcelPathPair> ResolveConfiguredLegacyExcelPathsAsync(CancellationToken ct)
    {
        var configuredCustomerPath = (await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey, ct))?.Trim();
        var configuredItemPath = (await _local.GetSettingAsync(LegacyItemExcelPathSettingKey, ct))?.Trim();

        if (File.Exists(configuredCustomerPath) && File.Exists(configuredItemPath))
            return new LegacyExcelPathPair(configuredCustomerPath!, configuredItemPath!);

        foreach (var root in EnumerateLegacyProbeRoots())
        {
            var customerCandidate = Path.Combine(root, DefaultCustomerExcelFileName);
            var itemCandidate = Path.Combine(root, DefaultItemExcelFileName);
            if (File.Exists(customerCandidate) && File.Exists(itemCandidate))
                return new LegacyExcelPathPair(customerCandidate, itemCandidate);
        }

        return new LegacyExcelPathPair(configuredCustomerPath ?? string.Empty, configuredItemPath ?? string.Empty);
    }

    private static IEnumerable<string> EnumerateLegacyProbeRoots()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> EnumerateSelfAndParents(string? start)
        {
            if (string.IsNullOrWhiteSpace(start))
                yield break;

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current))
            {
                yield return current;
                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    yield break;
                current = parent;
            }
        }

        foreach (var candidate in EnumerateSelfAndParents(AppContext.BaseDirectory))
        {
            if (visited.Add(candidate))
                yield return candidate;
        }

        foreach (var candidate in EnumerateSelfAndParents(Environment.CurrentDirectory))
        {
            if (visited.Add(candidate))
                yield return candidate;
        }
    }

    private static string BuildFileFingerprint(params string[] paths)
    {
        using var sha = SHA256.Create();
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var path in normalizedPaths)
        {
            var fileInfo = new FileInfo(path);
            var metadata = $"{path}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
            var metadataBytes = Encoding.UTF8.GetBytes(metadata);
            _ = sha.TransformBlock(metadataBytes, 0, metadataBytes.Length, null, 0);

            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                _ = sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        _ = sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    private static List<LocalCustomer> ReadCustomersFromLegacyLocalDb(string sourceDbPath)
    {
        using var connection = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly");
        connection.Open();

        var columns = BuildColumnSet(connection, "Customers");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Customers WHERE IFNULL(IsDeleted, 0) = 0 ORDER BY NameOriginal";
        using var reader = command.ExecuteReader();

        var rows = new List<LocalCustomer>();
        while (reader.Read())
        {
            var name = ReadSqliteString(reader, columns, "NameOriginal");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new LocalCustomer
            {
                Id = ReadSqliteGuid(reader, columns, "Id") ?? Guid.NewGuid(),
                CustomerMasterId = ReadSqliteGuid(reader, columns, "CustomerMasterId"),
                NameOriginal = name,
                NameMatchKey = ReadSqliteString(reader, columns, "NameMatchKey"),
                CategoryId = ReadSqliteGuid(reader, columns, "CategoryId"),
                TradeType = ReadSqliteString(reader, columns, "TradeType"),
                Department = ReadSqliteString(reader, columns, "Department"),
                ContactPerson = ReadSqliteString(reader, columns, "ContactPerson"),
                BusinessNumber = ReadSqliteString(reader, columns, "BusinessNumber"),
                Address = ReadSqliteString(reader, columns, "Address"),
                DetailAddress = ReadSqliteString(reader, columns, "DetailAddress"),
                Phone = ReadSqliteString(reader, columns, "Phone"),
                MobilePhone = ReadSqliteString(reader, columns, "MobilePhone"),
                FaxNumber = ReadSqliteString(reader, columns, "FaxNumber"),
                Email = ReadSqliteString(reader, columns, "Email"),
                HomePage = ReadSqliteString(reader, columns, "HomePage"),
                Representative = ReadSqliteString(reader, columns, "Representative"),
                BusinessType = ReadSqliteString(reader, columns, "BusinessType"),
                BusinessItem = ReadSqliteString(reader, columns, "BusinessItem"),
                Recipient = ReadSqliteString(reader, columns, "Recipient"),
                PriceGrade = ReadSqliteString(reader, columns, "PriceGrade"),
                Notes = ReadSqliteString(reader, columns, "Notes"),
                ResponsibleOfficeCode = ReadSqliteString(reader, columns, "ResponsibleOfficeCode"),
                IsDeleted = ReadSqliteBool(reader, columns, "IsDeleted"),
                CreatedAtUtc = ReadSqliteDateTime(reader, columns, "CreatedAtUtc") ?? DateTime.UnixEpoch,
                UpdatedAtUtc = ReadSqliteDateTime(reader, columns, "UpdatedAtUtc") ?? DateTime.UnixEpoch,
                Revision = ReadSqliteLong(reader, columns, "Revision"),
                IsDirty = ReadSqliteBool(reader, columns, "IsDirty", true)
            });
        }

        return rows;
    }

    private static List<LocalItem> ReadItemsFromLegacyLocalDb(string sourceDbPath)
    {
        using var connection = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly");
        connection.Open();

        var columns = BuildColumnSet(connection, "Items");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Items WHERE IFNULL(IsDeleted, 0) = 0 ORDER BY NameOriginal, SpecificationOriginal";
        using var reader = command.ExecuteReader();

        var rows = new List<LocalItem>();
        while (reader.Read())
        {
            var name = ReadSqliteString(reader, columns, "NameOriginal");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows.Add(new LocalItem
            {
                Id = ReadSqliteGuid(reader, columns, "Id") ?? Guid.NewGuid(),
                NameOriginal = name,
                NameMatchKey = ReadSqliteString(reader, columns, "NameMatchKey"),
                SpecificationOriginal = ReadSqliteString(reader, columns, "SpecificationOriginal"),
                SpecificationMatchKey = ReadSqliteString(reader, columns, "SpecificationMatchKey"),
                CategoryName = ReadSqliteString(reader, columns, "CategoryName"),
                Unit = ReadSqliteString(reader, columns, "Unit"),
                BoxQuantity = ReadSqliteDecimal(reader, columns, "BoxQuantity"),
                StorageLocation = ReadSqliteString(reader, columns, "StorageLocation"),
                CurrentStock = ReadSqliteDecimal(reader, columns, "CurrentStock"),
                SafetyStock = ReadSqliteDecimal(reader, columns, "SafetyStock"),
                PurchasePrice = ReadSqliteDecimal(reader, columns, "PurchasePrice"),
                SalePrice = ReadSqliteDecimal(reader, columns, "SalePrice"),
                RetailPrice = ReadSqliteDecimal(reader, columns, "RetailPrice"),
                PriceGradeA = ReadSqliteDecimal(reader, columns, "PriceGradeA"),
                PriceGradeB = ReadSqliteDecimal(reader, columns, "PriceGradeB"),
                PriceGradeC = ReadSqliteDecimal(reader, columns, "PriceGradeC"),
                LastPurchaseDate = ReadSqliteDateOnly(reader, columns, "LastPurchaseDate"),
                LastSaleDate = ReadSqliteDateOnly(reader, columns, "LastSaleDate"),
                SimpleMemo = ReadSqliteString(reader, columns, "SimpleMemo"),
                IsRental = ReadSqliteBool(reader, columns, "IsRental"),
                IsSale = ReadSqliteBool(reader, columns, "IsSale", true),
                SerialNumber = ReadSqliteString(reader, columns, "SerialNumber"),
                MaterialNumber = ReadSqliteString(reader, columns, "MaterialNumber"),
                InstallLocation = ReadSqliteString(reader, columns, "InstallLocation"),
                RentalStartDate = ReadSqliteDateOnly(reader, columns, "RentalStartDate"),
                RentalEndDate = ReadSqliteDateOnly(reader, columns, "RentalEndDate"),
                Notes = ReadSqliteString(reader, columns, "Notes"),
                IsDeleted = ReadSqliteBool(reader, columns, "IsDeleted"),
                CreatedAtUtc = ReadSqliteDateTime(reader, columns, "CreatedAtUtc") ?? DateTime.UnixEpoch,
                UpdatedAtUtc = ReadSqliteDateTime(reader, columns, "UpdatedAtUtc") ?? DateTime.UnixEpoch,
                Revision = ReadSqliteLong(reader, columns, "Revision"),
                IsDirty = ReadSqliteBool(reader, columns, "IsDirty", true)
            });
        }

        return rows;
    }

    private static HashSet<string> BuildColumnSet(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!string.IsNullOrWhiteSpace(name))
                columns.Add(name);
        }

        return columns;
    }

    private static string ReadSqliteString(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        if (!columns.Contains(columnName))
            return string.Empty;

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return string.Empty;

        return reader.GetValue(ordinal)?.ToString()?.Trim() ?? string.Empty;
    }

    private static Guid? ReadSqliteGuid(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        var raw = ReadSqliteString(reader, columns, columnName);
        return Guid.TryParse(raw, out var value) ? value : null;
    }

    private static decimal ReadSqliteDecimal(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        if (!columns.Contains(columnName))
            return 0m;

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return 0m;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => Convert.ToDecimal(doubleValue),
            float floatValue => Convert.ToDecimal(floatValue),
            long longValue => longValue,
            int intValue => intValue,
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0m
        };
    }

    private static bool ReadSqliteBool(SqliteDataReader reader, ISet<string> columns, string columnName, bool defaultValue = false)
    {
        if (!columns.Contains(columnName))
            return defaultValue;

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return defaultValue;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool boolValue => boolValue,
            long longValue => longValue != 0,
            int intValue => intValue != 0,
            string stringValue when bool.TryParse(stringValue, out var parsedBool) => parsedBool,
            string stringValue when long.TryParse(stringValue, out var parsedLong) => parsedLong != 0,
            _ => defaultValue
        };
    }

    private static long ReadSqliteLong(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        if (!columns.Contains(columnName))
            return 0;

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return 0;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            _ => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0
        };
    }

    private static DateTime? ReadSqliteDateTime(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        if (!columns.Contains(columnName))
            return null;

        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
            return null;

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => NormalizeUtc(dateTime),
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var invariantParsed) => NormalizeUtc(invariantParsed),
            string text when DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var cultureParsed) => NormalizeUtc(cultureParsed),
            _ => null
        };
    }

    private static DateOnly? ReadSqliteDateOnly(SqliteDataReader reader, ISet<string> columns, string columnName)
    {
        var value = ReadSqliteDateTime(reader, columns, columnName);
        return value.HasValue ? DateOnly.FromDateTime(value.Value) : null;
    }

    private static LocalCustomer BuildImportedCustomer(ImportedCustomerRow row)
    {
        var customer = CreateNewCustomerShell();
        ApplyCustomer(customer, row);
        customer.CreatedAtUtc = ToUtc(row.RegisterDate);
        customer.UpdatedAtUtc = customer.CreatedAtUtc;
        customer.IsDirty = false;
        return customer;
    }

    private static LocalItem BuildImportedItem(ImportedItemRow row)
    {
        var item = CreateNewItemShell();
        ApplyItem(item, row);
        item.CreatedAtUtc = ToUtc(row.RegisterDate);
        item.UpdatedAtUtc = item.CreatedAtUtc;
        item.IsDirty = false;
        return item;
    }

    private static LocalCustomer CreateNewCustomerShell(LocalCustomer? source = null)
    {
        return new LocalCustomer
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = source?.CreatedAtUtc ?? DateTime.UnixEpoch,
            UpdatedAtUtc = source?.UpdatedAtUtc ?? DateTime.UnixEpoch,
            Revision = source?.Revision ?? 0,
            TradeType = NormalizeTradeType(source?.TradeType),
            PriceGrade = NormalizePriceGrade(source?.PriceGrade),
            ResponsibleOfficeCode = NormalizeOfficeCode(source?.ResponsibleOfficeCode)
        };
    }

    private static LocalItem CreateNewItemShell(LocalItem? source = null)
    {
        return new LocalItem
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = source?.CreatedAtUtc ?? DateTime.UnixEpoch,
            UpdatedAtUtc = source?.UpdatedAtUtc ?? DateTime.UnixEpoch,
            Revision = source?.Revision ?? 0,
            IsSale = source?.IsSale ?? true,
            IsRental = source?.IsRental ?? false
        };
    }

    private static Dictionary<string, LocalCustomer?> BuildCustomerLookup(IEnumerable<LocalCustomer> customers)
    {
        var lookup = new Dictionary<string, LocalCustomer?>(StringComparer.Ordinal);
        foreach (var customer in customers.Where(customer => !customer.IsDeleted))
            RegisterCustomerLookup(lookup, customer);

        return lookup;
    }

    private static void RegisterCustomerLookup(IDictionary<string, LocalCustomer?> lookup, LocalCustomer customer)
    {
        foreach (var key in EnumerateCustomerExactKeys(customer))
            lookup[key] = customer;

        foreach (var key in EnumerateCustomerFallbackKeys(customer).Distinct(StringComparer.Ordinal))
        {
            if (!lookup.TryGetValue(key, out var existing))
            {
                lookup[key] = customer;
                continue;
            }

            if (existing is not null && existing.Id != customer.Id)
                lookup[key] = null;
        }
    }

    private static LocalCustomer? FindMatchingCustomer(
        IReadOnlyDictionary<string, LocalCustomer?> lookup,
        LocalCustomer source)
    {
        foreach (var key in EnumerateCustomerExactKeys(source))
        {
            if (lookup.TryGetValue(key, out var customer) && customer is not null)
                return customer;
        }

        foreach (var key in EnumerateCustomerFallbackKeys(source).Distinct(StringComparer.Ordinal))
        {
            if (lookup.TryGetValue(key, out var customer) && customer is not null)
                return customer;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCustomerExactKeys(LocalCustomer customer)
    {
        var businessNumber = NormalizeBusinessNumber(customer.BusinessNumber);
        var nameKey = NormalizeKey(customer.NameOriginal);
        var locationKey = BuildCustomerLocationKey(customer);

        if (!string.IsNullOrWhiteSpace(businessNumber) &&
            !string.IsNullOrWhiteSpace(nameKey) &&
            !string.IsNullOrWhiteSpace(locationKey))
        {
            yield return $"BIZ_NAME_LOC:{businessNumber}|{nameKey}|{locationKey}";
        }

        if (string.IsNullOrWhiteSpace(businessNumber) &&
            !string.IsNullOrWhiteSpace(nameKey) &&
            !string.IsNullOrWhiteSpace(locationKey))
        {
            yield return $"NAME_LOC:{nameKey}|{locationKey}";
        }
    }

    private static IEnumerable<string> EnumerateCustomerFallbackKeys(LocalCustomer customer)
    {
        var businessNumber = NormalizeBusinessNumber(customer.BusinessNumber);
        var nameKey = NormalizeKey(customer.NameOriginal);
        var locationKey = BuildCustomerLocationKey(customer);

        if (!string.IsNullOrWhiteSpace(businessNumber) && !string.IsNullOrWhiteSpace(nameKey))
            yield return $"BIZ_NAME:{businessNumber}|{nameKey}";

        if (!string.IsNullOrWhiteSpace(businessNumber) && string.IsNullOrWhiteSpace(nameKey) && !string.IsNullOrWhiteSpace(locationKey))
            yield return $"BIZ_LOC:{businessNumber}|{locationKey}";

        if (!string.IsNullOrWhiteSpace(nameKey))
            yield return $"NAME:{nameKey}";

        if (!string.IsNullOrWhiteSpace(businessNumber) && string.IsNullOrWhiteSpace(nameKey))
            yield return $"BIZ:{businessNumber}";
    }

    private static string BuildCustomerLocationKey(LocalCustomer customer)
    {
        var branchOffice = TryExtractCustomerNoteValue(customer.Notes, "종사업장");
        var fullAddress = NormalizeKey($"{customer.Address} {customer.DetailAddress}");
        var branchOfficeKey = NormalizeKey(branchOffice);

        if (string.IsNullOrWhiteSpace(branchOfficeKey) && string.IsNullOrWhiteSpace(fullAddress))
            return string.Empty;

        return $"{branchOfficeKey}|{fullAddress}";
    }

    private static string TryExtractCustomerNoteValue(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(label))
            return string.Empty;

        foreach (var line in notes
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(current => current.Trim()))
        {
            if (line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase))
                return line[(label.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string NormalizeBusinessNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static Dictionary<string, LocalItem> BuildItemLookup(IEnumerable<LocalItem> items)
    {
        var lookup = new Dictionary<string, LocalItem>(StringComparer.Ordinal);
        foreach (var item in items.Where(item => !item.IsDeleted))
            RegisterItemLookup(lookup, item);

        return lookup;
    }

    private static void RegisterItemLookup(IDictionary<string, LocalItem> lookup, LocalItem item)
    {
        foreach (var key in EnumerateItemKeys(item))
            lookup[key] = item;
    }

    private static LocalItem? FindMatchingItem(
        IReadOnlyDictionary<string, LocalItem> lookup,
        LocalItem source)
    {
        foreach (var key in EnumerateItemKeys(source))
        {
            if (lookup.TryGetValue(key, out var item))
                return item;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateItemKeys(LocalItem item)
    {
        var materialNumber = NormalizeKey(item.MaterialNumber);
        if (!string.IsNullOrWhiteSpace(materialNumber))
            yield return $"MAT:{materialNumber}";

        var compositeKey = BuildItemKey(item.NameOriginal, item.SpecificationOriginal);
        if (!string.IsNullOrWhiteSpace(compositeKey.Trim('|')))
            yield return $"ITEM:{compositeKey}";
    }

    private static bool MergeCustomer(LocalCustomer target, LocalCustomer source, bool preferIncoming)
    {
        var changed = false;

        changed |= ApplyString(target.NameOriginal, source.NameOriginal, preferIncoming, value => target.NameOriginal = value);
        var nameMatchKey = BuildMatchKey(target.NameOriginal);
        if (!string.Equals(target.NameMatchKey, nameMatchKey, StringComparison.Ordinal))
        {
            target.NameMatchKey = nameMatchKey;
            changed = true;
        }

        changed |= ApplyNullableGuid(target.CustomerMasterId, source.CustomerMasterId, value => target.CustomerMasterId = value);
        changed |= ApplyNullableGuid(target.CategoryId, source.CategoryId, value => target.CategoryId = value);
        changed |= ApplyString(target.TradeType, NormalizeTradeType(source.TradeType), preferIncoming, value => target.TradeType = value);
        changed |= ApplyString(target.Department, source.Department, preferIncoming, value => target.Department = value);
        changed |= ApplyString(target.ContactPerson, source.ContactPerson, preferIncoming, value => target.ContactPerson = value);
        changed |= ApplyString(target.BusinessNumber, source.BusinessNumber, preferIncoming, value => target.BusinessNumber = value);
        changed |= ApplyString(target.Address, source.Address, preferIncoming, value => target.Address = value);
        changed |= ApplyString(target.DetailAddress, source.DetailAddress, preferIncoming, value => target.DetailAddress = value);
        changed |= ApplyString(target.Phone, source.Phone, preferIncoming, value => target.Phone = value);
        changed |= ApplyString(target.MobilePhone, source.MobilePhone, preferIncoming, value => target.MobilePhone = value);
        changed |= ApplyString(target.FaxNumber, source.FaxNumber, preferIncoming, value => target.FaxNumber = value);
        changed |= ApplyString(target.Email, source.Email, preferIncoming, value => target.Email = value);
        changed |= ApplyString(target.HomePage, source.HomePage, preferIncoming, value => target.HomePage = value);
        changed |= ApplyString(target.Representative, source.Representative, preferIncoming, value => target.Representative = value);
        changed |= ApplyString(target.BusinessType, source.BusinessType, preferIncoming, value => target.BusinessType = value);
        changed |= ApplyString(target.BusinessItem, source.BusinessItem, preferIncoming, value => target.BusinessItem = value);
        changed |= ApplyString(target.Recipient, source.Recipient, preferIncoming, value => target.Recipient = value);
        changed |= ApplyString(target.PriceGrade, NormalizePriceGrade(source.PriceGrade), preferIncoming, value => target.PriceGrade = value);
        changed |= ApplyString(target.ResponsibleOfficeCode, NormalizeOfficeCode(source.ResponsibleOfficeCode), preferIncoming, value => target.ResponsibleOfficeCode = value);
        changed |= ApplyMergedLines(target.Notes, source.Notes, value => target.Notes = value);

        var createdAt = ChooseCreatedAt(target.CreatedAtUtc, source.CreatedAtUtc);
        if (createdAt != target.CreatedAtUtc)
        {
            target.CreatedAtUtc = createdAt;
            changed = true;
        }

        var updatedAt = ChooseUpdatedAt(target.UpdatedAtUtc, source.UpdatedAtUtc);
        if (updatedAt != target.UpdatedAtUtc)
        {
            target.UpdatedAtUtc = updatedAt;
            changed = true;
        }

        var revision = Math.Max(target.Revision, source.Revision);
        if (revision != target.Revision)
        {
            target.Revision = revision;
            changed = true;
        }

        if (target.IsDeleted)
        {
            target.IsDeleted = false;
            changed = true;
        }

        return changed;
    }

    private static bool MergeItem(LocalItem target, LocalItem source, bool preferIncoming)
    {
        var changed = false;

        changed |= ApplyString(target.NameOriginal, source.NameOriginal, preferIncoming, value => target.NameOriginal = value);
        var nameMatchKey = BuildMatchKey(target.NameOriginal);
        if (!string.Equals(target.NameMatchKey, nameMatchKey, StringComparison.Ordinal))
        {
            target.NameMatchKey = nameMatchKey;
            changed = true;
        }

        changed |= ApplyString(target.SpecificationOriginal, source.SpecificationOriginal, preferIncoming, value => target.SpecificationOriginal = value);
        var specMatchKey = BuildMatchKey(target.SpecificationOriginal);
        if (!string.Equals(target.SpecificationMatchKey, specMatchKey, StringComparison.Ordinal))
        {
            target.SpecificationMatchKey = specMatchKey;
            changed = true;
        }

        changed |= ApplyString(target.CategoryName, source.CategoryName, preferIncoming, value => target.CategoryName = value);
        changed |= ApplyString(target.Unit, source.Unit, preferIncoming, value => target.Unit = value);
        changed |= ApplyDecimal(target.BoxQuantity, source.BoxQuantity, preferIncoming, value => target.BoxQuantity = value);
        changed |= ApplyString(target.StorageLocation, source.StorageLocation, preferIncoming, value => target.StorageLocation = value);
        changed |= ApplyDecimal(target.CurrentStock, source.CurrentStock, preferIncoming, value => target.CurrentStock = value);
        changed |= ApplyDecimal(target.SafetyStock, source.SafetyStock, preferIncoming, value => target.SafetyStock = value);
        changed |= ApplyDecimal(target.PurchasePrice, source.PurchasePrice, preferIncoming, value => target.PurchasePrice = value);
        changed |= ApplyDecimal(target.SalePrice, source.SalePrice, preferIncoming, value => target.SalePrice = value);
        changed |= ApplyDecimal(target.RetailPrice, source.RetailPrice, preferIncoming, value => target.RetailPrice = value);
        changed |= ApplyDecimal(target.PriceGradeA, source.PriceGradeA, preferIncoming, value => target.PriceGradeA = value);
        changed |= ApplyDecimal(target.PriceGradeB, source.PriceGradeB, preferIncoming, value => target.PriceGradeB = value);
        changed |= ApplyDecimal(target.PriceGradeC, source.PriceGradeC, preferIncoming, value => target.PriceGradeC = value);
        changed |= ApplyNullableDateOnly(target.LastPurchaseDate, source.LastPurchaseDate, preferIncoming, value => target.LastPurchaseDate = value);
        changed |= ApplyNullableDateOnly(target.LastSaleDate, source.LastSaleDate, preferIncoming, value => target.LastSaleDate = value);
        changed |= ApplyString(target.SimpleMemo, source.SimpleMemo, preferIncoming, value => target.SimpleMemo = value);
        changed |= ApplyBool(target.IsRental, source.IsRental, preferIncoming, value => target.IsRental = value);
        changed |= ApplyBool(target.IsSale, source.IsSale, preferIncoming, value => target.IsSale = value, fillFalseWhenEmpty: true);
        changed |= ApplyString(target.SerialNumber, source.SerialNumber, preferIncoming, value => target.SerialNumber = value);
        changed |= ApplyString(target.MaterialNumber, source.MaterialNumber, preferIncoming, value => target.MaterialNumber = value);
        changed |= ApplyString(target.InstallLocation, source.InstallLocation, preferIncoming, value => target.InstallLocation = value);
        changed |= ApplyNullableDateOnly(target.RentalStartDate, source.RentalStartDate, preferIncoming, value => target.RentalStartDate = value);
        changed |= ApplyNullableDateOnly(target.RentalEndDate, source.RentalEndDate, preferIncoming, value => target.RentalEndDate = value);
        changed |= ApplyMergedLines(target.Notes, source.Notes, value => target.Notes = value);

        var createdAt = ChooseCreatedAt(target.CreatedAtUtc, source.CreatedAtUtc);
        if (createdAt != target.CreatedAtUtc)
        {
            target.CreatedAtUtc = createdAt;
            changed = true;
        }

        var updatedAt = ChooseUpdatedAt(target.UpdatedAtUtc, source.UpdatedAtUtc);
        if (updatedAt != target.UpdatedAtUtc)
        {
            target.UpdatedAtUtc = updatedAt;
            changed = true;
        }

        var revision = Math.Max(target.Revision, source.Revision);
        if (revision != target.Revision)
        {
            target.Revision = revision;
            changed = true;
        }

        if (target.IsDeleted)
        {
            target.IsDeleted = false;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyString(string current, string? incoming, bool preferIncoming, Action<string> apply)
    {
        var normalizedIncoming = incoming?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedIncoming))
            return false;

        var normalizedTarget = current?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            apply(normalizedIncoming);
            return true;
        }

        if (preferIncoming && !string.Equals(normalizedTarget, normalizedIncoming, StringComparison.Ordinal))
        {
            apply(normalizedIncoming);
            return true;
        }

        return false;
    }

    private static bool ApplyNullableGuid(Guid? current, Guid? incoming, Action<Guid?> apply)
    {
        if (current.HasValue || !incoming.HasValue)
            return false;

        apply(incoming);
        return true;
    }

    private static bool ApplyDecimal(decimal current, decimal incoming, bool preferIncoming, Action<decimal> apply)
    {
        if (incoming == 0m)
            return false;

        if (current == 0m)
        {
            apply(incoming);
            return true;
        }

        if (preferIncoming && current != incoming)
        {
            apply(incoming);
            return true;
        }

        return false;
    }

    private static bool ApplyNullableDateOnly(DateOnly? current, DateOnly? incoming, bool preferIncoming, Action<DateOnly?> apply)
    {
        if (!incoming.HasValue)
            return false;

        if (!current.HasValue)
        {
            apply(incoming);
            return true;
        }

        if (preferIncoming && current.Value != incoming.Value)
        {
            apply(incoming);
            return true;
        }

        return false;
    }

    private static bool ApplyBool(bool current, bool incoming, bool preferIncoming, Action<bool> apply, bool fillFalseWhenEmpty = false)
    {
        if (!preferIncoming && !fillFalseWhenEmpty)
            return false;

        if (current == incoming)
            return false;

        if (preferIncoming || (fillFalseWhenEmpty && incoming))
        {
            apply(incoming);
            return true;
        }

        return false;
    }

    private static bool ApplyMergedLines(string current, string? incoming, Action<string> apply)
    {
        var merged = MergeLines(current, incoming);
        if (string.Equals(current ?? string.Empty, merged, StringComparison.Ordinal))
            return false;

        apply(merged);
        return true;
    }

    private static string MergeLines(string? existing, string? incoming)
    {
        var lines = new List<string>();
        AddDistinctLines(lines, existing);
        AddDistinctLines(lines, incoming);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddDistinctLines(ICollection<string> lines, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        var existing = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
        foreach (var line in source
                     .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(current => current.Trim())
                     .Where(current => !string.IsNullOrWhiteSpace(current)))
        {
            if (existing.Add(line))
                lines.Add(line);
        }
    }

    private static DateTime ChooseCreatedAt(DateTime current, DateTime incoming)
    {
        current = NormalizeUtc(current);
        incoming = NormalizeUtc(incoming);

        if (current == DateTime.UnixEpoch)
            return incoming;
        if (incoming == DateTime.UnixEpoch)
            return current;

        return current <= incoming ? current : incoming;
    }

    private static DateTime ChooseUpdatedAt(DateTime current, DateTime incoming)
    {
        current = NormalizeUtc(current);
        incoming = NormalizeUtc(incoming);

        if (current == DateTime.UnixEpoch)
            return incoming;
        if (incoming == DateTime.UnixEpoch)
            return current;

        return current >= incoming ? current : incoming;
    }

    private static DateTime ToUtc(DateOnly? value)
    {
        if (!value.HasValue)
            return DateTime.UnixEpoch;

        var local = value.Value.ToDateTime(TimeOnly.MinValue);
        return NormalizeUtc(DateTime.SpecifyKind(local, DateTimeKind.Local));
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UnixEpoch;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
            _ => value.ToUniversalTime()
        };
    }

    private static string NormalizeTradeType(string? value)
        => 거래플랜.Shared.Contracts.CustomerClassificationNormalizer.NormalizeTradeTypeOrDefault(value);

    private static string NormalizePriceGrade(string? value)
        => string.IsNullOrWhiteSpace(value) ? "매출단가" : value.Trim();

    private static string NormalizeOfficeCode(string? value)
        => string.IsNullOrWhiteSpace(value) ? DomainConstants.OfficeUsenet : value.Trim();

    private static string BuildMatchKey(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;
}

public sealed record LegacyAutoMigrationResult(
    bool Applied,
    string SourceType,
    string SourcePath,
    LegacyImportResult? ImportResult,
    string Message);
