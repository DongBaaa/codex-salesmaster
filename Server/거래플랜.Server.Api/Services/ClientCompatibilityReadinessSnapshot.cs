namespace 거래플랜.Server.Api.Services;

public sealed record ClientCompatibilityReadinessSnapshot(
    string Mode,
    int ConfiguredPolicyCount,
    int EnabledPolicyCount,
    IReadOnlyList<ClientCompatibilityPolicyReadinessSnapshot> Policies)
{
    public static ClientCompatibilityReadinessSnapshot Create(
        ClientCompatibilityOptions? options)
    {
        options ??= new ClientCompatibilityOptions();
        var configured = options.Policies ?? [];
        var policies = configured
            .Where(static policy => policy?.Enabled == true)
            .Select(static policy =>
                new ClientCompatibilityPolicyReadinessSnapshot(
                    AppId: policy!.AppId?.Trim() ?? string.Empty,
                    Platform: policy.Platform?.Trim() ?? string.Empty,
                    PolicyVersion: Math.Max(0, policy.PolicyVersion),
                    RequiresUserAction: policy.RequiresUserAction,
                    MinimumVersion: policy.MinimumVersion?.Trim() ?? string.Empty,
                    MinimumBuild: policy.MinimumBuild,
                    MinimumProtocolVersion: policy.MinimumProtocolVersion,
                    LatestVersion: policy.LatestVersion?.Trim() ?? string.Empty,
                    LatestBuild: policy.LatestBuild))
            .OrderBy(static policy => policy.AppId, StringComparer.Ordinal)
            .ThenBy(static policy => policy.Platform, StringComparer.Ordinal)
            .ToArray();

        return new ClientCompatibilityReadinessSnapshot(
            Mode: string.IsNullOrWhiteSpace(options.Mode)
                ? ClientCompatibilityOptions.AuditOnlyMode
                : options.Mode.Trim(),
            ConfiguredPolicyCount: configured.Count,
            EnabledPolicyCount: policies.Length,
            Policies: policies);
    }
}

public sealed record ClientCompatibilityPolicyReadinessSnapshot(
    string AppId,
    string Platform,
    int PolicyVersion,
    bool RequiresUserAction,
    string MinimumVersion,
    int? MinimumBuild,
    int? MinimumProtocolVersion,
    string LatestVersion,
    int? LatestBuild);
