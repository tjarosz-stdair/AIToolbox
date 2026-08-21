using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/McpClientCredentials")]
public sealed class McpClientCredentialsController : Controller
{
    private readonly IMcpClientCredentialRepository _credentials;
    private readonly ISecretProtector _secretProtector;

    public McpClientCredentialsController(
        IMcpClientCredentialRepository credentials,
        ISecretProtector secretProtector)
    {
        _credentials = credentials;
        _secretProtector = secretProtector;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _credentials.GetAllAsync(cancellationToken));
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new McpClientCredentialFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(McpClientCredentialFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var credential = ToCredential(model);
        credential.ClientSecretCipherText = _secretProtector.Protect(model.ClientSecret);
        credential.ClientSecretLastUpdatedUtc = string.IsNullOrWhiteSpace(model.ClientSecret) ? null : DateTime.UtcNow;

        var id = await _credentials.CreateAsync(credential, cancellationToken);
        TempData["StatusMessage"] = "MCP client credential created.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetByIdAsync(id, cancellationToken);
        return credential is null ? NotFound() : View(ToForm(credential));
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, McpClientCredentialFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            model.HasStoredClientSecret = await CredentialHasStoredSecretAsync(id, cancellationToken);
            return View(model);
        }

        var credential = ToCredential(model);
        var updateSecret = model.ClearClientSecret || !string.IsNullOrWhiteSpace(model.ClientSecret);
        if (updateSecret)
        {
            credential.ClientSecretCipherText = model.ClearClientSecret ? null : _secretProtector.Protect(model.ClientSecret);
            credential.ClientSecretLastUpdatedUtc = model.ClearClientSecret ? null : DateTime.UtcNow;
        }

        await _credentials.UpdateAsync(credential, updateSecret, cancellationToken);
        TempData["StatusMessage"] = "MCP client credential saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task<bool> CredentialHasStoredSecretAsync(int id, CancellationToken cancellationToken)
    {
        var credential = await _credentials.GetByIdAsync(id, cancellationToken);
        return !string.IsNullOrWhiteSpace(credential?.ClientSecretCipherText);
    }

    private static McpClientCredential ToCredential(McpClientCredentialFormViewModel model)
    {
        return new McpClientCredential
        {
            Id = model.Id,
            Surface = model.Surface,
            ClientId = model.ClientId,
            TokenEndpointUrl = model.TokenEndpointUrl,
            McpEndpointUrl = model.McpEndpointUrl,
            GrantedScopes = model.GrantedScopes,
            IsEnabled = model.IsEnabled
        };
    }

    private static McpClientCredentialFormViewModel ToForm(McpClientCredential credential)
    {
        return new McpClientCredentialFormViewModel
        {
            Id = credential.Id,
            Surface = credential.Surface,
            ClientId = credential.ClientId,
            TokenEndpointUrl = credential.TokenEndpointUrl,
            McpEndpointUrl = credential.McpEndpointUrl,
            GrantedScopes = credential.GrantedScopes,
            IsEnabled = credential.IsEnabled,
            HasStoredClientSecret = !string.IsNullOrWhiteSpace(credential.ClientSecretCipherText)
        };
    }
}
