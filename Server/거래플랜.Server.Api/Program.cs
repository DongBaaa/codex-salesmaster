using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// The default host can include the Windows EventLog provider. On non-admin test
// workstations that provider may throw "access denied" while logging startup
// warnings, preventing the local test server from becoming ready. Keep logging to
// console/debug/event-source so Linux PC/container logs and local test logs continue
// to work without requiring Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddEventSourceLogger();

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<SeedUsersOptions>(builder.Configuration.GetSection(SeedUsersOptions.SectionName));
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
builder.Services.Configure<CentralFileStorageOptions>(builder.Configuration.GetSection(CentralFileStorageOptions.SectionName));

var dataProtectionKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (string.IsNullOrWhiteSpace(dataProtectionKeyRingPath) &&
    !builder.Environment.IsDevelopment() &&
    Directory.Exists("/storage"))
{
    dataProtectionKeyRingPath = "/storage/data-protection-keys";
}

if (!string.IsNullOrWhiteSpace(dataProtectionKeyRingPath))
{
    var resolvedDataProtectionKeyRingPath = Path.IsPathRooted(dataProtectionKeyRingPath)
        ? dataProtectionKeyRingPath
        : Path.Combine(AppContext.BaseDirectory, dataProtectionKeyRingPath);
    Directory.CreateDirectory(resolvedDataProtectionKeyRingPath);
    builder.Services.AddDataProtection()
        .SetApplicationName("GeoraePlan.Server.Api")
        .PersistKeysToFileSystem(new DirectoryInfo(resolvedDataProtectionKeyRingPath));
}

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var securityOptions = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
var seedUsersOptions = builder.Configuration.GetSection(SeedUsersOptions.SectionName).Get<SeedUsersOptions>() ?? new SeedUsersOptions();
var allowedCorsOrigins = securityOptions.AllowedCorsOrigins
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? string.Empty;
var dedicatedBusinessConnections = ResolveDedicatedBusinessConnections(builder.Configuration, connectionString);
var sqliteFallbackEnabled = builder.Configuration.GetValue("Database:EnableSqliteFallback", true);
var forceSqlite = string.Equals(Environment.GetEnvironmentVariable("ERP_DB_FALLBACK_SQLITE"), "1", StringComparison.Ordinal);
var forcePostgres = string.Equals(Environment.GetEnvironmentVariable("ERP_FORCE_POSTGRES"), "1", StringComparison.Ordinal);
var sqliteDbPath = ResolveSqliteFallbackPath();

var useSqlite = forceSqlite ||
                (!forcePostgres &&
                 (string.IsNullOrWhiteSpace(connectionString) ||
                  (sqliteFallbackEnabled && !TryCanConnectPostgres(connectionString))));

var tenantDatabaseRoutingOptions = new TenantDatabaseRoutingOptions
{
    UseSqlite = useSqlite,
    SqliteDbPath = sqliteDbPath,
    DefaultConnectionString = connectionString,
    DedicatedBusinessConnections = dedicatedBusinessConnections
};

ValidateProductionSecurityConfiguration(
    builder.Environment,
    connectionString,
    dedicatedBusinessConnections,
    jwtOptions,
    securityOptions,
    seedUsersOptions,
    useSqlite);

if (useSqlite)
{
    Console.WriteLine($"[거래플랜 API] PostgreSQL 연결 불가. SQLite 대체 사용: {sqliteDbPath}");
}
else
{
    Console.WriteLine("[거래플랜 API] PostgreSQL 연결 성공");
}

builder.Services.AddSingleton(tenantDatabaseRoutingOptions);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantDatabaseConnectionResolver, TenantDatabaseConnectionResolver>();
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var resolvedConnection = serviceProvider.GetRequiredService<ITenantDatabaseConnectionResolver>().ResolveCurrent();
    if (resolvedConnection.UseSqlite)
    {
        options.UseSqlite(
            resolvedConnection.ConnectionString,
            sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }
    else
    {
        options.UseNpgsql(
            resolvedConnection.ConnectionString,
            npgsqlOptions => npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
    }
});

builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddScoped<OfficeScopeService>();
builder.Services.AddScoped<InventoryLedgerService>();
builder.Services.AddScoped<InvoiceStockSnapshotService>();
builder.Services.AddScoped<RentalSettlementRecalculationService>();
builder.Services.AddScoped<RentalAssignmentHistoryService>();
builder.Services.AddScoped<IJwtTokenFactory, JwtTokenFactory>();
builder.Services.AddScoped<IActiveUserSessionValidator, ActiveUserSessionValidator>();
builder.Services.AddScoped<ActiveUserJwtBearerEvents>();
builder.Services.AddScoped<IInvoiceNumberService, InvoiceNumberService>();
builder.Services.AddSingleton<RevisionClock>();
builder.Services.AddSingleton<ICentralFileStorage, CentralFileStorage>();
builder.Services.AddSingleton<DatabaseInitializationState>();
builder.Services.AddHostedService<StartupQueryWarmupService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.EventsType = typeof(ActiveUserJwtBearerEvents);
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("AdminOrGod", policy =>
    {
        policy.RequireAssertion(context => IsGodUser(context.User) || context.User.IsInRole("Admin"));
    });
    AddPermissionPolicy(options, PermissionNames.CompanyProfileEdit);
    AddPermissionPolicy(options, PermissionNames.AmountViewSales);
    AddPermissionPolicy(options, PermissionNames.AmountViewPurchase);
    AddPermissionPolicy(options, PermissionNames.SettingsEdit);
    AddPermissionPolicy(options, PermissionNames.DataBackupRestore);
    AddPermissionPolicy(options, PermissionNames.CustomerEdit);
    AddPermissionPolicy(options, PermissionNames.ItemEdit);
    AddPermissionPolicy(options, PermissionNames.InvoiceEdit);
    AddPermissionPolicy(options, PermissionNames.PaymentEdit);
    AddPermissionPolicy(options, PermissionNames.InventoryReset);
    AddPermissionPolicy(options, PermissionNames.RentalProfileEdit);
    AddPermissionPolicy(options, PermissionNames.RentalAssetEdit);
    AddPermissionPolicy(options, PermissionNames.DeliveryEdit);
    AddPermissionPolicy(options, PermissionNames.RentalViewAll);
    AddPermissionPolicy(options, PermissionNames.RentalEditAll);
    AddPermissionPolicy(options, PermissionNames.DeliveryViewAll);
    AddPermissionPolicy(options, PermissionNames.RentalSettingsEdit);
    AddPermissionPolicy(options, PermissionNames.RentalImport);
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("DesktopClient", policy =>
    {
        if (securityOptions.AllowAnyCorsOrigin || builder.Environment.IsDevelopment())
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        if (allowedCorsOrigins.Length > 0)
        {
            policy.WithOrigins(allowedCorsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
            return;
        }

        policy.SetIsOriginAllowed(_ => false)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

if (securityOptions.EnableRateLimiting)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
            await context.HttpContext.Response.WriteAsJsonAsync(
                new
                {
                    error = "rate_limited",
                    message = "요청이 너무 많습니다. 잠시 후 다시 시도하세요."
                },
                cancellationToken: token);
        };

        var loginPermitLimit = Math.Max(1, securityOptions.LoginPermitLimitPerMinute);
        var apiPermitLimit = Math.Max(1, securityOptions.ApiPermitLimitPerMinute);

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var path = httpContext.Request.Path.Value ?? string.Empty;
            var permitLimit = path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase)
                ? loginPermitLimit
                : apiPermitLimit;

            return RateLimitPartition.GetFixedWindowLimiter(
                ResolveRateLimitKey(httpContext, path),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        });
    });
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "거래플랜 API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. 예: 'Bearer {token}'",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

LogSecurityWarnings(app, connectionString, dedicatedBusinessConnections, jwtOptions, securityOptions, useSqlite, allowedCorsOrigins);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

if (securityOptions.AddSecurityHeaders)
{
    app.Use(async (context, next) =>
    {
        if (securityOptions.RequireHttpsForwardedProto &&
            !app.Environment.IsDevelopment() &&
            !string.Equals(context.Request.Path.Value, "/healthz", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(context.Request.Path.Value, "/readyz", StringComparison.OrdinalIgnoreCase) &&
            !context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "https_required",
                message = "HTTPS reverse proxy를 통해 접속해야 합니다."
            });
            return;
        }

        context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
        context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
        context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        if (context.Request.IsHttps)
        {
            context.Response.Headers.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }

        await next();
    });
}

if (securityOptions.EnableRateLimiting)
{
    app.UseRateLimiter();
}

app.UseCors("DesktopClient");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    utc = DateTime.UtcNow
})).AllowAnonymous();

app.MapGet("/readyz", async Task<IResult> (
    DatabaseInitializationState databaseInitializationState,
    IServiceScopeFactory scopeFactory,
    CancellationToken cancellationToken) =>
{
    var snapshot = databaseInitializationState.CreateSnapshot();
    if (snapshot.Failed)
    {
        return Results.Json(new
        {
            status = "not_ready",
            reason = "database_initialization_failed",
            databaseInitialization = snapshot,
            utc = DateTime.UtcNow
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!snapshot.Completed)
    {
        return Results.Json(new
        {
            status = "starting",
            reason = snapshot.Started ? "database_initialization_running" : "database_initialization_not_started",
            databaseInitialization = snapshot,
            utc = DateTime.UtcNow
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            return Results.Json(new
            {
                status = "not_ready",
                reason = "database_connection_unavailable",
                databaseInitialization = snapshot,
                utc = DateTime.UtcNow
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            status = "not_ready",
            reason = "database_connection_check_failed",
            message = ex.Message,
            databaseInitialization = snapshot,
            utc = DateTime.UtcNow
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        status = "ready",
        databaseInitialization = snapshot,
        utc = DateTime.UtcNow
    });
}).AllowAnonymous();

var databaseInitializationState = app.Services.GetRequiredService<DatabaseInitializationState>();
var databaseInitializationTask = Task.Run(async () =>
{
    databaseInitializationState.MarkStarted();
    try
    {
        await DbInitializer.InitializeAsync(app.Services);
        databaseInitializationState.MarkCompleted();
        app.Logger.LogInformation("Database initialization completed.");
    }
    catch (Exception ex)
    {
        databaseInitializationState.MarkFailed(ex);
        app.Logger.LogCritical(ex, "Database initialization failed.");
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    if (!databaseInitializationTask.IsCompleted)
        app.Logger.LogWarning("Database initialization is still running while the application is stopping.");
});

app.Run();

static bool TryCanConnectPostgres(string connectionString)
{
    try
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 2, CommandTimeout = 2, Pooling = false };
        using var connection = new NpgsqlConnection(csb.ConnectionString);
        connection.Open();
        return true;
    }
    catch
    {
        return false;
    }
}

static string ResolveSqliteFallbackPath()
{
    var baseDirectory = AppContext.BaseDirectory;
    var currentPath = Path.Combine(baseDirectory, "거래플랜-local.db");
    var legacySqlitePath = Path.Combine(baseDirectory, "salesmaster-local.db");

    try
    {
        if (!File.Exists(currentPath) && File.Exists(legacySqlitePath))
            File.Copy(legacySqlitePath, currentPath);
    }
    catch
    {
        // Ignore migration copy failures and let SQLite create/use the current path directly.
    }

    return currentPath;
}

static void AddPermissionPolicy(AuthorizationOptions options, string permission)
{
    options.AddPolicy(permission, policy =>
    {
        policy.RequireAssertion(context =>
            context.User.IsInRole("Admin") ||
            IsGodUser(context.User) ||
            context.User.Claims.Any(claim => claim.Type == "perm" && claim.Value == permission));
    });
}

static bool IsGodUser(System.Security.Claims.ClaimsPrincipal user)
    => user.Claims.Any(claim => claim.Type == "god" && string.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase));

static string ResolveRateLimitKey(HttpContext context, string path)
{
    if (path.StartsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
    {
        var loginKey = context.Connection.RemoteIpAddress?.ToString();
        return $"login:{loginKey ?? "anonymous"}";
    }

    var subject = context.User.Identity?.IsAuthenticated == true
        ? context.User.Identity?.Name
        : null;

    subject ??= context.Connection.RemoteIpAddress?.ToString();
    return $"api:{subject ?? "anonymous"}";
}

static void ValidateProductionSecurityConfiguration(
    IWebHostEnvironment environment,
    string connectionString,
    IReadOnlyDictionary<string, string> dedicatedBusinessConnections,
    JwtOptions jwtOptions,
    SecurityOptions securityOptions,
    SeedUsersOptions seedUsersOptions,
    bool useSqlite)
{
    if (environment.IsDevelopment())
        return;

    if (useSqlite)
        throw new InvalidOperationException("Production environment cannot start with SQLite fallback enabled.");

    if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) ||
        jwtOptions.SigningKey.Length < 32 ||
        jwtOptions.SigningKey.Contains("ChangeThis", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Production JWT signing key must be replaced with a strong secret.");
    }

    if (ContainsInsecureConnectionStringSecret(connectionString))
        throw new InvalidOperationException("Production database password cannot use a sample or placeholder value.");

    if (!securityOptions.RequireHttpsForwardedProto)
        throw new InvalidOperationException("Production requires Security:RequireHttpsForwardedProto=true.");

    foreach (var pair in dedicatedBusinessConnections)
    {
        if (ContainsInsecureConnectionStringSecret(pair.Value))
            throw new InvalidOperationException($"Production database password for tenant '{pair.Key}' cannot use a sample or placeholder value.");
    }

    if (!seedUsersOptions.EnableSeedUsers)
        return;

    if (seedUsersOptions.UsesDefaultAdminPassword() ||
        seedUsersOptions.UsesDefaultUserPassword() ||
        seedUsersOptions.UsesDefaultItwPassword())
    {
        throw new InvalidOperationException("Production seed users cannot use default passwords.");
    }
}
static void LogSecurityWarnings(
    WebApplication app,
    string connectionString,
    IReadOnlyDictionary<string, string> dedicatedBusinessConnections,
    JwtOptions jwtOptions,
    SecurityOptions securityOptions,
    bool useSqlite,
    string[] allowedCorsOrigins)
{
    if (app.Environment.IsDevelopment())
        return;

    if (useSqlite)
    {
        app.Logger.LogWarning("Production startup is using SQLite fallback. Linux PC 운영 시 PostgreSQL 고정을 권장합니다.");
    }

    if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) ||
        jwtOptions.SigningKey.Length < 32 ||
        jwtOptions.SigningKey.Contains("ChangeThis", StringComparison.OrdinalIgnoreCase))
    {
        app.Logger.LogWarning("JWT signing key is weak or still using a placeholder. 운영 전 반드시 긴 랜덤 값으로 변경하세요.");
    }

    if (ContainsInsecureConnectionStringSecret(connectionString))
    {
        app.Logger.LogWarning("Connection string still contains a sample or placeholder database password. Change it before production use.");
    }

    if (securityOptions.AllowAnyCorsOrigin)
    {
        app.Logger.LogWarning("AllowAnyCorsOrigin=true 입니다. 운영에서는 허용 Origin을 제한하는 것을 권장합니다.");
    }
    else if (allowedCorsOrigins.Length == 0)
    {
        app.Logger.LogInformation("No browser CORS origins are configured. 기본 네이티브 앱(PC/Android) 사용에는 영향이 없습니다.");
    }

    if (!securityOptions.RequireHttpsForwardedProto)
    {
        app.Logger.LogWarning("RequireHttpsForwardedProto=false 입니다. Linux PC reverse proxy 운영 시 true 를 권장합니다.");
    }

    foreach (var pair in dedicatedBusinessConnections)
    {
        app.Logger.LogInformation("Dedicated business DB routing enabled for tenant {TenantCode}.", pair.Key);
    }
}

static IReadOnlyDictionary<string, string> ResolveDedicatedBusinessConnections(IConfiguration configuration, string defaultConnectionString)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var connectionSection = configuration.GetSection("ConnectionStrings");

    foreach (var tenantCode in TenantScopeCatalog.AllTenants)
    {
        if (string.Equals(tenantCode, TenantScopeCatalog.UsenetGroup, StringComparison.OrdinalIgnoreCase))
            continue;

        var candidate = (connectionSection[tenantCode] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            continue;

        if (string.Equals(candidate, defaultConnectionString, StringComparison.OrdinalIgnoreCase))
            continue;

        result[tenantCode] = candidate;
    }

    return result;
}

static bool ContainsInsecureConnectionStringSecret(string connectionString)
    => connectionString.Contains("sm" + "_pass", StringComparison.OrdinalIgnoreCase) ||
       connectionString.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase) ||
       connectionString.Contains("__SET_SECURE_PASSWORD__", StringComparison.OrdinalIgnoreCase);
