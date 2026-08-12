using System.Text.Json;
using 거래플랜.Server.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DbUpdateConcurrencyExceptionMiddlewareTests
{
    [Fact]
    public async Task Mapper_ReturnsOnlySafeMetadata_WithOriginalAndActualRevision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ConcurrencyTestDbContext>()
            .UseSqlite(connection)
            .Options;
        var entityId = Guid.NewGuid();

        await using var staleContext = new ConcurrencyTestDbContext(options);
        await staleContext.Database.EnsureCreatedAsync();
        staleContext.Entities.Add(new ConcurrencyTestEntity
        {
            Id = entityId,
            Revision = 10,
            SecretValue = "database-secret-before-conflict"
        });
        await staleContext.SaveChangesAsync();
        staleContext.ChangeTracker.Clear();

        var staleEntity = await staleContext.Entities.SingleAsync(entity => entity.Id == entityId);
        await using (var competingContext = new ConcurrencyTestDbContext(options))
        {
            var competingEntity = await competingContext.Entities.SingleAsync(entity => entity.Id == entityId);
            competingEntity.Revision = 11;
            competingEntity.SecretValue = "database-secret-after-conflict";
            await competingContext.SaveChangesAsync();
        }

        staleEntity.SecretValue = "client-secret-that-must-not-leak";
        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleContext.SaveChangesAsync());

        var response = await DbUpdateConcurrencyConflictMapper.MapAsync(exception);

        var conflict = Assert.Single(response.Conflicts);
        Assert.Equal(nameof(ConcurrencyTestEntity), conflict.EntityName);
        Assert.Equal<Guid?>(entityId, conflict.EntityId);
        Assert.Equal<long?>(10L, conflict.OriginalRevision);
        Assert.Equal<long?>(11L, conflict.ActualRevision);

        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("database-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Middleware_ReturnsSafeJsonConflict_WithoutLeakingExceptionMessage()
    {
        const string sensitiveExceptionMessage = "password=must-never-be-returned";
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new DbUpdateConcurrencyExceptionMiddleware(
            _ => Task.FromException(new DbUpdateConcurrencyException(sensitiveExceptionMessage)),
            NullLogger<DbUpdateConcurrencyExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType, StringComparison.OrdinalIgnoreCase);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("concurrency_conflict", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("conflicts").ValueKind);
        Assert.DoesNotContain(sensitiveExceptionMessage, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Middleware_RethrowsConcurrencyException_WhenResponseAlreadyStarted()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var exception = new DbUpdateConcurrencyException("must be rethrown");
        var middleware = new DbUpdateConcurrencyExceptionMiddleware(
            _ => Task.FromException(exception),
            NullLogger<DbUpdateConcurrencyExceptionMiddleware>.Instance);

        var thrown = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => middleware.InvokeAsync(context));

        Assert.Same(exception, thrown);
    }

    private sealed class ConcurrencyTestDbContext(DbContextOptions<ConcurrencyTestDbContext> options)
        : DbContext(options)
    {
        public DbSet<ConcurrencyTestEntity> Entities => Set<ConcurrencyTestEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConcurrencyTestEntity>().HasKey(entity => entity.Id);
            modelBuilder.Entity<ConcurrencyTestEntity>()
                .Property(entity => entity.Revision)
                .IsConcurrencyToken();
        }
    }

    private sealed class ConcurrencyTestEntity
    {
        public Guid Id { get; set; }
        public long Revision { get; set; }
        public string SecretValue { get; set; } = string.Empty;
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
