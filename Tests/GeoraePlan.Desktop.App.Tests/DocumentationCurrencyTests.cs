using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DocumentationCurrencyTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData("사용 메뉴얼.md")]
    [InlineData("Mobile/GeoraePlan.Mobile.App/README.md")]
    public void Documentation_StatesCurrentSourceAndLocalStableVersions(
        string relativePath)
    {
        var root = FindRepositoryRoot();
        var lines = ReadLines(root, relativePath);
        var versions = LoadCurrentVersions(root);
        var missing = new List<string>();

        RequireLine(
            lines,
            missing,
            "Desktop csproj Version",
            versions.DesktopVersion,
            ["Desktop", "Windows PC", "Windows", "PC"],
            ["소스", "source", "Version"]);
        RequireVersion(
            lines,
            missing,
            "Desktop csproj FileVersion",
            versions.DesktopFileVersion);
        RequireLine(
            lines,
            missing,
            "local stable manifest desktop version",
            versions.StableDesktopVersion,
            ["Desktop", "Windows PC", "Windows", "PC"],
            ["stable", "공개"]);
        RequireLine(
            lines,
            missing,
            "Mobile csproj ApplicationDisplayVersion",
            versions.MobileDisplayVersion,
            ["Android", "모바일", "APK"],
            ["소스", "source", "ApplicationDisplayVersion"]);
        RequireLine(
            lines,
            missing,
            "Mobile csproj ApplicationVersion",
            versions.MobileApplicationVersion,
            ["Android", "모바일", "APK"],
            ["소스", "source"],
            ["ApplicationVersion", "versionCode"]);
        RequireLine(
            lines,
            missing,
            "local stable manifest Android version",
            versions.StableAndroidVersion,
            ["Android", "모바일", "APK"],
            ["stable", "공개"]);

        Assert.True(
            missing.Count == 0,
            $"{relativePath}에 현재 버전 정보가 누락되었습니다:{Environment.NewLine}- " +
            string.Join($"{Environment.NewLine}- ", missing));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("사용 메뉴얼.md")]
    [InlineData("Mobile/GeoraePlan.Mobile.App/README.md")]
    public void Documentation_StatesMobileUnsupportedScopeAndPcOnlyOperations(
        string relativePath)
    {
        var root = FindRepositoryRoot();
        var lines = ReadLines(root, relativePath);
        var pcOnlyScope = ReadPcOnlyScope(lines);
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(pcOnlyScope))
            missing.Add("모바일 미지원 또는 PC 전용 범위");
        RequireText(
            pcOnlyScope,
            missing,
            "사용자/권한 관리는 PC 전용",
            text =>
                text.Contains("사용자", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("권한", StringComparison.OrdinalIgnoreCase));
        RequireText(
            pcOnlyScope,
            missing,
            "일반 백업/복원은 PC 전용",
            text =>
                text.Contains("백업", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("복원", StringComparison.OrdinalIgnoreCase));
        RequireText(
            pcOnlyScope,
            missing,
            "Excel/자료집계는 PC 전용",
            text =>
                ContainsAny(text, "Excel", "엑셀") &&
                text.Contains("자료", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("집계", StringComparison.OrdinalIgnoreCase));
        RequireText(
            pcOnlyScope,
            missing,
            "재고이동 확정은 PC 전용",
            text =>
                text.Contains("재고이동", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(text, "확정", "수령", "반려"));
        RequireText(
            pcOnlyScope,
            missing,
            "렌탈 수정은 PC 전용",
            text =>
                text.Contains("렌탈", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("수정", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            missing.Count == 0,
            $"{relativePath}에 모바일 업무 경계가 누락되었습니다:{Environment.NewLine}- " +
            string.Join($"{Environment.NewLine}- ", missing));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("사용 메뉴얼.md")]
    [InlineData("Mobile/GeoraePlan.Mobile.App/README.md")]
    public void Documentation_RequiresTestVersionValidationAndApprovalBeforeLive(
        string relativePath)
    {
        var root = FindRepositoryRoot();
        var text = string.Join('\n', ReadLines(root, relativePath));
        var normalized = Regex.Replace(text, @"\s+", " ");

        Assert.Matches(
            new Regex(
                @"(?:테스트판.{0,40}검증|테스트\s*시행.{0,100}(?:먼저|선|사전).{0,30}검증)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            normalized);
        Assert.Matches(
            new Regex(
                @"승인.{0,30}전.{0,40}live.{0,50}(?:수행하지|실행하지|진행하지|금지|불가)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            normalized);
    }

    [Fact]
    public void RootReadme_SeparatesCompletedDesktopReleaseFromRemainingExternalGates()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var staleCompletionClaims = new[]
        {
            "현재 버전의 정식 패키지와 Linux PC live 반영을 완료했고",
            "Goal 관련 585개 파일",
            "Android versionCode 증가, Release signing, emulator `adb install -r` 검증 완료",
        };

        foreach (var staleClaim in staleCompletionClaims)
        {
            Assert.DoesNotContain(staleClaim, readme, StringComparison.Ordinal);
        }

        Assert.Contains(
            "현재 소스 `1.1.698`의 정식 패키지 생성과 Linux PC live 반영을 완료했습니다.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows Authenticode 공개 신뢰 서명과 실제 기기 설치는 수행하지 않았습니다.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "[공개]` 현재 Goal 변경의 선택 Git stage/commit/push와 원격 SHA 확인을 완료했습니다.",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GoalTracker_CurrentAuthoritativeSummaryPrecedesHistoryAndKeepsExternalGatesOpen()
    {
        var root = FindRepositoryRoot();
        var tracker = File.ReadAllText(Path.Combine(
            root,
            "tasks",
            "거래플랜-전체-품질화-Goal-현황.md"));
        const string currentMarker =
            "## 2026-08-22 현재 authoritative 완료 감사";
        const string historicalMarker =
            "## 2026-08-13 04:42 KST 최신 보호 source-users snapshot과 격리 seed 충돌 차단";
        var currentIndex = tracker.IndexOf(currentMarker, StringComparison.Ordinal);
        var historicalIndex = tracker.IndexOf(historicalMarker, StringComparison.Ordinal);

        Assert.True(currentIndex >= 0, "현재 authoritative 완료 감사가 없습니다.");
        Assert.True(
            historicalIndex > currentIndex,
            "현재 authoritative 완료 감사가 누적 이력보다 먼저 나와야 합니다.");

        var current = tracker[currentIndex..historicalIndex];
        var required = new[]
        {
            "Goal 상태: **진행 중**",
            "soak-20260813-190113",
            "1,181행에서 끝났고",
            "soak-quality-20260820-150531",
            "정확히 1,440개, bad 0",
            "Windows Authenticode",
            "Android production keystore",
            "실제 종이 출력",
            "외부 backup replica",
            "`live 반영` 승인",
            "Git stage/commit/push",
            "승인 전 수행하지 않습니다.",
        };

        foreach (var value in required)
            Assert.Contains(value, current, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Goal 상태: **완료**",
            current,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "전체 Goal 완료를 선언합니다",
            current,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("사용 메뉴얼.md")]
    [InlineData("Mobile/GeoraePlan.Mobile.App/README.md")]
    public void Documentation_CurrentAndroidClaimsMatchTrackedStableManifest(
        string relativePath)
    {
        var root = FindRepositoryRoot();
        var lines = ReadLines(root, relativePath);
        var versions = LoadCurrentVersions(root);
        var staleClaims = FindStaleAndroidClaims(
            lines,
            versions.MobileDisplayVersion,
            versions.MobileApplicationVersion,
            versions.StableAndroidVersion,
            versions.StableAndroidFileName);

        Assert.True(
            staleClaims.Count == 0,
            $"{relativePath}에서 오래된 Android APK를 최신으로 지칭합니다:{Environment.NewLine}- " +
            string.Join($"{Environment.NewLine}- ", staleClaims));
    }

    [Fact]
    public void AndroidClaimValidator_AllowsSourceAndStableVersionsToDiverge()
    {
        var lines = new[]
        {
            "- `[공개]` Android `0.2.81`",
            "- `[로컬검증]` Android 현재 소스 `0.2.82`, versionCode `193`",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Empty(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsIncorrectStableVersionAndFileName()
    {
        var lines = new[]
        {
            "- Android APK 최신 stable: `0.2.80`, `tradeplan-android-v0.2.80.apk`",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsMixedRoleLineWhenVersionsDiverge()
    {
        var lines = new[]
        {
            "- Android 공개 stable·현재 소스: `0.2.81`",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStaleSourceVersionCode()
    {
        var lines = new[]
        {
            "- Android source versionCode 192",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsUntrackedStableVersionCodeClaim()
    {
        var lines = new[]
        {
            "- Android source `0.2.82`, versionCode `193`",
            "- Android public stable `0.2.81`, versionCode **192**",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
        Assert.Contains("public stable", staleClaims[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStaleSecondVersionCodeClaim()
    {
        var lines = new[]
        {
            "- Public APK versionCode is checked by the gate. Android source versionCode 192",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStaleClaimsInheritedFromSourceHeading()
    {
        var lines = new[]
        {
            "## Android current source",
            "- display version: **0.2.80**",
            "- versionCode: `191`",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Equal(2, staleClaims.Count);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStableVersionInContinuationClause()
    {
        var lines = new[]
        {
            "- Android public stable display version, 0.2.80",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_AllowsLatestSourceWithoutStableRole()
    {
        var lines = new[]
        {
            "- Android 최신 소스: 0.2.82, versionCode 193",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Empty(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStaleStableUnderKoreanAndroidHeading()
    {
        var lines = new[]
        {
            "# 안드로이드 배포 현황",
            "- 공개 stable 표시 버전: 0.2.80",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Single(staleClaims);
    }

    [Fact]
    public void AndroidClaimValidator_RejectsStaleStableUnderLatestArtifactsHeading()
    {
        var lines = new[]
        {
            "## 최신 Android 산출물",
            "- 표시 버전: 0.2.80",
            "- APK: tradeplan-android-v0.2.80.apk",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Equal(2, staleClaims.Count);
    }

    [Fact]
    public void AndroidClaimValidator_AllowsLatestClaimInheritedFromSourceHeading()
    {
        var lines = new[]
        {
            "## Android current source",
            "- latest display version: 0.2.82",
        };

        var staleClaims = FindStaleAndroidClaims(
            lines,
            sourceVersion: "0.2.82",
            sourceVersionCode: "193",
            stableVersion: "0.2.81",
            stableFileName: "tradeplan-android-v0.2.81.apk");

        Assert.Empty(staleClaims);
    }

    [Fact]
    public void UserManualBuildInputs_AreRepositoryResidentAndVersionDerived()
    {
        var root = FindRepositoryRoot();
        var generatorPath = Path.Combine(
            root,
            "tools",
            "manual",
            "build_user_manual_pdf.py");
        var wrapperPath = Path.Combine(
            root,
            "tools",
            "manual",
            "Build-GeoraePlanUserManualPdf.ps1");
        var requirementsPath = Path.Combine(
            root,
            "tools",
            "manual",
            "requirements.lock.txt");
        var captureManifestPath = Path.Combine(
            root,
            "tools",
            "manual",
            "assets",
            "capture-manifest.json");

        Assert.True(File.Exists(generatorPath), $"PDF generator is missing: {generatorPath}");
        Assert.True(File.Exists(wrapperPath), $"PDF build wrapper is missing: {wrapperPath}");
        Assert.True(File.Exists(requirementsPath), $"PDF dependency lock is missing: {requirementsPath}");
        Assert.True(File.Exists(captureManifestPath), $"Capture manifest is missing: {captureManifestPath}");

        var generator = File.ReadAllText(generatorPath);
        Assert.Contains("거래플랜.Desktop.App.csproj", generator, StringComparison.Ordinal);
        Assert.Contains("GeoraePlan.Mobile.App.csproj", generator, StringComparison.Ordinal);
        Assert.Contains("\"배포\", \"stable.json\"", generator, StringComparison.Ordinal);
        Assert.Contains("validate_pdf", generator, StringComparison.Ordinal);
        Assert.Contains("android_supported_section", generator, StringComparison.Ordinal);
        Assert.Contains("android_pc_only_section", generator, StringComparison.Ordinal);
        Assert.Contains("georaeplan-current-wpf-exact-matrix-v2", generator, StringComparison.Ordinal);
        Assert.Contains("modelledMeasurementCount", generator, StringComparison.Ordinal);
        Assert.Contains("sourceEvidence", generator, StringComparison.Ordinal);
        Assert.Contains("[\"동기화\", \"동기화\"]", generator, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"required_supported_fragments\s*=\s*\((?s:.*?)""동기화""",
                RegexOptions.CultureInvariant),
            generator);
        foreach (var phrase in new[]
                 {
                     "거래처 조회·입력",
                     "품목 조회·입력",
                     "무결성 상태 조회",
                     "동기화",
                     "사용자·권한 관리",
                     "일반 백업/복원",
                     "Excel 내보내기와 자료집계",
                     "재고이동 생성·수령·반려",
                     "렌탈 청구 생성·입금과 렌탈 프로필·자산 수정",
                 })
        {
            Assert.Contains(phrase, generator, StringComparison.Ordinal);
        }
        Assert.DoesNotMatch(
            new Regex(
                @"(?:LOCAL_DESKTOP_VERSION|PUBLIC_STABLE_DESKTOP_VERSION|ANDROID_VERSION|ANDROID_VERSION_CODE)\s*=\s*[""']\d",
                RegexOptions.CultureInvariant),
            generator);

        var wrapper = File.ReadAllText(wrapperPath);
        Assert.Contains("--require-hashes", wrapper, StringComparison.Ordinal);
        Assert.Contains("requirements.lock.txt", wrapper, StringComparison.Ordinal);
        Assert.Contains("build_user_manual_pdf.py", wrapper, StringComparison.Ordinal);

        var requirements = File.ReadAllText(requirementsPath);
        foreach (var package in new[] { "Pillow", "pypdf", "reportlab", "charset-normalizer" })
        {
            Assert.Matches(
                new Regex(
                    $@"(?im)^{Regex.Escape(package)}==[^\r\n]+\s+--hash=sha256:[0-9a-f]{{64}}\s*$",
                    RegexOptions.CultureInvariant),
                requirements);
        }

        using var captureManifest = JsonDocument.Parse(
            File.ReadAllText(captureManifestPath));
        var captureRoot = captureManifest.RootElement;
        Assert.Equal(2, captureRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-08-22", captureRoot.GetProperty("captureDate").GetString());
        Assert.Equal("1.1.693", captureRoot.GetProperty("desktopVersion").GetString());

        var sourceEvidence = captureRoot.GetProperty("sourceEvidence");
        Assert.Equal("georaeplan-current-wpf-exact-matrix-v2", sourceEvidence.GetProperty("kind").GetString());
        Assert.Equal(
            "6182B6A19A67D7976E27A1C1EF5D39EA27E471111F7C3C67D752B92DFDE2CCC5",
            sourceEvidence.GetProperty("resultSha256").GetString());
        Assert.Equal(
            "C1DD126443642E9D882CCE0693D8EF23F4843D30D50BE23205223EB74E0CE493",
            sourceEvidence.GetProperty("assemblySha256").GetString());
        Assert.Equal(768, sourceEvidence.GetProperty("measurementCount").GetInt32());
        Assert.Equal(36, sourceEvidence.GetProperty("successScreenshotCount").GetInt32());
        Assert.Equal(0, sourceEvidence.GetProperty("modelledMeasurementCount").GetInt32());

        var screenshots = captureRoot
            .GetProperty("screenshots")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(15, screenshots.Length);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var sourceWindows = new HashSet<string>(StringComparer.Ordinal);
        var screenshotHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var screenshot in screenshots)
        {
            var fileName = screenshot.GetProperty("fileName").GetString()!;
            var sourceWindow = screenshot.GetProperty("sourceWindow").GetString()!;
            var expectedHash = screenshot.GetProperty("sha256").GetString()!;
            Assert.Equal(Path.GetFileName(fileName), fileName);
            Assert.Matches("^[A-Za-z][A-Za-z0-9]*Window$", sourceWindow);
            Assert.Matches("^[0-9A-F]{64}$", expectedHash);
            Assert.True(fileNames.Add(fileName), $"Manual screenshot name is duplicated: {fileName}");
            Assert.True(sourceWindows.Add(sourceWindow), $"Manual source window is duplicated: {sourceWindow}");
            Assert.True(screenshotHashes.Add(expectedHash), $"Manual screenshot hash is duplicated: {expectedHash}");

            var screenshotPath = Path.Combine(
                root,
                "tools",
                "manual",
                "assets",
                "screenshots",
                fileName);
            Assert.True(File.Exists(screenshotPath), $"Manual screenshot is missing: {screenshotPath}");
            Assert.Equal(
                expectedHash,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(screenshotPath))),
                ignoreCase: true);
        }
        Assert.DoesNotContain("04_customer_menu.png", fileNames);
        Assert.DoesNotContain("16_recycle_bin.png", fileNames);
        Assert.Contains("18_trade_print.png", fileNames);
        Assert.Contains("19_sync_diagnostics.png", fileNames);
    }

    private static CurrentVersions LoadCurrentVersions(string root)
    {
        var desktopProject = XDocument.Load(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "거래플랜.Desktop.App.csproj"));
        var mobileProject = XDocument.Load(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "GeoraePlan.Mobile.App.csproj"));
        using var stableManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "배포",
            "stable.json")));
        var stableAndroid = stableManifest.RootElement.GetProperty("android");

        return new CurrentVersions(
            ReadProjectProperty(desktopProject, "Version"),
            ReadProjectProperty(desktopProject, "FileVersion"),
            ReadProjectProperty(mobileProject, "ApplicationDisplayVersion"),
            ReadProjectProperty(mobileProject, "ApplicationVersion"),
            stableManifest.RootElement
                .GetProperty("desktop")
                .GetProperty("version")
                .GetString()!,
            stableAndroid.GetProperty("version").GetString()!,
            stableAndroid.GetProperty("fileName").GetString()!);
    }

    private static IReadOnlyList<string> FindStaleAndroidClaims(
        IReadOnlyList<string> lines,
        string sourceVersion,
        string sourceVersionCode,
        string stableVersion,
        string stableFileName)
    {
        var staleClaims = new List<string>();
        var headingContexts = new AndroidClaimContext?[7];
        foreach (var line in lines)
        {
            var heading = Regex.Match(
                line,
                @"^\s*(?<marks>#{1,6})\s+(?<text>.+?)\s*$",
                RegexOptions.CultureInvariant);
            var inheritedContext = FindNearestHeadingContext(
                headingContexts,
                maximumLevel: heading.Success
                    ? heading.Groups["marks"].Value.Length - 1
                    : 6);
            var claimText = heading.Success
                ? heading.Groups["text"].Value
                : line;
            if (heading.Success)
            {
                var headingLevel = heading.Groups["marks"].Value.Length;
                for (var level = headingLevel; level < headingContexts.Length; level++)
                    headingContexts[level] = null;
                headingContexts[headingLevel] = ApplyClaimMarkers(
                    inheritedContext,
                    claimText);
            }

            var clauseContext = inheritedContext;
            var lineIsStale = false;
            foreach (var clause in claimText.Split([',', ';', '，', '；']))
            {
                clauseContext = ApplyClaimMarkers(clauseContext, clause);
                if (!clauseContext.IsAndroid ||
                    (!clauseContext.IsSource && !clauseContext.IsStable))
                {
                    continue;
                }

                var claimedAndroidVersions = Regex.Matches(
                        clause,
                        @"(?<![0-9.])\d+\.\d+\.\d+(?![0-9.])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .Select(match => match.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var claimedApkNames = Regex.Matches(
                        clause,
                        @"tradeplan-android-v[^\\/\s`""]+\.apk",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .Select(match => match.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var claimedVersionCodes = ExtractClaimedVersionCodes(clause);
                var hasClaim =
                    claimedAndroidVersions.Length > 0 ||
                    claimedApkNames.Length > 0 ||
                    claimedVersionCodes.Length > 0;
                if (!hasClaim)
                    continue;

                var mixedRoleMustBeSplit =
                    clauseContext.IsSource && clauseContext.IsStable;
                var staleVersion =
                    claimedAndroidVersions.Any(version =>
                        clauseContext.IsSource &&
                        !string.Equals(
                            version,
                            sourceVersion,
                            StringComparison.Ordinal) ||
                        clauseContext.IsStable &&
                        !string.Equals(
                            version,
                            stableVersion,
                            StringComparison.Ordinal));
                var staleSourceVersionCode =
                    clauseContext.IsSource &&
                    claimedVersionCodes.Any(versionCode =>
                        !string.Equals(
                            versionCode,
                            sourceVersionCode,
                            StringComparison.Ordinal));
                var untrackedStableVersionCode =
                    clauseContext.IsStable &&
                    claimedVersionCodes.Length > 0;
                var staleStableFileName =
                    clauseContext.IsStable &&
                    claimedApkNames.Any(fileName =>
                        !string.Equals(
                            fileName,
                            stableFileName,
                            StringComparison.OrdinalIgnoreCase));
                if (mixedRoleMustBeSplit ||
                    staleVersion ||
                    staleSourceVersionCode ||
                    untrackedStableVersionCode ||
                    staleStableFileName)
                {
                    lineIsStale = true;
                    break;
                }
            }

            if (lineIsStale)
                staleClaims.Add(line.Trim());
        }

        return staleClaims;
    }

    private static AndroidClaimContext FindNearestHeadingContext(
        IReadOnlyList<AndroidClaimContext?> headingContexts,
        int maximumLevel)
    {
        for (var level = Math.Min(maximumLevel, headingContexts.Count - 1);
             level >= 1;
             level--)
        {
            if (headingContexts[level] is { } context)
                return context;
        }

        return new AndroidClaimContext(
            IsAndroid: false,
            IsSource: false,
            IsStable: false);
    }

    private static AndroidClaimContext ApplyClaimMarkers(
        AndroidClaimContext inherited,
        string text)
    {
        var hasAndroidMarker = ContainsAny(
            text,
            "Android",
            "안드로이드",
            "모바일",
            "APK",
            "tradeplan-android-");
        var hasDesktopMarker = ContainsAny(
            text,
            "Desktop",
            "Windows",
            "데스크톱",
            "PC");
        var isAndroid = hasAndroidMarker
            ? true
            : hasDesktopMarker
                ? false
                : inherited.IsAndroid;

        var hasSourceMarker = ContainsAny(
            text,
            "소스",
            "source",
            "ApplicationDisplayVersion",
            "ApplicationVersion");
        var hasLatestMarker = ContainsAny(
            text,
            "최신",
            "latest");
        var hasStableMarker =
            ContainsAny(text, "stable", "public", "published", "[공개]") ||
            (hasLatestMarker && !hasSourceMarker && !inherited.IsSource) ||
            Regex.IsMatch(
                text,
                @"공개\s*(?:stable|Android|안드로이드|모바일|APK|버전|표시)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var isSource = hasSourceMarker
            ? true
            : hasStableMarker
                ? false
                : inherited.IsSource;
        var isStable = hasStableMarker
            ? true
            : hasSourceMarker
                ? false
                : inherited.IsStable;
        if (hasSourceMarker && hasStableMarker)
        {
            isSource = true;
            isStable = true;
        }

        return new AndroidClaimContext(isAndroid, isSource, isStable);
    }

    private static string[] ExtractClaimedVersionCodes(string line)
    {
        var labels = Regex.Matches(
                line,
                @"(?:versionCode|ApplicationVersion)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .ToArray();
        var values = new List<string>();
        for (var index = 0; index < labels.Length; index++)
        {
            var start = labels[index].Index + labels[index].Length;
            var end = index + 1 < labels.Length
                ? labels[index + 1].Index
                : line.Length;
            var clause = line[start..end];
            var clauseEnd = clause.IndexOfAny([',', ';', '.']);
            if (clauseEnd >= 0)
                clause = clause[..clauseEnd];

            values.AddRange(
                Regex.Matches(
                        clause,
                        @"(?<![0-9.])(?<value>[1-9]\d*)(?![0-9.])",
                        RegexOptions.CultureInvariant)
                    .Select(match => match.Groups["value"].Value));
        }

        return values
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadProjectProperty(XDocument project, string name)
        => project
            .Descendants()
            .Single(element => element.Name.LocalName == name)
            .Value
            .Trim();

    private static void RequireVersion(
        IReadOnlyCollection<string> lines,
        ICollection<string> missing,
        string description,
        string version)
    {
        if (!lines.Any(line => ContainsVersion(line, version)))
            missing.Add($"{description} `{version}`");
    }

    private static void RequireLine(
        IReadOnlyCollection<string> lines,
        ICollection<string> missing,
        string description,
        string version,
        params string[][] requiredTokenGroups)
    {
        if (!lines.Any(line =>
                ContainsVersion(line, version) &&
                requiredTokenGroups.All(group => ContainsAny(line, group))))
        {
            missing.Add($"{description} `{version}`");
        }
    }

    private static void RequireMatchingLine(
        IReadOnlyCollection<string> lines,
        ICollection<string> missing,
        string description,
        Func<string, bool> predicate)
    {
        if (!lines.Any(predicate))
            missing.Add(description);
    }

    private static void RequireText(
        string text,
        ICollection<string> missing,
        string description,
        Func<string, bool> predicate)
    {
        if (!predicate(text))
            missing.Add(description);
    }

    private static string ReadPcOnlyScope(IReadOnlyList<string> lines)
    {
        var headingIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].StartsWith('#') &&
                lines[index].Contains("PC", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(lines[index], "해야", "전용", "미지원"))
            {
                headingIndex = index;
                break;
            }
        }

        if (headingIndex < 0)
            return string.Empty;

        var headingLevel = lines[headingIndex].TakeWhile(character => character == '#').Count();
        var sectionLines = new List<string>();
        for (var index = headingIndex; index < lines.Count; index++)
        {
            var line = lines[index];
            if (index > headingIndex && line.StartsWith('#'))
            {
                var level = line.TakeWhile(character => character == '#').Count();
                if (level <= headingLevel)
                    break;
            }

            sectionLines.Add(line);
        }

        return string.Join('\n', sectionLines);
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsVersion(string line, string version)
        => Regex.IsMatch(
            line,
            $@"(?<![0-9.]){Regex.Escape(version)}(?![0-9.])",
            RegexOptions.CultureInvariant);

    private static string[] ReadLines(string root, string relativePath)
        => File.ReadAllLines(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitMarker = Path.Combine(directory.FullName, ".git");
            if ((Directory.Exists(gitMarker) || File.Exists(gitMarker)) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private readonly record struct AndroidClaimContext(
        bool IsAndroid,
        bool IsSource,
        bool IsStable);

    private sealed record CurrentVersions(
        string DesktopVersion,
        string DesktopFileVersion,
        string MobileDisplayVersion,
        string MobileApplicationVersion,
        string StableDesktopVersion,
        string StableAndroidVersion,
        string StableAndroidFileName);
}
