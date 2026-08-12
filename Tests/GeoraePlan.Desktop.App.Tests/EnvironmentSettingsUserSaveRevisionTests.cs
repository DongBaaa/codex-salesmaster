using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsUserSaveRevisionTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task UpdateAndPassword_UsesAuthoritativeUpdatedRevisionBeforeDownstreamState()
    {
        var request = CreateUpdateRequest(expectedRevision: 41);
        request.Username = "  server-normalized-user  ";
        var updated = CreateUpdatedUser(revision: 42, username: "server-normalized-user");
        var calls = new List<string>();
        UserAccountDto? downstreamUser = null;

        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            request,
            "new-password",
            (id, actualRequest) =>
            {
                Assert.Equal(UserId, id);
                Assert.Same(request, actualRequest);
                calls.Add("update");
                return Task.FromResult<UserAccountDto?>(updated);
            },
            (id, passwordRequest) =>
            {
                Assert.Equal(UserId, id);
                Assert.Equal(42, passwordRequest.ExpectedRevision);
                Assert.Equal("new-password", passwordRequest.Password);
                calls.Add("password");
                return Task.CompletedTask;
            },
            authoritativeUser =>
            {
                downstreamUser = authoritativeUser;
                calls.Add("profile");
                return Task.CompletedTask;
            },
            authoritativeUser =>
            {
                Assert.Same(updated, authoritativeUser);
                calls.Add("reload");
                return Task.CompletedTask;
            });

        Assert.Same(updated, result.UpdatedUser);
        Assert.Equal(EnvironmentSettingsViewModel.UserPasswordSaveState.Succeeded, result.PasswordState);
        Assert.Same(updated, downstreamUser);
        Assert.Equal(["update", "password", "profile", "reload"], calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateWithoutPassword_SkipsPasswordAndAppliesAuthoritativeState(string? password)
    {
        var request = CreateUpdateRequest(expectedRevision: 7);
        request.Username = "authoritative-user";
        var updated = CreateUpdatedUser(revision: 8, username: "authoritative-user");
        var passwordCalls = 0;
        UserAccountDto? downstreamUser = null;

        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            request,
            password,
            (_, _) => Task.FromResult<UserAccountDto?>(updated),
            (_, _) =>
            {
                passwordCalls++;
                return Task.CompletedTask;
            },
            authoritativeUser =>
            {
                downstreamUser = authoritativeUser;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask);

        Assert.Equal(0, passwordCalls);
        Assert.Same(updated, downstreamUser);
        Assert.Same(updated, result.UpdatedUser);
        Assert.Equal(EnvironmentSettingsViewModel.UserPasswordSaveState.NotRequested, result.PasswordState);
    }

    [Fact]
    public async Task FirstStageFailure_PreventsPasswordAndDownstreamState()
    {
        var passwordCalls = 0;
        var downstreamCalls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
                UserId,
                CreateUpdateRequest(expectedRevision: 12),
                "new-password",
                (_, _) => throw new InvalidOperationException("update failed"),
                (_, _) =>
                {
                    passwordCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    downstreamCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    downstreamCalls++;
                    return Task.CompletedTask;
                }));

        Assert.Equal("update failed", exception.Message);
        Assert.Equal(0, passwordCalls);
        Assert.Equal(0, downstreamCalls);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tenant")]
    [InlineData("office")]
    [InlineData("scope")]
    [InlineData("revision")]
    [InlineData("username")]
    [InlineData("role")]
    [InlineData("active")]
    [InlineData("permissions")]
    public async Task InvalidAuthoritativeResponse_PreventsPasswordAndDownstreamState(string mismatch)
    {
        var request = CreateUpdateRequest(expectedRevision: 20);
        var updated = CreateUpdatedUser(revision: 21);
        switch (mismatch)
        {
            case "id":
                updated.Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
                break;
            case "tenant":
                updated.TenantCode = TenantScopeCatalog.Itworld;
                break;
            case "office":
                updated.OfficeCode = OfficeCodeCatalog.Itworld;
                break;
            case "scope":
                updated.ScopeType = TenantScopeCatalog.ScopeTenantAll;
                break;
            case "revision":
                updated.Revision = request.ExpectedRevision;
                break;
            case "username":
                updated.Username = "other-user";
                break;
            case "role":
                updated.Role = "Admin";
                break;
            case "active":
                updated.IsActive = false;
                break;
            case "permissions":
                updated.Permissions = [AppPermissionNames.CustomerEdit];
                break;
        }

        var passwordCalls = 0;
        var downstreamCalls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
                UserId,
                request,
                "new-password",
                (_, _) => Task.FromResult<UserAccountDto?>(updated),
                (_, _) =>
                {
                    passwordCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    downstreamCalls++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    downstreamCalls++;
                    return Task.CompletedTask;
                }));

        Assert.Equal(0, passwordCalls);
        Assert.Equal(0, downstreamCalls);
    }

    [Fact]
    public async Task DefinitivePasswordFailure_AppliesProfileAndReloadWithoutRetry()
    {
        var passwordCalls = 0;
        var downstreamCalls = 0;

        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            CreateUpdateRequest(expectedRevision: 30),
            "new-password",
            (_, _) => Task.FromResult<UserAccountDto?>(CreateUpdatedUser(revision: 31)),
            (_, _) =>
            {
                passwordCalls++;
                throw new HttpRequestException("rejected", null, HttpStatusCode.BadRequest);
            },
            _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            },
            _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, passwordCalls);
        Assert.Equal(2, downstreamCalls);
        Assert.Equal(EnvironmentSettingsViewModel.UserPasswordSaveState.DefinitiveFailure, result.PasswordState);
        Assert.False(result.RequiresAuthoritativeReload);
    }

    [Theory]
    [MemberData(nameof(AmbiguousPasswordFailures))]
    public async Task AmbiguousPasswordFailure_IsNotRetriedAndStillRunsDownstream(Exception failure)
    {
        var passwordCalls = 0;
        var downstreamCalls = 0;

        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            CreateUpdateRequest(expectedRevision: 50),
            "new-password",
            (_, _) => Task.FromResult<UserAccountDto?>(CreateUpdatedUser(revision: 51)),
            (_, _) =>
            {
                passwordCalls++;
                throw failure;
            },
            _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            },
            _ =>
            {
                downstreamCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, passwordCalls);
        Assert.Equal(2, downstreamCalls);
        Assert.Equal(EnvironmentSettingsViewModel.UserPasswordSaveState.Ambiguous, result.PasswordState);
    }

    [Fact]
    public async Task SuccessfulPasswordWithReloadFailure_RequiresAuthoritativeReload()
    {
        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            CreateUpdateRequest(expectedRevision: 60),
            "new-password",
            (_, _) => Task.FromResult<UserAccountDto?>(CreateUpdatedUser(revision: 61)),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => throw new InvalidDataException("saved user missing"));

        Assert.Equal(EnvironmentSettingsViewModel.UserPasswordSaveState.Succeeded, result.PasswordState);
        Assert.IsType<InvalidDataException>(result.ReloadFailure);
        Assert.True(result.RequiresAuthoritativeReload);
    }

    [Fact]
    public async Task CompanyProfileFailure_DoesNotPreventAuthoritativeReload()
    {
        var reloadCalls = 0;
        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            CreateUpdateRequest(expectedRevision: 65),
            null,
            (_, _) => Task.FromResult<UserAccountDto?>(CreateUpdatedUser(revision: 66)),
            (_, _) => Task.CompletedTask,
            _ => throw new InvalidOperationException("profile failed"),
            _ =>
            {
                reloadCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, reloadCalls);
        Assert.IsType<InvalidOperationException>(result.CompanyProfileFailure);
        Assert.Null(result.ReloadFailure);
        Assert.False(result.RequiresAuthoritativeReload);
    }

    [Fact]
    public async Task ResponseValidation_UsesServerCanonicalTenantAndPermissionSet()
    {
        var request = CreateUpdateRequest(expectedRevision: 70);
        request.Username = "  edited-user  ";
        request.Role = "user";
        request.TenantCode = TenantScopeCatalog.Itworld;
        request.OfficeCode = OfficeCodeCatalog.Usenet;
        request.Permissions = [" Settings.Edit ", "settings.edit"];
        var updated = CreateUpdatedUser(revision: 71);
        updated.Permissions = ["settings.edit"];

        var result = await EnvironmentSettingsViewModel.RunExistingUserSaveAsync(
            UserId,
            request,
            null,
            (_, _) => Task.FromResult<UserAccountDto?>(updated),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        Assert.Same(updated, result.UpdatedUser);
    }

    public static IEnumerable<object[]> AmbiguousPasswordFailures()
    {
        yield return [new HttpRequestException("response lost")];
        yield return [new TimeoutException("timed out")];
        yield return [new TaskCanceledException("timed out")];
    }

    [Fact]
    public async Task SaveUserCommand_DefinitivePasswordFailure_ReloadsAndPreservesExplicitPartialSuccess()
    {
        var handler = new UserSaveHandler
        {
            PasswordStatusCode = HttpStatusCode.BadRequest
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 100);
        handler.ReloadUsersFactory = updated =>
        [
            CloneUser(updated, revision: updated.Revision)
        ];
        fixture.ViewModel.EditingUsername = "  renamed-user  ";
        fixture.ViewModel.EditingPassword = "rejected-password";
        fixture.ViewModel.EditingPasswordConfirm = "rejected-password";

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.StartsWith(
            "사용자 정보 저장 완료, 비밀번호 변경 실패",
            fixture.ViewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("사용자 저장 실패", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, handler.PasswordCalls);
        Assert.Equal(initial.Id, fixture.ViewModel.SelectedUser?.Id);
        Assert.True(fixture.ViewModel.SelectedUser!.Revision > initial.Revision);
        Assert.Equal("renamed-user", fixture.ViewModel.SelectedUser.Username);
        Assert.Equal("rejected-password", fixture.ViewModel.EditingPassword);
        Assert.Equal(fixture.ProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("renamed-user"));
    }

    [Fact]
    public async Task SaveUserCommand_AmbiguousPasswordFailure_ReloadsAuthoritativeUserWithoutVmRetry()
    {
        var handler = new UserSaveHandler
        {
            PasswordFailure = new HttpRequestException("response lost")
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 200);
        handler.ReloadUsersFactory = updated =>
        [
            CloneUser(updated, revision: updated.Revision + 1)
        ];
        fixture.ViewModel.EditingPassword = "uncertain-password";
        fixture.ViewModel.EditingPasswordConfirm = "uncertain-password";

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.StartsWith(
            "사용자 정보 저장 완료, 비밀번호 상태 미확정(자동 재시도 안 함)",
            fixture.ViewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.Equal(1, handler.PasswordCalls);
        Assert.Equal(initial.Id, fixture.ViewModel.SelectedUser?.Id);
        Assert.True(fixture.ViewModel.SelectedUser!.Revision > initial.Revision);
        Assert.Equal("uncertain-password", fixture.ViewModel.EditingPassword);
        Assert.Equal(fixture.ProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("edited-user"));
    }

    [Fact]
    public async Task SaveUserCommand_MissingReloadTarget_PreservesEditorAndBlocksStaleSecondSave()
    {
        var handler = new UserSaveHandler();
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 300);
        handler.ReloadUsersFactory = _ => [];
        fixture.ViewModel.EditingUsername = "  edited-user  ";
        fixture.ViewModel.EditingPassword = "accepted-password";
        fixture.ViewModel.EditingPasswordConfirm = "accepted-password";

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Contains("사용자 목록 재조회 실패", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("다시 저장하기 전에", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(initial.Id, fixture.ViewModel.EditingUserId);
        Assert.Equal("edited-user", fixture.ViewModel.EditingUsername);
        Assert.Equal(string.Empty, fixture.ViewModel.EditingPassword);
        Assert.Equal(fixture.ProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("edited-user"));
        var updateCallsAfterFirstSave = handler.UpdateCalls;

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(updateCallsAfterFirstSave, handler.UpdateCalls);
        Assert.Contains("이전 사용자 변경 결과를 확정하지 못했습니다", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveUserCommand_AmbiguousCreate_ServerCommitted_RemainsUnconfirmedWithoutProfileWrite()
    {
        var handler = new UserSaveHandler
        {
            CreateFailure = new HttpRequestException("create response lost"),
            CommitCreateBeforeFailure = true
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        PrepareNewUser(fixture.ViewModel, "new-user", "new-password");

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.CreateCalls);
        Assert.Contains(fixture.ViewModel.Users, user => user.Username == "new-user");
        Assert.Null(fixture.ViewModel.SelectedUser);
        Assert.Equal(Guid.Empty, fixture.ViewModel.EditingUserId);
        Assert.Equal("new-password", fixture.ViewModel.EditingPassword);
        Assert.Null(await fixture.Local.GetAssignedCompanyProfileIdAsync("new-user"));
        Assert.Contains("요청 귀속 미확정", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("서버 상태 일치 여부와 무관", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("회사설정은 변경하지 않았고", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.CreateCalls);
    }

    [Fact]
    public async Task SaveUserCommand_ConfirmedCreate_ProfileFailureKeepsServerSuccessAndExistingMapping()
    {
        var handler = new UserSaveHandler();
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var existingProfileId = await fixture.AddCompanyProfileAsync("existing profile");
        await fixture.Local.SetAssignedCompanyProfileAsync("new-user", existingProfileId);
        fixture.ViewModel.AssignedUserCompanyProfileWriter = (_, _) =>
            throw new InvalidOperationException("profile write failed");
        PrepareNewUser(fixture.ViewModel, "new-user", "new-password");

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.CreateCalls);
        Assert.NotEqual(Guid.Empty, fixture.ViewModel.EditingUserId);
        Assert.Equal("new-user", fixture.ViewModel.SelectedUser?.Username);
        Assert.Equal(string.Empty, fixture.ViewModel.EditingPassword);
        Assert.Equal(existingProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("new-user"));
        Assert.Contains("사용자 생성 완료", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("회사설정 적용 실패", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("생성을 반복하지 마세요", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveUserCommand_ConfirmedCreate_ReloadFailureKeepsServerSuccessAndPreventsDuplicateCreateState()
    {
        var handler = new UserSaveHandler
        {
            FailUsersGetFromCall = 1
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        PrepareNewUser(fixture.ViewModel, "new-user", "new-password");

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.CreateCalls);
        Assert.NotEqual(Guid.Empty, fixture.ViewModel.EditingUserId);
        Assert.Equal("new-user", fixture.ViewModel.SelectedUser?.Username);
        Assert.Contains(fixture.ViewModel.Users, user => user.Id == fixture.ViewModel.EditingUserId);
        Assert.Equal(string.Empty, fixture.ViewModel.EditingPassword);
        Assert.Equal(fixture.ProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("new-user"));
        Assert.Contains("사용자 생성 완료", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("사용자 목록 새로고침 실패", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("생성을 반복하지 마세요", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveUserCommand_AmbiguousCreate_OtherAdminConcurrentExactMatch_RemainsUnconfirmed()
    {
        var handler = new UserSaveHandler
        {
            CreateFailure = new HttpRequestException("create response lost"),
            OtherAdminCreatesExactMatchBeforeFailure = true
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        PrepareNewUser(fixture.ViewModel, "existing-user", "new-password");

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.CreateCalls);
        Assert.Null(await fixture.Local.GetAssignedCompanyProfileIdAsync("existing-user"));
        Assert.Null(fixture.ViewModel.SelectedUser);
        Assert.Equal("new-password", fixture.ViewModel.EditingPassword);
        Assert.Contains(fixture.ViewModel.Users, user => user.Username == "existing-user");
        Assert.Contains("요청 귀속 미확정", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("서버 상태 일치 여부와 무관", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.CreateCalls);
    }

    [Fact]
    public async Task SaveUserCommand_AmbiguousUpdate_ConfirmsFieldsWithoutRunningPassword()
    {
        var handler = new UserSaveHandler
        {
            UpdateFailure = new HttpRequestException("update response lost"),
            CommitUpdateBeforeFailure = true
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 400);
        fixture.ViewModel.EditingUsername = "confirmed-user";
        fixture.ViewModel.EditingPassword = "must-not-run";
        fixture.ViewModel.EditingPasswordConfirm = "must-not-run";

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.UpdateCalls);
        Assert.Equal(0, handler.PasswordCalls);
        Assert.Equal(initial.Id, fixture.ViewModel.SelectedUser?.Id);
        Assert.Equal("confirmed-user", fixture.ViewModel.SelectedUser?.Username);
        Assert.True(fixture.ViewModel.SelectedUser!.Revision > initial.Revision);
        Assert.Equal("must-not-run", fixture.ViewModel.EditingPassword);
        Assert.Equal(fixture.ProfileId, await fixture.Local.GetAssignedCompanyProfileIdAsync("confirmed-user"));
        Assert.Contains("비밀번호 변경은 실행하지 않았습니다", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveUserCommand_AmbiguousUpdate_UnconfirmedPreservesEditAndBlocksSecondSave()
    {
        var handler = new UserSaveHandler
        {
            UpdateFailure = new HttpRequestException("update response lost"),
            CommitUpdateBeforeFailure = false
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        await fixture.SelectExistingUserAsync(revision: 500);
        fixture.ViewModel.EditingUsername = "unconfirmed-user";
        fixture.ViewModel.EditingPassword = "must-not-run";
        fixture.ViewModel.EditingPasswordConfirm = "must-not-run";

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.UpdateCalls);
        Assert.Equal(0, handler.PasswordCalls);
        Assert.Equal("unconfirmed-user", fixture.ViewModel.EditingUsername);
        Assert.Equal("must-not-run", fixture.ViewModel.EditingPassword);
        Assert.Null(await fixture.Local.GetAssignedCompanyProfileIdAsync("unconfirmed-user"));
        Assert.Contains("상태 미확정", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);

        await fixture.ViewModel.SaveUserCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.UpdateCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task DeleteUserCommand_AmbiguousOutcome_NeverConfirmsOrRedispatches(
        bool deleteCommitted,
        bool scopeMovedOutOfView)
    {
        var handler = new UserSaveHandler
        {
            DeleteFailure = new HttpRequestException("delete response lost"),
            CommitDeleteBeforeFailure = deleteCommitted,
            HideDeleteTargetBeforeFailure = scopeMovedOutOfView
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 600);

        await fixture.ViewModel.DeleteUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.DeleteCalls);
        Assert.DoesNotContain("삭제 결과를 서버 재조회로 확인", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("사용자 삭제 완료", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("삭제 요청 결과 미확정", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("반복 금지", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        if (deleteCommitted || scopeMovedOutOfView)
        {
            Assert.DoesNotContain(fixture.ViewModel.Users, user => user.Id == initial.Id);
            Assert.Null(fixture.ViewModel.SelectedUser);
            Assert.Contains("삭제와 관리 범위 변경을 구분할 수 없습니다", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(initial.Id, fixture.ViewModel.SelectedUser?.Id);
            Assert.Contains("자동 재삭제하지 않았습니다", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        }

        await fixture.ViewModel.DeleteUserCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.DeleteCalls);
    }

    [Fact]
    public async Task DeleteUserCommand_ConfirmedDelete_ReloadFailureKeepsDeletedLocalStateAndPreventsRepeat()
    {
        var handler = new UserSaveHandler
        {
            FailUsersGetFromCall = 1
        };
        await using var fixture = await ViewModelFixture.CreateAsync(handler);
        var initial = await fixture.SelectExistingUserAsync(revision: 700);

        await fixture.ViewModel.DeleteUserCommand.ExecuteAsync(null);

        Assert.Equal(1, handler.DeleteCalls);
        Assert.Null(fixture.ViewModel.SelectedUser);
        Assert.Equal(Guid.Empty, fixture.ViewModel.EditingUserId);
        Assert.DoesNotContain(fixture.ViewModel.Users, user => user.Id == initial.Id);
        Assert.Contains("사용자 삭제 완료", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("사용자 목록 새로고침 실패", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("삭제를 반복하지 마세요", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);

        await fixture.ViewModel.DeleteUserCommand.ExecuteAsync(null);
        Assert.Equal(1, handler.DeleteCalls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task UserMutation_HttpRequestFailure_IsAmbiguousAndDispatchedOnce(string mutation)
    {
        var handler = new SingleDispatchHandler(SingleDispatchFailure.HttpRequest);
        var api = CreateApiClient(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() => InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.SendCalls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task UserMutation_ServerError_IsAmbiguousAndDispatchedOnce(string mutation)
    {
        var handler = new SingleDispatchHandler(SingleDispatchFailure.ServerError);
        var api = CreateApiClient(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() => InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.SendCalls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task UserMutation_SuccessBodyReadFailure_IsAmbiguousAndDispatchedOnce(string mutation)
    {
        var handler = new SingleDispatchHandler(SingleDispatchFailure.MalformedSuccessBody);
        var api = CreateApiClient(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() => InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.SendCalls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task UserMutation_SuccessCanonicalMismatch_IsAmbiguousAndNeverRedispatched(string mutation)
    {
        var handler = new SingleDispatchHandler(SingleDispatchFailure.CanonicalMismatch);
        var api = CreateApiClient(handler);

        await Assert.ThrowsAsync<AmbiguousMutationOutcomeException>(() => InvokeMutationAsync(api, mutation));

        Assert.Equal(1, handler.SendCalls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("password")]
    [InlineData("delete")]
    public async Task UserMutation_FirstConflict_IsDefinitiveAndDispatchedOnce(string mutation)
    {
        var handler = new SingleDispatchHandler(SingleDispatchFailure.Conflict);
        var api = CreateApiClient(handler);

        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => InvokeMutationAsync(api, mutation));

        Assert.IsNotType<AmbiguousMutationOutcomeException>(failure);
        Assert.Equal(1, handler.SendCalls);
    }

    private static ErpApiClient CreateApiClient(HttpMessageHandler handler)
    {
        var session = new SessionState();
        session.SetSession(
            "admin-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeTenantAll
            },
            DateTime.UtcNow.AddHours(1));
        return new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
    }

    private static async Task InvokeMutationAsync(ErpApiClient api, string mutation)
    {
        switch (mutation)
        {
            case "create":
                await api.CreateUserAsync(new CreateUserRequest
                {
                    Username = "new-user",
                    Password = "password",
                    Role = "User",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                    IsActive = true,
                    Permissions = [AppPermissionNames.SettingsEdit]
                });
                break;
            case "update":
                await api.UpdateUserAsync(UserId, CreateUpdateRequest(expectedRevision: 10));
                break;
            case "password":
                await api.UpdateUserPasswordAsync(UserId, new UpdateUserPasswordRequest
                {
                    ExpectedRevision = 11,
                    Password = "password"
                });
                break;
            case "delete":
                await api.DeleteUserAsync(UserId, expectedRevision: 11);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static void PrepareNewUser(
        EnvironmentSettingsViewModel viewModel,
        string username,
        string password)
    {
        viewModel.NewUserCommand.Execute(null);
        viewModel.EditingUsername = username;
        viewModel.EditingPassword = password;
        viewModel.EditingPasswordConfirm = password;
        viewModel.EditingUserRole = "User";
        viewModel.EditingUserTenantCode = TenantScopeCatalog.UsenetGroup;
        viewModel.EditingUserOfficeCode = OfficeCodeCatalog.Usenet;
        viewModel.EditingUserScopeType = TenantScopeCatalog.ScopeOfficeOnly;
        viewModel.EditingUserIsActive = true;
    }

    private static UpdateUserRequest CreateUpdateRequest(long expectedRevision)
        => new()
        {
            ExpectedRevision = expectedRevision,
            Username = "edited-user",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            Permissions = [AppPermissionNames.SettingsEdit]
        };

    private static UserAccountDto CreateUpdatedUser(long revision, string username = "edited-user")
        => new()
        {
            Id = UserId,
            Revision = revision,
            Username = username,
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            Permissions = [AppPermissionNames.SettingsEdit]
        };

    private static UserAccountDto CloneUser(UserAccountDto source, long revision)
        => new()
        {
            Id = source.Id,
            Revision = revision,
            Username = source.Username,
            Role = source.Role,
            TenantCode = source.TenantCode,
            OfficeCode = source.OfficeCode,
            ScopeType = source.ScopeType,
            IsActive = source.IsActive,
            Permissions = [.. source.Permissions]
        };

    private sealed class ViewModelFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly LocalDbContext _db;
        private readonly SyncService _sync;
        private readonly HttpClient _httpClient;

        private ViewModelFixture(
            SqliteConnection connection,
            LocalDbContext db,
            SyncService sync,
            HttpClient httpClient,
            LocalStateService local,
            EnvironmentSettingsViewModel viewModel,
            Guid profileId,
            UserSaveHandler handler)
        {
            _connection = connection;
            _db = db;
            _sync = sync;
            _httpClient = httpClient;
            Local = local;
            ViewModel = viewModel;
            ProfileId = profileId;
            Handler = handler;
        }

        public LocalStateService Local { get; }
        public EnvironmentSettingsViewModel ViewModel { get; }
        public Guid ProfileId { get; }
        public UserSaveHandler Handler { get; }

        public static async Task<ViewModelFixture> CreateAsync(UserSaveHandler handler)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            session.SetSession(
                "admin-token",
                new UserSessionDto
                {
                    UserId = Guid.NewGuid(),
                    Username = "admin",
                    Role = "Admin",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeTenantAll
                },
                DateTime.UtcNow.AddHours(1));
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var api = new ErpApiClient(httpClient, session);
            var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
            var viewModel = new EnvironmentSettingsViewModel(
                local,
                session,
                api,
                sync,
                new BackupService(),
                diagnostics,
                new DataIntegrityIssueService(db, dispatcher, local, erpApiClient: api, syncService: sync),
                rental,
                new StatementPrintService(),
                new RentalDocumentService(),
                null!);
            var profileId = Guid.NewGuid();
            var profile = new LocalCompanyProfile
            {
                Id = profileId,
                ProfileName = "USENET profile",
                OfficeCode = OfficeCodeCatalog.Usenet,
                IsDefaultForOffice = true
            };
            db.CompanyProfiles.Add(profile);
            await db.SaveChangesAsync();
            viewModel.CompanyProfiles.Add(profile);

            return new ViewModelFixture(connection, db, sync, httpClient, local, viewModel, profileId, handler);
        }

        public async Task<UserAccountDto> SelectExistingUserAsync(long revision)
        {
            var user = CreateUpdatedUser(revision);
            Handler.SeedUser(user);
            await Local.SetAssignedCompanyProfileAsync(user.Username, ProfileId);
            ViewModel.Users.Add(user);
            ViewModel.SelectedUser = user;
            for (var attempt = 0;
                 attempt < 50 &&
                 !string.Equals(
                     ViewModel.EditingUserCompanyProfileId,
                     ProfileId.ToString("D"),
                     StringComparison.OrdinalIgnoreCase);
                 attempt++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(ProfileId.ToString("D"), ViewModel.EditingUserCompanyProfileId);
            return user;
        }

        public async Task<Guid> AddCompanyProfileAsync(string profileName)
        {
            var profile = new LocalCompanyProfile
            {
                Id = Guid.NewGuid(),
                ProfileName = profileName,
                OfficeCode = OfficeCodeCatalog.Usenet,
                IsDefaultForOffice = false
            };
            _db.CompanyProfiles.Add(profile);
            await _db.SaveChangesAsync();
            ViewModel.CompanyProfiles.Add(profile);
            return profile.Id;
        }

        public async ValueTask DisposeAsync()
        {
            _sync.Dispose();
            _httpClient.Dispose();
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private enum SingleDispatchFailure
    {
        HttpRequest,
        ServerError,
        MalformedSuccessBody,
        CanonicalMismatch,
        Conflict
    }

    private sealed class SingleDispatchHandler(SingleDispatchFailure failure) : HttpMessageHandler
    {
        public int SendCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            if (failure == SingleDispatchFailure.HttpRequest)
                throw new HttpRequestException("response lost");

            if (failure == SingleDispatchFailure.ServerError)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("server error")
                });
            }

            if (failure == SingleDispatchFailure.Conflict)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("revision conflict")
                });
            }

            if (failure == SingleDispatchFailure.CanonicalMismatch)
            {
                var response = CreateUpdatedUser(revision: 11);
                response.Role = "Admin";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{")
            });
        }
    }

    private sealed class UserSaveHandler : HttpMessageHandler
    {
        private UserAccountDto? _updatedUser;
        private readonly List<UserAccountDto> _serverUsers = new();

        public HttpStatusCode? CreateStatusCode { get; set; }
        public HttpStatusCode? UpdateStatusCode { get; set; }
        public HttpStatusCode? PasswordStatusCode { get; set; }
        public HttpStatusCode? DeleteStatusCode { get; set; }
        public Exception? CreateFailure { get; set; }
        public Exception? UpdateFailure { get; set; }
        public Exception? PasswordFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public bool CommitCreateBeforeFailure { get; set; }
        public bool OtherAdminCreatesExactMatchBeforeFailure { get; set; }
        public bool CommitUpdateBeforeFailure { get; set; }
        public bool CommitDeleteBeforeFailure { get; set; }
        public bool HideDeleteTargetBeforeFailure { get; set; }
        public int FailUsersGetFromCall { get; set; }
        public Func<UserAccountDto, List<UserAccountDto>>? ReloadUsersFactory { get; set; }
        public int UsersGetCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int PasswordCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public void SeedUser(UserAccountDto user)
        {
            _serverUsers.RemoveAll(current => current.Id == user.Id);
            _serverUsers.Add(CloneUser(user, user.Revision));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post && path == "/users")
            {
                CreateCalls++;
                var create = await request.Content!.ReadFromJsonAsync<CreateUserRequest>(cancellationToken)
                    ?? throw new InvalidDataException("create request missing");
                var created = CreateCanonicalUser(Guid.NewGuid(), 1, create);
                if (OtherAdminCreatesExactMatchBeforeFailure)
                {
                    SeedUser(CreateCanonicalUser(
                        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                        1,
                        create));
                }
                else if (CreateFailure is null || CommitCreateBeforeFailure)
                {
                    _updatedUser = created;
                    SeedUser(created);
                }
                if (CreateFailure is not null)
                    throw CreateFailure;
                if (CreateStatusCode is { } createStatus)
                    return new HttpResponseMessage(createStatus);
                return JsonResponse(created);
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/password", StringComparison.Ordinal))
            {
                PasswordCalls++;
                if (PasswordFailure is not null)
                    throw PasswordFailure;
                return new HttpResponseMessage(PasswordStatusCode ?? HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/users/", StringComparison.Ordinal))
            {
                UpdateCalls++;
                var update = await request.Content!.ReadFromJsonAsync<UpdateUserRequest>(cancellationToken)
                    ?? throw new InvalidDataException("update request missing");
                _updatedUser = CreateCanonicalUser(UserId, update.ExpectedRevision + 1, update);
                if (UpdateFailure is null || CommitUpdateBeforeFailure)
                    SeedUser(_updatedUser);
                if (UpdateFailure is not null)
                    throw UpdateFailure;
                if (UpdateStatusCode is { } updateStatus)
                    return new HttpResponseMessage(updateStatus);
                return JsonResponse(_updatedUser);
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/users/", StringComparison.Ordinal))
            {
                DeleteCalls++;
                if (DeleteFailure is null || CommitDeleteBeforeFailure || HideDeleteTargetBeforeFailure)
                    _serverUsers.RemoveAll(user => user.Id == UserId);
                if (DeleteFailure is not null)
                    throw DeleteFailure;
                return new HttpResponseMessage(DeleteStatusCode ?? HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Get && path == "/users")
            {
                UsersGetCalls++;
                if (FailUsersGetFromCall > 0 && UsersGetCalls >= FailUsersGetFromCall)
                    throw new HttpRequestException("users reload failed");
                var users = _updatedUser is not null && ReloadUsersFactory is not null
                    ? ReloadUsersFactory.Invoke(_updatedUser)
                    : _serverUsers.Select(user => CloneUser(user, user.Revision)).ToList();
                return JsonResponse(users);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value)
            };

        private static UserAccountDto CreateCanonicalUser(
            Guid id,
            long revision,
            CreateUserRequest request)
            => CreateCanonicalUser(
                id,
                revision,
                request.Username,
                request.Role,
                request.TenantCode,
                request.OfficeCode,
                request.ScopeType,
                request.IsActive,
                request.Permissions);

        private static UserAccountDto CreateCanonicalUser(
            Guid id,
            long revision,
            UpdateUserRequest request)
            => CreateCanonicalUser(
                id,
                revision,
                request.Username,
                request.Role,
                request.TenantCode,
                request.OfficeCode,
                request.ScopeType,
                request.IsActive,
                request.Permissions);

        private static UserAccountDto CreateCanonicalUser(
            Guid id,
            long revision,
            string username,
            string role,
            string tenantCode,
            string officeCode,
            string scopeType,
            bool isActive,
            IEnumerable<string> permissions)
            => new()
            {
                Id = id,
                Revision = revision,
                Username = username.Trim(),
                Role = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User",
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode),
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode),
                ScopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType),
                IsActive = isActive,
                Permissions = permissions
                    .Where(permission => !string.IsNullOrWhiteSpace(permission))
                    .Select(permission => permission.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
    }
}
