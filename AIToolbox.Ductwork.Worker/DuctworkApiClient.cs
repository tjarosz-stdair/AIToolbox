using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIToolbox.Ductwork.Contracts;
using Microsoft.Extensions.Options;

namespace AIToolbox.Ductwork.Worker;

/// <summary>The only way the worker touches the server. It has no database, no files and no provider keys of its own.</summary>
public interface IDuctworkApi
{
    Task<HandshakeInfo> HandshakeAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct);
    Task<ClaimedTask?> ClaimAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct);
    Task<HeartbeatReply> HeartbeatAsync(long taskId, int done, int? total, string? message, CancellationToken ct);
    Task<TaskInput> GetInputAsync(long taskId, CancellationToken ct);
    Task DownloadFileAsync(long taskId, Guid fileId, Stream destination, CancellationToken ct);
    Task<RegisterSheetsReply> RegisterSheetsAsync(long taskId, Guid fileId, IReadOnlyList<PageReport> pages, CancellationToken ct);
    Task<SubmitReply> SubmitResultAsync(long taskId, string manifestJson, string? resultRef, CancellationToken ct);
    Task FailAsync(long taskId, string error, bool retryable, CancellationToken ct);
}

public sealed class DuctworkApiClient : IDuctworkApi
{
    public const string ApiKeyHeader = "X-AIToolbox-Api-Key";
    private const string Base = "api/v1/ductwork/worker/";

    private readonly HttpClient _http;
    private readonly WorkerOptions _options;
    private readonly TimeSpan[] _retryDelays;

    public DuctworkApiClient(HttpClient http, IOptions<WorkerOptions> options) : this(http, options, [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8)])
    {
    }

    internal DuctworkApiClient(HttpClient http, IOptions<WorkerOptions> options, TimeSpan[] retryDelays)
    {
        _http = http;
        _options = options.Value;
        _retryDelays = retryDelays;

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.ServerUrl))
            _http.BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove(ApiKeyHeader);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) _http.DefaultRequestHeaders.Add(ApiKeyHeader, _options.ApiKey);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private string Name => _options.WorkerName;

    public async Task<HandshakeInfo> HandshakeAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, "handshake", new { workerName = Name, contractVersion = ContractInfo.Version, taskTypes }), ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var body = await ReadErrorAsync(response, ct);
            if (body.Code == "contract-version")
                throw new ContractMismatchException(body.Extra.GetValueOrDefault("serverContractVersion") ?? "?", body.Message);
        }

        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<HandshakeInfo>(WorkerJson.Options, ct))!;
    }

    public async Task<ClaimedTask?> ClaimAsync(IReadOnlyCollection<string> taskTypes, CancellationToken ct)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, "tasks/claim", new { workerName = Name, taskTypes }), ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ClaimedTask>(WorkerJson.Options, ct);
    }

    public async Task<HeartbeatReply> HeartbeatAsync(long taskId, int done, int? total, string? message, CancellationToken ct)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, $"tasks/{taskId}/heartbeat", new { workerName = Name, done, total, message }), ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<HeartbeatReply>(WorkerJson.Options, ct))!;
    }

    public async Task<TaskInput> GetInputAsync(long taskId, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Base}tasks/{taskId}/input?workerName={Uri.EscapeDataString(Name)}"), ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInput>(WorkerJson.Options, ct))!;
    }

    public async Task DownloadFileAsync(long taskId, Guid fileId, Stream destination, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Base}tasks/{taskId}/files/{fileId:D}/content?workerName={Uri.EscapeDataString(Name)}"), ct,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, ct);
        await response.Content.CopyToAsync(destination, ct);
    }

    public async Task<RegisterSheetsReply> RegisterSheetsAsync(long taskId, Guid fileId, IReadOnlyList<PageReport> pages, CancellationToken ct)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, $"tasks/{taskId}/sheets", new { workerName = Name, fileId, sheets = pages }), ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<RegisterSheetsReply>(WorkerJson.Options, ct))!;
    }

    public async Task<SubmitReply> SubmitResultAsync(long taskId, string manifestJson, string? resultRef, CancellationToken ct)
    {
        var query = $"workerName={Uri.EscapeDataString(Name)}" + (resultRef is null ? "" : $"&resultRef={Uri.EscapeDataString(resultRef)}");
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Base}tasks/{taskId}/result?{query}")
        {
            Content = new StringContent(manifestJson, Encoding.UTF8, "application/json")
        }, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SubmitReply>(WorkerJson.Options, ct))!;
    }

    public async Task FailAsync(long taskId, string error, bool retryable, CancellationToken ct)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, $"tasks/{taskId}/fail", new { workerName = Name, error, retryable }), ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ------------------------------------------------------------------ plumbing

    private static HttpRequestMessage Json(HttpMethod method, string path, object body) =>
        new(method, Base + path) { Content = JsonContent.Create(body, new MediaTypeHeaderValue("application/json"), WorkerJson.Options) };

    /// <summary>Sends a request, asking again after a pause when the server was busy or unreachable. A refusal is never retried.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await _http.SendAsync(build(), completion, ct);
                if (!IsTransient(response.StatusCode) || attempt >= _retryDelays.Length) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < _retryDelays.Length)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _retryDelays.Length)
            {
                // The request timed out; the caller did not cancel.
            }

            await Task.Delay(_retryDelays[attempt], ct);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private sealed record ErrorBody(string Code, string Message, IReadOnlyList<string> Reasons, Dictionary<string, string> Extra);

    private static async Task<ErrorBody> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try { text = await response.Content.ReadAsStringAsync(ct); }
        catch (Exception) { text = ""; }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string Str(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var reasons = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
            var extra = new Dictionary<string, string>();
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var p in root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)) extra[p.Name] = p.Value.GetString()!;
            var message = Str("error");
            return new ErrorBody(Str("code"), message.Length > 0 ? message : $"The server answered {(int)response.StatusCode}.", reasons, extra);
        }
        catch (JsonException)
        {
            return new ErrorBody("", $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.", [], []);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var error = await ReadErrorAsync(response, ct);
        if (error.Code == "result-rejected") throw new ResultRejectedApiException(error.Reasons, error.Message);
        throw new ApiException((int)response.StatusCode, error.Code, error.Message);
    }
}
