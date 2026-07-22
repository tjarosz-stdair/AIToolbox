using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/ApiConsumers")]
public sealed class ApiConsumersController : Controller
{
    private readonly IApiConsumerRepository _consumers;
    private readonly IApiKeyService _apiKeys;

    public ApiConsumersController(IApiConsumerRepository consumers, IApiKeyService apiKeys)
    {
        _consumers = consumers;
        _apiKeys = apiKeys;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        return View(await _consumers.GetConsumersAsync(cancellationToken));
    }

    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View(new ApiConsumerFormViewModel());
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ApiConsumerFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var id = await _consumers.CreateConsumerAsync(ToConsumer(model), cancellationToken);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken cancellationToken)
    {
        var consumer = await _consumers.GetConsumerAsync(id, cancellationToken);
        return consumer is null
            ? NotFound()
            : View(new ApiConsumerDetailViewModel
            {
                Consumer = consumer,
                NewKey = new ApiConsumerKeyFormViewModel { ApiConsumerId = id }
            });
    }

    [HttpPost("{id:int}/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, ApiConsumerFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Detail), new ApiConsumerDetailViewModel
            {
                Consumer = ToConsumer(model),
                NewKey = new ApiConsumerKeyFormViewModel { ApiConsumerId = id }
            });
        }

        await _consumers.UpdateConsumerAsync(ToConsumer(model), cancellationToken);
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost("{id:int}/Keys/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateKey(int id, ApiConsumerKeyFormViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.ApiConsumerId)
        {
            return BadRequest();
        }

        var consumer = await _consumers.GetConsumerAsync(id, cancellationToken);
        if (consumer is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(nameof(Detail), new ApiConsumerDetailViewModel { Consumer = consumer, NewKey = model });
        }

        var rawKey = _apiKeys.GenerateRawKey();
        var key = new ApiConsumerKey
        {
            ApiConsumerId = id,
            Name = model.Name.Trim(),
            KeyPrefix = _apiKeys.GetPrefix(rawKey),
            KeyHash = _apiKeys.HashKey(rawKey),
            ScopesJson = JsonSerializer.Serialize(ParseScopes(model.Scopes), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ExpiresUtc = model.ExpiresUtc
        };

        await _consumers.CreateKeyAsync(key, cancellationToken);
        TempData["RawApiKey"] = rawKey;
        TempData["KeyPrefix"] = key.KeyPrefix;
        return RedirectToAction(nameof(KeyCreated), new { id });
    }

    [HttpGet("{id:int}/Keys/Created")]
    public async Task<IActionResult> KeyCreated(int id, CancellationToken cancellationToken)
    {
        var rawKey = TempData["RawApiKey"] as string;
        var keyPrefix = TempData["KeyPrefix"] as string;
        var consumer = await _consumers.GetConsumerAsync(id, cancellationToken);
        if (consumer is null || string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(keyPrefix))
        {
            return RedirectToAction(nameof(Detail), new { id });
        }

        return View(new ApiConsumerKeyCreatedViewModel
        {
            Consumer = consumer,
            RawKey = rawKey,
            KeyPrefix = keyPrefix
        });
    }

    [HttpPost("Keys/{keyId:int}/Revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeKey(int keyId, int consumerId, CancellationToken cancellationToken)
    {
        await _consumers.RevokeKeyAsync(keyId, cancellationToken);
        return RedirectToAction(nameof(Detail), new { id = consumerId });
    }

    private static ApiConsumer ToConsumer(ApiConsumerFormViewModel model)
    {
        return new ApiConsumer
        {
            Id = model.Id,
            Name = model.Name.Trim(),
            Description = model.Description,
            OwnerEmail = model.OwnerEmail,
            IsEnabled = model.IsEnabled
        };
    }

    private static IReadOnlyList<string> ParseScopes(string scopes)
    {
        return scopes.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
