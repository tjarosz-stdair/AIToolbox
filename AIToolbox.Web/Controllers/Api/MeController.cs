using AIToolbox.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers.Api;

[ApiController]
[Authorize(Policy = "ActiveUser")]
[Route("api/v1/me")]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            email = User.GetEmailAddress(),
            tenant = User.GetTenantId(),
            role = User.IsInRole("Admin") ? "Admin" : "User"
        });
    }
}
