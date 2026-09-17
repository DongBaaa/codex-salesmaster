using System.IO;
using System.Security.Cryptography;

namespace 거래플랜.Desktop.App.Services;

// One enumeration only. A digest identifies bytes, not a persistent trust decision.
internal sealed class BackupVerificationReuseScope<TStatus>(
    Func<string, TStatus> verify,
    TStatus verifiedStatus)
{
    private readonly Dictionary<string, byte[]> _verifiedContent =
        new(StringComparer.OrdinalIgnoreCase);

    internal TStatus Verify(string path)
    {
        var fullPath = Path.GetFullPath(path);
        // Keep ordinary Windows writers/replacers out while hashing and validating.
        // A changed file in the next pass is fully validated again.
        using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        var digest = SHA256.HashData(stream);
        if (_verifiedContent.TryGetValue(fullPath, out var previous) &&
            CryptographicOperations.FixedTimeEquals(previous, digest))
        {
            return verifiedStatus;
        }

        _verifiedContent.Remove(fullPath);
        var status = verify(fullPath);
        if (EqualityComparer<TStatus>.Default.Equals(status, verifiedStatus))
            _verifiedContent[fullPath] = digest;
        return status;
    }
}
