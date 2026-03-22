namespace 거래플랜.Server.Api.Services;

public interface ICentralFileStorage
{
    string RootPath { get; }

    Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default);
    byte[] ReadBytes(string? storedPath, byte[]? fallback = null);
    void DeleteIfExists(string? storedPath);
}
