using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsBackupLoadingTests
{
    [Fact]
    public async Task CoreBecomesUsableBeforeBackupVerification_AndCompletionPreservesLaterWrite()
    {
        var vm = Create();
        var core = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backups = new TaskCompletionSource<IReadOnlyList<BackupSnapshotInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BackupSnapshotReader = () => { backupStarted.SetResult(); return backups.Task; };
        var initialization = vm.InitializeCoreThenBackupListAsync(async () => { await core.Task; vm.StatusMessage = "core ready"; });
        Assert.True(vm.IsInitialLoadInProgress);
        Assert.False(vm.CanInteract);
        Assert.True(vm.CanNavigateTabs);
        Assert.False(vm.IsCloseBlocked);
        Assert.False(backupStarted.Task.IsCompleted);
        core.SetResult();
        await backupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(initialization.IsCompleted);
        Assert.True(vm.CanInteract);
        Assert.False(vm.IsInitialLoadInProgress);
        Assert.False(vm.IsCloseBlocked);
        Assert.True(vm.IsBackupListLoading);
        Assert.False(vm.CreateBackupSnapshotCommand.CanExecute(null));
        Assert.False(vm.ScheduleSelectedBackupRestoreCommand.CanExecute(null));
        Assert.False(vm.RunBackupCommand.CanExecute(null));
        Assert.False(vm.ReloadBackupSnapshotsCommand.CanExecute(null));
        await vm.CreateBackupSnapshotCommand.ExecuteAsync(null);
        await vm.ScheduleSelectedBackupRestoreCommand.ExecuteAsync(null);
        await vm.RunBackupCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsBackupListLoading);
        vm.IsBusy = true;
        vm.StatusMessage = "another save in progress";
        backups.SetResult([Snapshot("verified")]);
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsCloseBlocked);
        Assert.Equal("another save in progress", vm.StatusMessage);
        Assert.False(vm.IsBackupListLoading);
        Assert.Single(vm.BackupSnapshots);
        Assert.False(vm.RunBackupCommand.CanExecute(null));
        vm.IsBusy = false;
        Assert.True(vm.RunBackupCommand.CanExecute(null));
        Assert.True(vm.ScheduleSelectedBackupRestoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedInitialVerificationClearsOldSelection_AndCanRetryWithoutReplacingCoreStatus()
    {
        var vm = Create();
        vm.BackupSnapshots.Add(new BackupSnapshotRow { FilePath = "old" });
        vm.SelectedBackupSnapshot = vm.BackupSnapshots[0];
        vm.BackupSnapshotReader = () => Task.FromException<IReadOnlyList<BackupSnapshotInfo>>(new IOException("test read failure"));
        await vm.InitializeCoreThenBackupListAsync(() => { vm.StatusMessage = "core ready"; return Task.CompletedTask; });
        Assert.Equal("core ready", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBackupListLoading);
        Assert.Empty(vm.BackupSnapshots);
        Assert.Null(vm.SelectedBackupSnapshot);
        Assert.Contains("불러오지 못했습니다", vm.BackupDataStatus);
        Assert.True(vm.ReloadBackupSnapshotsCommand.CanExecute(null));
        vm.BackupSnapshotReader = () => Task.FromResult<IReadOnlyList<BackupSnapshotInfo>>([Snapshot("new")]);
        await vm.ReloadBackupSnapshotsCommand.ExecuteAsync(null);
        Assert.Equal("new", Assert.Single(vm.BackupSnapshots).FileName);
        Assert.Same(vm.BackupSnapshots[0], vm.SelectedBackupSnapshot);
        Assert.Equal("core ready", vm.StatusMessage);
    }

    [Fact]
    public async Task ConcurrentReloadCallsShareOneVerification_AndDisableAlreadyCreatedCommands()
    {
        var vm = Create();
        var pending = new TaskCompletionSource<IReadOnlyList<BackupSnapshotInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        vm.BackupSnapshotReader = () => { calls++; return pending.Task; };
        var command = vm.RunBackupCommand;
        var notifications = 0;
        command.CanExecuteChanged += (_, _) => notifications++;
        Assert.True(command.CanExecute(null));
        vm.BackupSnapshots.Add(new BackupSnapshotRow { FileName = "previous" });
        vm.SelectedBackupSnapshot = vm.BackupSnapshots[0];
        var first = vm.ReloadBackupSnapshotsAsync();
        Assert.Equal("previous", Assert.Single(vm.BackupSnapshots).FileName);
        Assert.Null(vm.SelectedBackupSnapshot);
        var second = vm.ReloadBackupSnapshotsAsync();
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.False(command.CanExecute(null));
        pending.SetResult([]);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(command.CanExecute(null));
        Assert.True(notifications >= 2);
    }

    [Fact]
    public async Task BackgroundVerificationDoesNotGrantBackupPermission()
    {
        var vm = Create(admin: false);
        vm.BackupSnapshotReader = () => Task.FromResult<IReadOnlyList<BackupSnapshotInfo>>([Snapshot("verified")]);
        await vm.InitializeCoreThenBackupListAsync(() => Task.CompletedTask);
        Assert.True(vm.CanInteract);
        Assert.False(vm.CanManageBackupData);
        Assert.False(vm.CreateBackupSnapshotCommand.CanExecute(null));
        Assert.False(vm.ScheduleSelectedBackupRestoreCommand.CanExecute(null));
        Assert.False(vm.RunBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task CoreFailureDoesNotStartBackupWork_AndReleasesInitializationState()
    {
        var vm = Create();
        var calls = 0;
        vm.BackupSnapshotReader = () => { calls++; return Task.FromResult<IReadOnlyList<BackupSnapshotInfo>>([]); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.InitializeCoreThenBackupListAsync(() => throw new InvalidOperationException("core failure")));
        Assert.Equal(0, calls);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsInitialLoadInProgress);
    }

    private static BackupSnapshotInfo Snapshot(string name) => new(name, name, DateTime.Now, 10, name, "10 B");

    private static EnvironmentSettingsViewModel Create(bool admin = true)
    {
        var session = new SessionState();
        session.SetSession("isolated-test", new UserSessionDto { Username = "test", Role = admin ? "Admin" : "User", OfficeCode = "USENET" });
        return new(null!, session, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }
}
