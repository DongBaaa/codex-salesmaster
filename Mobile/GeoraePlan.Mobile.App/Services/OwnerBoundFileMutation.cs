namespace GeoraePlan.Mobile.App.Services;

internal static class OwnerBoundFileMutation
{
    public static async Task PublishAsync(
        string temporaryPath,
        string targetPath,
        bool overwrite,
        Func<CancellationToken, Task<IDisposable>>
            acquireOwnerCommitLease,
        Action validateOwner,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(acquireOwnerCommitLease);
        ArgumentNullException.ThrowIfNull(validateOwner);

        using var ownerLease =
            await acquireOwnerCommitLease(ct);
        validateOwner();
        File.Move(
            temporaryPath,
            targetPath,
            overwrite);
        validateOwner();
    }

    public static async Task<bool> DeleteIfExistsAsync(
        string path,
        Func<CancellationToken, Task<IDisposable>>
            acquireOwnerCommitLease,
        Action validateOwner,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(acquireOwnerCommitLease);
        ArgumentNullException.ThrowIfNull(validateOwner);

        using var ownerLease =
            await acquireOwnerCommitLease(ct);
        validateOwner();
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        validateOwner();
        return true;
    }
}
