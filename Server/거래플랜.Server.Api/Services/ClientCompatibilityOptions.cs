namespace 거래플랜.Server.Api.Services;

using Microsoft.Extensions.Options;

public sealed class ClientCompatibilityOptions
{
    public const string SectionName = "ClientCompatibility";
    public const string AuditOnlyMode = "AuditOnly";
    public const string StrictBlockMode = "StrictBlock";

    public string Mode { get; set; } = AuditOnlyMode;
    public List<ClientCompatibilityPolicyOptions?> Policies { get; set; } = [];

    public bool IsStrictBlockMode =>
        string.Equals(Mode, StrictBlockMode, StringComparison.OrdinalIgnoreCase);
}

public sealed class ClientCompatibilityPolicyOptions
{
    public bool Enabled { get; set; } = true;
    public string? AppId { get; set; } = string.Empty;
    public string? Platform { get; set; } = string.Empty;
    public int PolicyVersion { get; set; } = 1;
    public bool RequiresUserAction { get; set; } = true;
    public string? MinimumVersion { get; set; } = string.Empty;
    public int? MinimumBuild { get; set; }
    public int? MinimumProtocolVersion { get; set; }
    public string? LatestVersion { get; set; } = string.Empty;
    public int? LatestBuild { get; set; }
    public string? UpdateUrl { get; set; } = string.Empty;
    public string? UpgradeToken { get; set; } = "georaeplan-client";
}

public sealed class ClientCompatibilityOptionsValidator
    : IValidateOptions<ClientCompatibilityOptions>
{
    private static readonly (string AppId, string Platform)[]
        RequiredStrictClientPolicies =
        [
            ("kr.georaeplan.desktop", "windows"),
            ("kr.georaeplan.mobile", "android")
        ];

    public ValidateOptionsResult Validate(
        string? name,
        ClientCompatibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var mode = options.Mode ?? string.Empty;
        if (!string.Equals(mode, mode.Trim(), StringComparison.Ordinal) ||
            (!string.Equals(
                 mode,
                 ClientCompatibilityOptions.AuditOnlyMode,
                 StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(
                 mode,
                 ClientCompatibilityOptions.StrictBlockMode,
                 StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(
                $"ClientCompatibility:Mode must be exactly '{ClientCompatibilityOptions.AuditOnlyMode}' or '{ClientCompatibilityOptions.StrictBlockMode}'.");
        }

        var enabledPolicies = new List<ClientCompatibilityPolicyOptions>();
        var policies = options.Policies ?? [];
        for (var index = 0; index < policies.Count; index++)
        {
            var policy = policies[index];
            if (policy is null)
            {
                failures.Add(
                    $"ClientCompatibility:Policies:{index} cannot be null.");
                continue;
            }

            if (!policy.Enabled)
                continue;

            enabledPolicies.Add(policy);
            ValidateEnabledPolicy(policy, index, failures);
        }

        var duplicatePolicyKeys = enabledPolicies
            .Where(static policy =>
                !string.IsNullOrWhiteSpace(policy.AppId) &&
                !string.IsNullOrWhiteSpace(policy.Platform))
            .GroupBy(
                static policy =>
                    $"{policy.AppId!.Trim().ToLowerInvariant()}\u001f{policy.Platform!.Trim().ToLowerInvariant()}",
                StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key.Split('\u001f'))
            .ToList();
        foreach (var duplicate in duplicatePolicyKeys)
        {
            failures.Add(
                $"ClientCompatibility contains duplicate enabled policy key appId='{duplicate[0]}', platform='{duplicate[1]}'.");
        }

        if (string.Equals(
                mode,
                ClientCompatibilityOptions.StrictBlockMode,
                StringComparison.OrdinalIgnoreCase) &&
            enabledPolicies.Count == 0)
        {
            failures.Add(
                "ClientCompatibility StrictBlock requires at least one enabled policy.");
        }

        if (string.Equals(
                mode,
                ClientCompatibilityOptions.StrictBlockMode,
                StringComparison.OrdinalIgnoreCase))
        {
            var enabledPolicyKeys = enabledPolicies
                .Where(static policy =>
                    !string.IsNullOrWhiteSpace(policy.AppId) &&
                    !string.IsNullOrWhiteSpace(policy.Platform))
                .Select(static policy =>
                    (
                        AppId: policy.AppId!.Trim(),
                        Platform: policy.Platform!.Trim()))
                .ToHashSet(ClientPolicyKeyComparer.Instance);
            foreach (var required in RequiredStrictClientPolicies)
            {
                if (enabledPolicyKeys.Contains(required))
                {
                    continue;
                }

                failures.Add(
                    $"ClientCompatibility StrictBlock requires enabled policy appId='{required.AppId}', platform='{required.Platform}'.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEnabledPolicy(
        ClientCompatibilityPolicyOptions policy,
        int index,
        ICollection<string> failures)
    {
        var prefix = $"ClientCompatibility:Policies:{index}";
        var appId = policy.AppId?.Trim() ?? string.Empty;
        var platform = policy.Platform?.Trim() ?? string.Empty;

        if (!IsToken(appId, maximumLength: 128))
            failures.Add($"{prefix}:AppId must be a non-empty ASCII token.");
        if (!IsToken(platform, maximumLength: 32))
            failures.Add($"{prefix}:Platform must be a non-empty ASCII token.");
        if (policy.PolicyVersion <= 0)
            failures.Add($"{prefix}:PolicyVersion must be positive.");
        if (!policy.RequiresUserAction)
        {
            failures.Add(
                $"{prefix}:RequiresUserAction must be true for an enabled compatibility policy.");
        }

        Version? minimumVersion = null;
        var minimumVersionText = policy.MinimumVersion?.Trim() ?? string.Empty;
        if (minimumVersionText.Length > 0 &&
            !Version.TryParse(minimumVersionText, out minimumVersion))
        {
            failures.Add($"{prefix}:MinimumVersion must be a numeric System.Version value.");
        }

        if (policy.MinimumBuild is <= 0)
            failures.Add($"{prefix}:MinimumBuild must be positive when provided.");
        if (policy.MinimumProtocolVersion is <= 0)
            failures.Add($"{prefix}:MinimumProtocolVersion must be positive when provided.");
        if (minimumVersionText.Length == 0 &&
            policy.MinimumBuild is null &&
            policy.MinimumProtocolVersion is null)
        {
            failures.Add(
                $"{prefix} must define at least one minimum version, build, or protocol.");
        }

        var latestVersionText = policy.LatestVersion?.Trim() ?? string.Empty;
        if (!Version.TryParse(latestVersionText, out var latestVersion))
        {
            failures.Add($"{prefix}:LatestVersion must be a numeric System.Version value.");
        }
        else if (minimumVersion is not null && latestVersion < minimumVersion)
        {
            failures.Add($"{prefix}:LatestVersion cannot be lower than MinimumVersion.");
        }

        if (policy.LatestBuild is not > 0)
        {
            failures.Add($"{prefix}:LatestBuild must be positive.");
        }
        else if (policy.MinimumBuild is { } minimumBuild &&
                 policy.LatestBuild < minimumBuild)
        {
            failures.Add($"{prefix}:LatestBuild cannot be lower than MinimumBuild.");
        }

        if (!IsSafeUpdateUrl(policy.UpdateUrl))
            failures.Add($"{prefix}:UpdateUrl must be a relative URL or an absolute HTTPS URL.");
        if (!IsUpgradeToken(policy.UpgradeToken))
            failures.Add($"{prefix}:UpgradeToken must be an ASCII token of at most 64 characters.");
    }

    private static bool IsToken(string value, int maximumLength)
        => value.Length is > 0 &&
           value.Length <= maximumLength &&
           value.All(static character =>
               char.IsAsciiLetterOrDigit(character) ||
               character is '.' or '-' or '_');

    private static bool IsUpgradeToken(string? value)
    {
        var token = value?.Trim() ?? string.Empty;
        return token.Length is > 0 and <= 64 &&
               token.All(static character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '.' or '-' or '_');
    }

    private static bool IsSafeUpdateUrl(string? value)
    {
        var url = value?.Trim() ?? string.Empty;
        if (url.Length == 0 ||
            url.Any(static character => char.IsControl(character)))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return string.Equals(
                absolute.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase);
        }

        return Uri.TryCreate(url, UriKind.Relative, out _) &&
               !url.StartsWith("//", StringComparison.Ordinal);
    }

    private sealed class ClientPolicyKeyComparer
        : IEqualityComparer<(string AppId, string Platform)>
    {
        public static ClientPolicyKeyComparer Instance { get; } = new();

        public bool Equals(
            (string AppId, string Platform) left,
            (string AppId, string Platform) right)
            => string.Equals(
                   left.AppId,
                   right.AppId,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   left.Platform,
                   right.Platform,
                   StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string AppId, string Platform) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.AppId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Platform));
    }
}
