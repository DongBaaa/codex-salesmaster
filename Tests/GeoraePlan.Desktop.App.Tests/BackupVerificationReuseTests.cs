using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackupVerificationReuseTests
{
    [Fact]
    public void UnchangedContentReusesOnlyWithinTheSameEnumeration()
    {
        using var fixture = new Fixture();
        var calls = 0;
        bool Validate(string path) { calls++; return File.ReadAllText(path) == "good"; }
        var scope = new BackupVerificationReuseScope<bool>(Validate, true);
        Assert.True(scope.Verify(fixture.FilePath));
        Assert.True(scope.Verify(fixture.FilePath));
        Assert.Equal(1, calls);
        Assert.True(new BackupVerificationReuseScope<bool>(Validate, true).Verify(fixture.FilePath));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SameLengthAndTimestampCannotHideChangedContent()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<bool>(path =>
        { calls++; return File.ReadAllText(path) == "good"; }, true);
        var timestamp = File.GetLastWriteTimeUtc(fixture.FilePath);
        Assert.True(scope.Verify(fixture.FilePath));
        File.WriteAllText(fixture.FilePath, "evil");
        File.SetLastWriteTimeUtc(fixture.FilePath, timestamp);
        Assert.False(scope.Verify(fixture.FilePath));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidOrIndeterminateResultsAreRetried()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<int>(_ => ++calls < 3 ? 2 : 1, 1);
        Assert.Equal(2, scope.Verify(fixture.FilePath));
        Assert.Equal(2, scope.Verify(fixture.FilePath));
        Assert.Equal(1, scope.Verify(fixture.FilePath));
        Assert.Equal(1, scope.Verify(fixture.FilePath));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void MissingFileDoesNotReuseOldSuccess()
    {
        using var fixture = new Fixture();
        var scope = new BackupVerificationReuseScope<bool>(_ => true, true);
        Assert.True(scope.Verify(fixture.FilePath));
        File.Delete(fixture.FilePath);
        Assert.Throws<FileNotFoundException>(() => scope.Verify(fixture.FilePath));
    }

    [Fact]
    public void ValidatorFailureDoesNotPublishSuccess()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<bool>(_ =>
        { if (++calls == 1) throw new InvalidDataException(); return true; }, true);
        Assert.Throws<InvalidDataException>(() => scope.Verify(fixture.FilePath));
        Assert.True(scope.Verify(fixture.FilePath));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void WindowsFileCannotBeWrittenDuringVerification()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var fixture = new Fixture();
        var scope = new BackupVerificationReuseScope<bool>(path =>
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "evil"));
            Assert.Equal("good", File.ReadAllText(path));
            return true;
        }, true);
        Assert.True(scope.Verify(fixture.FilePath));
    }

    [Fact]
    public void AtomicReplacementIsRevalidatedEvenWithPreservedMetadata()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<bool>(path =>
        { calls++; return File.ReadAllText(path) == "good"; }, true);
        Assert.True(scope.Verify(fixture.FilePath));
        var timestamp = File.GetLastWriteTimeUtc(fixture.FilePath);
        var replacement = fixture.FilePath + ".replacement";
        File.WriteAllText(replacement, "evil");
        File.SetLastWriteTimeUtc(replacement, timestamp);
        File.Move(replacement, fixture.FilePath, overwrite: true);
        Assert.False(scope.Verify(fixture.FilePath));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void OpenWriterPreventsReuseOfPreviousSuccess()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<bool>(path =>
        { calls++; return File.ReadAllText(path) == "good"; }, true);
        Assert.True(scope.Verify(fixture.FilePath));
        using (var writer = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Throws<IOException>(() => scope.Verify(fixture.FilePath));
        Assert.True(scope.Verify(fixture.FilePath));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DeleteAndReplacementAreBlockedDuringVerification()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var fixture = new Fixture();
        var replacement = fixture.FilePath + ".replacement";
        File.WriteAllText(replacement, "evil");
        var scope = new BackupVerificationReuseScope<bool>(path =>
        {
            Assert.Throws<IOException>(() => File.Delete(path));
            var replacementError = Record.Exception(() => File.Move(replacement, path, overwrite: true));
            Assert.True(replacementError is IOException or UnauthorizedAccessException);
            Assert.Equal("good", File.ReadAllText(path));
            return true;
        }, true);
        Assert.True(scope.Verify(fixture.FilePath));
    }

    [Fact]
    public void ChangedInvalidContentDiscardsOldSuccessBeforeReturning()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var scope = new BackupVerificationReuseScope<bool>(path =>
        { calls++; return File.ReadAllText(path) == "good"; }, true);
        Assert.True(scope.Verify(fixture.FilePath));
        File.WriteAllText(fixture.FilePath, "evil");
        Assert.False(scope.Verify(fixture.FilePath));
        File.WriteAllText(fixture.FilePath, "good");
        Assert.True(scope.Verify(fixture.FilePath));
        Assert.Equal(3, calls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "trade-verification-reuse-" + Guid.NewGuid().ToString("N"));
        internal string FilePath { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, "sample.gpbackup");
            File.WriteAllText(FilePath, "good");
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
