using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
// Request logging can expose OAuth query strings. The sample emits only explicit, sanitized results.
builder.Logging.ClearProviders();
var app = builder.Build();
var id = builder.Configuration["ORelay:RegistrationId"] ?? throw new InvalidOperationException("ORelay registration was not supplied before startup.");
var redirectUri = builder.Configuration["ORelay:RedirectUri"] ?? throw new InvalidOperationException("ORelay redirect URI is missing.");
var provider = new Uri(builder.Configuration["ProviderUrl"] ?? throw new InvalidOperationException("Provider URL is missing."));
var pending = new ConcurrentDictionary<string, (string State, DateTimeOffset Expires)>();
using var http = new HttpClient { BaseAddress = provider };
var startedAt = DateTimeOffset.UtcNow;

app.MapGet("/health", () => Results.Ok());
app.MapGet("/sample/session", () => Results.Json(new { registrationId = id, redirectUri, startedAt, processId = Environment.ProcessId }));
app.MapGet("/login", (HttpContext context) =>
{
    var browserKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var state = id + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    pending[browserKey] = (state, DateTimeOffset.UtcNow.AddMinutes(5));
    context.Response.Cookies.Append(FlowCookie(state), browserKey, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, MaxAge = TimeSpan.FromMinutes(5) });
    return Results.Redirect(QueryHelpers.AddQueryString(new Uri(provider, "/authorize").AbsoluteUri,
        new Dictionary<string, string?> { ["state"] = state, ["redirect_uri"] = redirectUri, ["response_type"] = "code" }));
});
app.MapGet("/oauth/callback", async (HttpContext context) =>
{
    var query = context.Request.Query;
    if (query["state"].Count != 1) return Results.BadRequest(new { error = "invalid_state" });
    var cookieName = FlowCookie(query["state"].ToString());
    var browserKey = context.Request.Cookies[cookieName];
    if (browserKey is null || !pending.TryRemove(browserKey, out var flow)
        || flow.Expires <= DateTimeOffset.UtcNow || query["state"].Count != 1
        || !string.Equals(query["state"], flow.State, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "invalid_state" });
    context.Response.Cookies.Delete(cookieName);
    if (query.ContainsKey("error")) return Results.BadRequest(new { error = "provider_denied" });
    if (query["code"].Count != 1) return Results.BadRequest(new { error = "missing_code" });
    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = query["code"].ToString(),
        ["redirect_uri"] = redirectUri,
    });
    using var response = await http.PostAsync("/token", form, context.RequestAborted);
    if (!response.IsSuccessStatusCode) return Results.BadRequest(new { error = "exchange_failed" });
    var token = await response.Content.ReadFromJsonAsync<TokenReply>(context.RequestAborted);
    return token?.AccessToken is not null
        ? Results.Ok(new { stateValidated = true, directCodeExchange = true, redirectUri, registrationId = id })
        : Results.BadRequest(new { error = "invalid_token_response" });
});
await app.RunAsync();

static string FlowCookie(string state) => "orelay-flow-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state)))[..24];

internal sealed record TokenReply([property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken);
