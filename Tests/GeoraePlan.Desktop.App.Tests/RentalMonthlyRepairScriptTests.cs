using System.Diagnostics;
using System.Text;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalMonthlyRepairScriptTests
{
    [Fact]
    public async Task ZeroFeeMissingAssets_DoNotInflatePaidQuantityOrTemplateAmount()
    {
        var root = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"rental-monthly-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var harnessPath = Path.Combine(root, "harness.ps1");

        try
        {
            var sourceScript = Path.Combine(
                ResolveProjectRoot(),
                "tools",
                "maintenance",
                "Invoke-GeoraePlanRentalMonthlyRepair.ps1");
            var functionNames = new[]
            {
                "Get-Decimal",
                "Get-StringValue",
                "Set-NoteProperty",
                "Get-TemplateItems",
                "Get-TemplateLineAmount",
                "New-MissingAssetRows",
                "Format-MissingAssetPreview",
                "Get-DefaultRentalFeeName",
                "Get-DefaultBundleMode",
                "Test-DecimalEquals",
                "Set-NormalizedTemplateFromAssets"
            };
            var quotedNames = string.Join(
                ",",
                functionNames.Select(name => $"'{name}'"));
            var harness = $$$"""
                param([Parameter(Mandatory=$true)][string]$SourceScript)
                $ErrorActionPreference='Stop'
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,[ref]$tokens,[ref]$errors)
                if($errors.Count-ne0){throw (($errors|% Message)-join [Environment]::NewLine)}
                foreach($name in @({{{quotedNames}}})){
                    $node=$ast.Find({param($n) $n-is [Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq$name},$true)
                    if($null-eq$node){throw "missing function: $name"}
                    . ([scriptblock]::Create($node.Extent.Text))
                }
                $existing=@('paid-1','paid-2','paid-3','paid-4')
                $profile=[pscustomobject]@{
                    monthlyAmount=[decimal]960000
                    itemName='test item'
                    billingType='개별'
                    billingTemplateJson=(@([ordered]@{
                        ItemId='line-1';DisplayItemName='test item';BillingLineMode='개별'
                        Specification='';Unit='';MaterialNumber='';RepresentativeAssetId=$null
                        Quantity=[decimal]4;UnitPrice=[decimal]240000;Amount=[decimal]960000;Note=''
                        IncludedAssetIds=$existing
                    })|ConvertTo-Json -Depth 20 -Compress)
                }
                $assets=@()
                foreach($id in $existing){$assets+=[pscustomobject]@{id=$id;isDeleted=$false;monthlyFee=[decimal]240000;itemName='paid'}}
                foreach($id in @('free-1','free-2','free-3')){$assets+=[pscustomobject]@{id=$id;isDeleted=$false;monthlyFee=[decimal]0;itemName='free'}}
                $result=Set-NormalizedTemplateFromAssets -Profile $profile -LinkedAssets $assets
                $line=@($result.TemplateJson|ConvertFrom-Json)[0]
                if([decimal]$line.Quantity-ne4){throw "quantity=$($line.Quantity)"}
                if([decimal]$line.UnitPrice-ne240000){throw "unitPrice=$($line.UnitPrice)"}
                if([decimal]$line.Amount-ne960000){throw "amount=$($line.Amount)"}
                if(@($line.IncludedAssetIds).Count-ne7){throw "assetCount=$(@($line.IncludedAssetIds).Count)"}
                if([decimal]$result.TemplateMonthlyAmount-ne960000){throw "templateTotal=$($result.TemplateMonthlyAmount)"}
                if([decimal]$result.LinkedAssetMonthlyAmount-ne960000){throw "assetTotal=$($result.LinkedAssetMonthlyAmount)"}
                if([int]$result.MissingAssetCount-ne3){throw "missing=$($result.MissingAssetCount)"}
                'zero-fee-quantity-contract=PASS'
                """;
            await File.WriteAllTextAsync(
                harnessPath,
                harness,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(harnessPath);
            startInfo.ArgumentList.Add("-SourceScript");
            startInfo.ArgumentList.Add(sourceScript);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0, $"stdout={stdout}\nstderr={stderr}");
            Assert.Contains("zero-fee-quantity-contract=PASS", stdout, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string ResolveProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "tools")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Project root was not found.");
    }
}
