using System.Security.Cryptography;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public static class CustomerContractContentService
{
    public static bool HasRegisteredFile(LocalCustomerContract? contract)
        => contract is not null &&
           !contract.IsDeleted &&
           contract.FileSize > 0 &&
           !string.IsNullOrWhiteSpace(contract.FileName) &&
           !string.Equals(contract.FileName, "PDF 미등록", StringComparison.OrdinalIgnoreCase);

    public static bool HasLocalContent(LocalCustomerContract? contract)
        => contract?.FileContent is { Length: > 0 };

    public static async Task<LocalCustomerContract> EnsureContentAsync(
        LocalCustomerContract contract,
        LocalStateService local,
        SessionState session,
        ErpApiClient? api,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(session);

        if (HasLocalContent(contract))
            return contract;

        if (!HasRegisteredFile(contract))
            throw new InvalidOperationException("선택한 계약서에는 아직 PDF 파일이 등록되지 않았습니다.");

        if (session.IsOfflineMode)
            throw new InvalidOperationException("계약서 PDF 내용이 이 PC에 없어 서버에서 내려받아야 합니다. 온라인 모드로 로그인한 뒤 다시 열어주세요.");

        if (api is null)
            throw new InvalidOperationException("계약서 PDF를 내려받을 서버 연결을 준비하지 못했습니다. 프로그램을 다시 실행한 뒤 다시 시도하세요.");

        var content = await api.DownloadCustomerContractContentAsync(contract.Id, ct);
        ValidateDownloadedContent(contract, content);

        var cached = await local.CacheCustomerContractContentAsync(contract.Id, content, session, ct);
        if (!cached)
            throw new InvalidOperationException("계약서 PDF를 내려받았지만 로컬 저장값에 반영하지 못했습니다. 권한 범위와 동기화 상태를 확인하세요.");

        contract.FileContent = content;
        return contract;
    }

    private static void ValidateDownloadedContent(LocalCustomerContract contract, byte[] content)
    {
        if (content.Length == 0)
            throw new InvalidOperationException("서버에서 내려받은 계약서 PDF 내용이 비어 있습니다. 운영 점검에서 파일 저장소 무결성을 확인하세요.");

        if (contract.FileSize > 0 && content.LongLength != contract.FileSize)
        {
            throw new InvalidOperationException(
                $"계약서 파일 크기가 서버 기록과 일치하지 않습니다. 기록 {contract.FileSize:N0}바이트, 실제 {content.LongLength:N0}바이트입니다. 운영 점검에서 파일 저장소 무결성을 확인하세요.");
        }

        if (string.IsNullOrWhiteSpace(contract.FileHash))
            return;

        var actualHash = Convert.ToHexString(SHA256.HashData(content));
        if (!string.Equals(contract.FileHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("계약서 파일 해시가 서버 기록과 일치하지 않습니다. 운영 점검에서 파일 저장소 무결성을 확인하세요.");
    }
}
