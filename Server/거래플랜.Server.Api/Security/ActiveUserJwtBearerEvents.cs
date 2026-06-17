using System.Security.Claims;
using 거래플랜.Server.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace 거래플랜.Server.Api.Security;

public interface IActiveUserSessionValidator
{
    Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken);
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
        var userIdValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            context.Fail("JWT 사용자 식별자를 확인할 수 없습니다.");
            return;
        }

        if (!await _validator.IsActiveUserAsync(userId, context.HttpContext.RequestAborted))
            context.Fail("비활성화되었거나 삭제된 사용자입니다.");
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
        var centralConnection = _connectionResolver.ResolveCentral();
        return centralConnection.UseSqlite
            ? await IsActiveSqliteUserAsync(centralConnection.ConnectionString, userId, cancellationToken)
            : await IsActivePostgresUserAsync(centralConnection.ConnectionString, userId, cancellationToken);
    }

    private static async Task<bool> IsActiveSqliteUserAsync(
        string connectionString,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM "Users"
            WHERE UPPER("Id") = UPPER($id)
              AND COALESCE("IsActive", 1) = 1
              AND COALESCE("IsDeleted", 0) = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", userId.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<bool> IsActivePostgresUserAsync(
        string connectionString,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM "Users"
            WHERE "Id" = @id
              AND COALESCE("IsActive", TRUE) = TRUE
              AND COALESCE("IsDeleted", FALSE) = FALSE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("id", userId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }
}
