using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

public sealed class AccountController : Controller
{
    [AllowAnonymous]
    public IActionResult SignIn(string? returnUrl = null)
    {
        var redirectUri = Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action("Index", "Compare");

        return Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    [HttpPost]
    [ActionName("SignOut")]
    [ValidateAntiForgeryToken]
    public IActionResult SignOutUser()
    {
        return SignOut(
            new AuthenticationProperties { RedirectUri = Url.Action("SignedOut", "Account") },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    [AllowAnonymous]
    public IActionResult SignedOut()
    {
        return View();
    }

    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        return View();
    }

    [AllowAnonymous]
    public IActionResult ExternalAuthFailed(string? message = null)
    {
        ViewData["Message"] = string.IsNullOrWhiteSpace(message)
            ? "Microsoft sign-in did not complete."
            : message;

        return View();
    }

    [AllowAnonymous]
    public IActionResult AdminConsentComplete(bool? admin_consent = null, string? tenant = null, string? error = null, string? error_description = null)
    {
        var succeeded = admin_consent == true && string.IsNullOrWhiteSpace(error);
        ViewData["Succeeded"] = succeeded;
        ViewData["TenantId"] = tenant;
        ViewData["Message"] = succeeded
            ? "Tenant admin consent was granted. Users from this tenant can now sign in if they have an enabled access grant."
            : BuildAdminConsentErrorMessage(error, error_description);

        return View();
    }

    private static string BuildAdminConsentErrorMessage(string? error, string? errorDescription)
    {
        if (!string.IsNullOrWhiteSpace(errorDescription))
        {
            return errorDescription;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return $"Tenant admin consent did not complete: {error}.";
        }

        return "Tenant admin consent did not return a completion status.";
    }
}
