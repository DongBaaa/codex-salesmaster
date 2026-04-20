using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("runtime/scope-matrix")]
public sealed class ScopeDiagnosticsController : ControllerBase
{
    private readonly OfficeScopeService _officeScopeService;

    public ScopeDiagnosticsController(OfficeScopeService officeScopeService)
    {
        _officeScopeService = officeScopeService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ScopeMatrixSnapshotDto), StatusCodes.Status200OK)]
    public ActionResult<ScopeMatrixSnapshotDto> Get()
        => Ok(_officeScopeService.BuildCurrentScopeMatrix());
}
