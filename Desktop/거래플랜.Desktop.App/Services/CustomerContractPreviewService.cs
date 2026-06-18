using System.Diagnostics;
using System.IO;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public static class CustomerContractPreviewService
{
    public static string BuildPreviewPath(LocalCustomerContract contract)
    {
        var previewDir = Path.Combine(AppPaths.CustomerContractPreviewDir, contract.CustomerId.ToString("N"));
        var extension = string.IsNullOrWhiteSpace(Path.GetExtension(contract.FileName))
            ? ".pdf"
            : Path.GetExtension(contract.FileName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(contract.FileName);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            fileNameWithoutExtension = "customer-contract";

        var safeBaseName = new string(fileNameWithoutExtension.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(safeBaseName))
            safeBaseName = "customer-contract";

        return Path.Combine(previewDir, $"{safeBaseName}_{contract.Id:N}{extension}");
    }

    public static string MaterializePreviewFile(LocalCustomerContract contract)
    {
        if (contract.FileContent is null || contract.FileContent.Length == 0)
            throw new InvalidOperationException("계약서 PDF 내용이 이 PC에 없습니다. 온라인 상태에서 서버 파일 내려받기를 먼저 완료해주세요.");

        var previewPath = BuildPreviewPath(contract);
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        File.WriteAllBytes(previewPath, contract.FileContent);
        return previewPath;
    }

    public static void Open(LocalCustomerContract contract)
    {
        var previewPath = MaterializePreviewFile(contract);
        Process.Start(new ProcessStartInfo(previewPath)
        {
            UseShellExecute = true
        });
    }
}
