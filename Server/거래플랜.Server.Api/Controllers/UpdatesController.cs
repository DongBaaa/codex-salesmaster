using System.Text.Json;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("updates")]
public sealed class UpdatesController : ControllerBase
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly UpdateOptions _options;

    public UpdatesController(IOptions<UpdateOptions> options)
    {
        _options = options.Value ?? new UpdateOptions();
    }

    [HttpGet("manifest")]
    public async Task<ActionResult<AppUpdateManifestDto>> GetManifestAsync([FromQuery] string? channel = null, CancellationToken ct = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var manifestPath = Path.Combine(GetStorageRoot(), "manifest", normalizedChannel + ".json");
        if (!System.IO.File.Exists(manifestPath))
            return NotFound(new { message = $"update manifest not found: {normalizedChannel}" });

        await using var stream = System.IO.File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<AppUpdateManifestDto>(stream, ManifestJsonOptions, ct);
        if (manifest is null)
            return NotFound(new { message = $"update manifest is empty: {normalizedChannel}" });

        manifest.Channel = string.IsNullOrWhiteSpace(manifest.Channel) ? normalizedChannel : manifest.Channel.Trim();
        NormalizePackage(manifest.Desktop, "desktop");
        NormalizePackage(manifest.Android, "android");
        return Ok(manifest);
    }

    [HttpGet("download/{platform}/{fileName}")]
    public IActionResult DownloadPackage(string platform, string fileName)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform is null)
            return NotFound();

        var safeFileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName) || !string.Equals(safeFileName, fileName, StringComparison.Ordinal))
            return NotFound();

        var fullPath = Path.Combine(GetStorageRoot(), "downloads", normalizedPlatform, safeFileName);
        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var stream = System.IO.File.OpenRead(fullPath);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Update-FileName"] = Uri.EscapeDataString(safeFileName);
        return File(stream, ResolveContentType(safeFileName));
    }

    private void NormalizePackage(AppUpdatePackageDto? package, string platform)
    {
        if (package is null)
            return;

        package.Platform = string.IsNullOrWhiteSpace(package.Platform) ? platform : package.Platform.Trim();

        if (string.IsNullOrWhiteSpace(package.FileName) && !string.IsNullOrWhiteSpace(package.PackageUrl))
            package.FileName = Path.GetFileName(package.PackageUrl);

        if (Uri.TryCreate(package.PackageUrl, UriKind.Absolute, out _))
            return;

        if (!string.IsNullOrWhiteSpace(package.PackageUrl) && package.PackageUrl.StartsWith('/'))
        {
            package.PackageUrl = $"{Request.Scheme}://{Request.Host}{package.PackageUrl}";
            return;
        }

        if (string.IsNullOrWhiteSpace(package.FileName))
            return;

        var encodedFileName = Uri.EscapeDataString(package.FileName);
        package.PackageUrl = $"{Request.Scheme}://{Request.Host}/updates/download/{platform}/{encodedFileName}";
    }

    private string GetStorageRoot()
    {
        var configured = string.IsNullOrWhiteSpace(_options.StorageRoot) ? "updates" : _options.StorageRoot.Trim();
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = (channel ?? "stable").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "stable" => "stable",
            "test" => "test",
            "beta" => "beta",
            _ => "stable"
        };
    }

    private static string? NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "desktop" => "desktop",
            "android" => "android",
            _ => null
        };
    }

    private static string ResolveContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.ToLowerInvariant() switch
        {
            ".apk" => "application/vnd.android.package-archive",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}
