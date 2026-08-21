using AIToolbox.Web.Configuration;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/Users")]
public sealed class UsersController : Controller
{
    private readonly IAuthorizedUserRepository _users;
    private readonly IOptions<MicrosoftEntraOptions> _entraOptions;

    public UsersController(
        IAuthorizedUserRepository users,
        IOptions<MicrosoftEntraOptions> entraOptions)
    {
        _users = users;
        _entraOptions = entraOptions;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var users = await _users.GetAllAsync(cancellationToken);
        return View(users);
    }

    [HttpGet("ConsentUrls")]
    public async Task<IActionResult> ConsentUrls(CancellationToken cancellationToken)
    {
        var options = _entraOptions.Value;
        var users = await _users.GetAllAsync(cancellationToken);
        var redirectUri = BuildRedirectUri("/Account/AdminConsentComplete");
        var tenants = options.AllowedTenantIds
            .Where(tenantId => !string.IsNullOrWhiteSpace(tenantId))
            .Select(tenantId => tenantId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tenantId => tenantId)
            .Select(tenantId =>
            {
                var tenantUsers = users
                    .Where(user => string.Equals(user.TenantId?.Trim(), tenantId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new TenantConsentUrlViewModel
                {
                    TenantId = tenantId,
                    InferredDomains = tenantUsers
                        .Select(user => TryGetDomain(user.Email))
                        .Where(domain => !string.IsNullOrWhiteSpace(domain))
                        .Select(domain => domain!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(domain => domain)
                        .ToList(),
                    AuthorizedUserCount = tenantUsers.Count,
                    ConsentUrl = BuildConsentUrl(options.Instance, tenantId, options.ClientId, redirectUri)
                };
            })
            .ToList();

        return View(new TenantConsentUrlsViewModel
        {
            ClientId = options.ClientId,
            RedirectUri = redirectUri,
            AuthorityTenant = options.TenantId,
            Tenants = tenants
        });
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new UserFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var existingUser = await _users.GetByEmailAsync(model.Email, cancellationToken);
        if (existingUser is not null)
        {
            ModelState.AddModelError(nameof(model.Email), "That user already has an access grant.");
            return View(model);
        }

        var id = await _users.CreateAsync(ToUser(model), cancellationToken);
        TempData["StatusMessage"] = "User access grant created.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var user = await _users.GetByIdAsync(id, cancellationToken);
        return user is null ? NotFound() : View(ToForm(user));
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, UserFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await _users.UpdateAsync(ToUser(model), cancellationToken);
        TempData["StatusMessage"] = "User access grant saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    private static AuthorizedUser ToUser(UserFormViewModel model)
    {
        return new AuthorizedUser
        {
            Id = model.Id,
            Email = model.Email,
            DisplayName = model.DisplayName,
            TenantId = model.TenantId,
            IsAdmin = model.IsAdmin,
            IsEnabled = model.IsEnabled
        };
    }

    private static UserFormViewModel ToForm(AuthorizedUser user)
    {
        return new UserFormViewModel
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            TenantId = user.TenantId,
            IsAdmin = user.IsAdmin,
            IsEnabled = user.IsEnabled
        };
    }

    private string BuildRedirectUri(string callbackPath)
    {
        var pathBase = Request.PathBase.ToString().TrimEnd('/');
        var callback = callbackPath.Trim();
        if (!callback.StartsWith('/'))
        {
            callback = "/" + callback;
        }

        return $"{Request.Scheme}://{Request.Host}{pathBase}{callback}";
    }

    private static string BuildConsentUrl(string instance, string tenantId, string clientId, string redirectUri)
    {
        var authority = string.IsNullOrWhiteSpace(instance)
            ? "https://login.microsoftonline.com"
            : instance.TrimEnd('/');
        return $"{authority}/{Uri.EscapeDataString(tenantId)}/adminconsent?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    private static string? TryGetDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        return atIndex < 0 || atIndex == email.Length - 1 ? null : email[(atIndex + 1)..].Trim();
    }
}
