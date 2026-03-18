using System.Text;
using System.Text.Json.Serialization;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? string.Empty;
var sqliteFallbackEnabled = builder.Configuration.GetValue("Database:EnableSqliteFallback", true);
var forceSqlite = string.Equals(Environment.GetEnvironmentVariable("ERP_DB_FALLBACK_SQLITE"), "1", StringComparison.Ordinal);
var forcePostgres = string.Equals(Environment.GetEnvironmentVariable("ERP_FORCE_POSTGRES"), "1", StringComparison.Ordinal);
var sqliteDbPath = ResolveSqliteFallbackPath();

var useSqlite = forceSqlite ||
                (!forcePostgres &&
                 (string.IsNullOrWhiteSpace(connectionString) ||
                  (sqliteFallbackEnabled && !TryCanConnectPostgres(connectionString))));

if (useSqlite)
{
    Console.WriteLine($"[거래플랜 API] PostgreSQL 연결 불가. SQLite 폴백 사용: {sqliteDbPath}");
}
else
{
    Console.WriteLine("[거래플랜 API] PostgreSQL 연결 성공");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useSqlite)
    {
        options.UseSqlite($"Data Source={sqliteDbPath}");
    }
    else
    {
        options.UseNpgsql(connectionString);
    }
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddScoped<IJwtTokenFactory, JwtTokenFactory>();
builder.Services.AddScoped<IInvoiceNumberService, InvoiceNumberService>();
builder.Services.AddSingleton<RevisionClock>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
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
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    AddPermissionPolicy(options, PermissionNames.CompanyProfileEdit);
    AddPermissionPolicy(options, PermissionNames.AmountViewSales);
    AddPermissionPolicy(options, PermissionNames.AmountViewPurchase);
    AddPermissionPolicy(options, PermissionNames.SettingsEdit);
    AddPermissionPolicy(options, PermissionNames.DataBackupRestore);
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("DesktopClient", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "거래플랜 API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header. 예: 'Bearer {token}'",
        Name = "Authorization", In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey, Scheme = "Bearer"
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("DesktopClient");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await DbInitializer.InitializeAsync(app.Services);

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
            context.User.Claims.Any(claim => claim.Type == "perm" && claim.Value == permission));
    });
}
