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
}
