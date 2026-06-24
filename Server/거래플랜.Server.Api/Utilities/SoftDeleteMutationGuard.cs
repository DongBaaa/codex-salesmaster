using Microsoft.AspNetCore.Mvc;

namespace 거래플랜.Server.Api.Utilities;

public static class SoftDeleteMutationGuard
{
    public const string ErrorCode = "soft_delete_must_use_delete_endpoint";

    public static BadRequestObjectResult RejectUpdate(string entityDisplayName)
        => new(new
        {
            error = ErrorCode,
            message = $"{entityDisplayName} 삭제는 수정(PUT)이 아니라 전용 삭제 API를 사용해야 합니다."
        });
}
