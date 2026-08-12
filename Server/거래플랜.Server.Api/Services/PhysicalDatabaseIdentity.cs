using System.Security.Cryptography;
using System.Text;
using 거래플랜.Server.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace 거래플랜.Server.Api.Services;

public static class PhysicalDatabaseIdentity
{
    public static string FromConnectionInfo(TenantDatabaseConnectionInfo connectionInfo)
    {
        if (string.IsNullOrWhiteSpace(connectionInfo.ConnectionString))
            throw new InvalidOperationException("Database connection string is empty.");

        if (connectionInfo.UseSqlite)
        {
            var builder = new SqliteConnectionStringBuilder(connectionInfo.ConnectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource))
                throw new InvalidOperationException("SQLite data source is empty.");

            var dataSource = string.Equals(
                builder.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase)
                ? ":memory:"
                : Path.GetFullPath(builder.DataSource);
            if (OperatingSystem.IsWindows() &&
                !string.Equals(
                    dataSource,
                    ":memory:",
                    StringComparison.Ordinal))
            {
                dataSource = dataSource.ToUpperInvariant();
            }
            return $"sqlite|{dataSource}";
        }

        var npgsqlBuilder = new NpgsqlConnectionStringBuilder(connectionInfo.ConnectionString);
        if (string.IsNullOrWhiteSpace(npgsqlBuilder.Host))
            throw new InvalidOperationException("PostgreSQL host is empty.");

        var effectiveDatabase = string.IsNullOrWhiteSpace(npgsqlBuilder.Database)
            ? npgsqlBuilder.Username ?? string.Empty
            : npgsqlBuilder.Database;
        return $"postgres|{npgsqlBuilder.Host.Trim().ToLowerInvariant()}|{npgsqlBuilder.Port}|{effectiveDatabase}";
    }

    public static string FromDbContext(AppDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        return FromConnectionInfo(new TenantDatabaseConnectionInfo
        {
            UseSqlite = dbContext.Database.IsSqlite(),
            ConnectionString = connection.ConnectionString
        });
    }

    public static string GetStorageNamespace(AppDbContext dbContext)
    {
        var identityBytes = Encoding.UTF8.GetBytes(FromDbContext(dbContext));
        return $"db-{Convert.ToHexString(SHA256.HashData(identityBytes))}";
    }
}
