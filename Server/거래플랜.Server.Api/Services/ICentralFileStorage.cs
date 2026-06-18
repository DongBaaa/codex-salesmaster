namespace 거래플랜.Server.Api.Services;

public interface ICentralFileStorage
{
    string RootPath { get; }

    Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default);
    byte[] ReadBytes(string? storedPath, byte[]? fallback = null);
    FileStorageInspectionResult Inspect(string? storedPath, bool computeHash = false)
        => new(
            HasStoredPath: !string.IsNullOrWhiteSpace(storedPath),
            IsSafePath: false,
            Exists: false,
            Length: null,
            Hash: string.Empty,
            Error: string.IsNullOrWhiteSpace(storedPath) ? string.Empty : "file_storage_inspection_not_implemented");
    void DeleteIfExists(string? storedPath);
}

public sealed record FileStorageInspectionResult(
    bool HasStoredPath,
    bool IsSafePath,
    bool Exists,
    long? Length,
    string Hash,
    string Error);
