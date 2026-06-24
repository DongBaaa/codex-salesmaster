using System.Security.Claims;
using 거래플랜.Shared.Contracts;
using 거래플랜.Server.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace 거래플랜.Server.Api.Security;

public interface IActiveUserSessionValidator
{
    Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<bool> IsCurrentTokenAsync(Guid userId, ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class ActiveUserJwtBearerEvents : JwtBearerEvents
{
    private readonly IActiveUserSessionValidator _validator;

    public ActiveUserJwtBearerEvents(IActiveUserSessionValidator validator)
    {
        _validator = validator;
    }

    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userIdValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            context.Fail("JWT 사용자 식별자를 확인할 수 없습니다.");
            return;
        }

        if (principal is null || !await _validator.IsCurrentTokenAsync(userId, principal, context.HttpContext.RequestAborted))
            context.Fail("비활성화되었거나 삭제/권한/범위가 변경된 사용자입니다. 다시 로그인하세요.");
    }
}

public sealed class ActiveUserSessionValidator : IActiveUserSessionValidator
{
    private readonly ITenantDatabaseConnectionResolver _connectionResolver;

    public ActiveUserSessionValidator(ITenantDatabaseConnectionResolver connectionResolver)
    {
        _connectionResolver = connectionResolver;
    }

    public async Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var snapshot = await LoadUserSessionSnapshotAsync(userId, cancellationToken);
        return snapshot is not null;
    }

    public async Task<bool> IsCurrentTokenAsync(
        Guid userId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadUserSessionSnapshotAsync(userId, cancellationToken);
        return snapshot is not null && TokenClaimsMatch(snapshot, principal);
    }

    private async Task<ActiveUserSessionSnapshot?> LoadUserSessionSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var centralConnection = _connectionResolver.ResolveCentral();
        return centralConnection.UseSqlite
            ? await LoadSqliteUserSessionSnapshotAsync(centralConnection.ConnectionString, userId, cancellationToken)
            : await LoadPostgresUserSessionSnapshotAsync(centralConnection.ConnectionString, userId, cancellationToken);
    }

    private static async Task<ActiveUserSessionSnapshot?> LoadSqliteUserSessionSnapshotAsync(
        string connectionString,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var userCommand = connection.CreateCommand();
        userCommand.CommandText =
            """
            SELECT "Role", "TenantCode", "OfficeCode", "ScopeType"
            FROM "Users"
            WHERE UPPER("Id") = UPPER($id)
              AND COALESCE("IsActive", 1) = 1
              AND COALESCE("IsDeleted", 0) = 0
            LIMIT 1;
            """;
        userCommand.Parameters.AddWithValue("$id", userId.ToString());

        string? role;
        string? tenantCode;
        string? officeCode;
        string? scopeType;
        await using (var reader = await userCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            role = ReadNullableString(reader, 0);
            tenantCode = ReadNullableString(reader, 1);
            officeCode = ReadNullableString(reader, 2);
            scopeType = ReadNullableString(reader, 3);
        }

        await using var permissionsCommand = connection.CreateCommand();
        permissionsCommand.CommandText =
            """
            SELECT "Permission"
            FROM "UserPermissions"
            WHERE UPPER("UserId") = UPPER($id)
            ORDER BY "Permission";
            """;
        permissionsCommand.Parameters.AddWithValue("$id", userId.ToString());

        var permissions = new List<string>();
        await using (var permissionsReader = await permissionsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await permissionsReader.ReadAsync(cancellationToken))
            {
                var permission = ReadNullableString(permissionsReader, 0);
                if (!string.IsNullOrWhiteSpace(permission))
                    permissions.Add(permission);
            }
        }

        return ActiveUserSessionSnapshot.Create(role, tenantCode, officeCode, scopeType, permissions);
    }

    private static async Task<ActiveUserSessionSnapshot?> LoadPostgresUserSessionSnapshotAsync(
        string connectionString,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var userCommand = connection.CreateCommand();
        userCommand.CommandText =
            """
            SELECT "Role", "TenantCode", "OfficeCode", "ScopeType"
            FROM "Users"
            WHERE "Id" = @id
              AND COALESCE("IsActive", TRUE) = TRUE
              AND COALESCE("IsDeleted", FALSE) = FALSE
            LIMIT 1;
            """;
        userCommand.Parameters.AddWithValue("id", userId);

        string? role;
        string? tenantCode;
        string? officeCode;
        string? scopeType;
        await using (var reader = await userCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            role = ReadNullableString(reader, 0);
            tenantCode = ReadNullableString(reader, 1);
            officeCode = ReadNullableString(reader, 2);
            scopeType = ReadNullableString(reader, 3);
        }

        await using var permissionsCommand = connection.CreateCommand();
        permissionsCommand.CommandText =
            """
            SELECT "Permission"
            FROM "UserPermissions"
            WHERE "UserId" = @id
            ORDER BY "Permission";
            """;
        permissionsCommand.Parameters.AddWithValue("id", userId);

        var permissions = new List<string>();
        await using (var permissionsReader = await permissionsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await permissionsReader.ReadAsync(cancellationToken))
            {
                var permission = ReadNullableString(permissionsReader, 0);
                if (!string.IsNullOrWhiteSpace(permission))
                    permissions.Add(permission);
            }
        }

        return ActiveUserSessionSnapshot.Create(role, tenantCode, officeCode, scopeType, permissions);
    }

    private static bool TokenClaimsMatch(ActiveUserSessionSnapshot snapshot, ClaimsPrincipal principal)
    {
        if (!SingleClaimEquals(principal, ClaimTypes.Role, snapshot.Role, StringComparer.Ordinal))
            return false;

        if (!SingleClaimEquals(
                principal,
                "tenant",
                snapshot.TenantCode,
                StringComparer.OrdinalIgnoreCase,
                value => TenantScopeCatalog.NormalizeTenantCodeOrDefault(value)))
        {
            return false;
        }

        if (!SingleClaimEquals(
                principal,
                "office",
                snapshot.OfficeCode,
                StringComparer.OrdinalIgnoreCase,
                value => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(value)))
        {
            return false;
        }

        if (!SingleClaimEquals(
                principal,
                "scope",
                snapshot.ScopeType,
                StringComparer.OrdinalIgnoreCase,
                value => TenantScopeCatalog.NormalizeScopeTypeOrDefault(value)))
        {
            return false;
        }

        var tokenPermissions = principal.Claims
            .Where(claim => string.Equals(claim.Type, "perm", StringComparison.Ordinal))
            .Select(claim => claim.Value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return tokenPermissions.SequenceEqual(snapshot.Permissions, StringComparer.Ordinal);
    }

    private static bool SingleClaimEquals(
        ClaimsPrincipal principal,
        string claimType,
        string expected,
        StringComparer comparer,
        Func<string?, string>? normalize = null)
    {
        var values = principal.Claims
            .Where(claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            .Select(claim => normalize is null ? claim.Value?.Trim() ?? string.Empty : normalize(claim.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(comparer)
            .ToArray();

        return values.Length == 1 && comparer.Equals(values[0], expected);
    }

    private static string? ReadNullableString(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record ActiveUserSessionSnapshot(
        string Role,
        string TenantCode,
        string OfficeCode,
        string ScopeType,
        IReadOnlyList<string> Permissions)
    {
        public static ActiveUserSessionSnapshot Create(
            string? role,
            string? tenantCode,
            string? officeCode,
            string? scopeType,
            IEnumerable<string> permissions)
        {
            var normalizedRole = string.IsNullOrWhiteSpace(role) ? "User" : role.Trim();
            var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
            var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOfficeCode);
            var normalizedScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(
                scopeType,
                TenantScopeCatalog.ScopeOfficeOnly);

            var normalizedPermissions = permissions
                .Select(permission => permission.Trim())
                .Where(permission => !string.IsNullOrWhiteSpace(permission))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(permission => permission, StringComparer.Ordinal)
                .ToArray();

            return new ActiveUserSessionSnapshot(
                normalizedRole,
                normalizedTenantCode,
                normalizedOfficeCode,
                normalizedScopeType,
                normalizedPermissions);
        }
    }
}
