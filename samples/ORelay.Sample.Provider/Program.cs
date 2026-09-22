using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var fixedRedirectUri = builder.Configuration["RelayRedirectUri"] ?? throw new InvalidOperationException("Set the provider's fixed relay redirect URI.");
var codes = new ConcurrentDictionary<string, DateTimeOffset>();
app.MapGet("/health", () => Results.Ok());
app.MapGet("/authorize", (HttpRequest request) =>
{
    if (request.Query["redirect_uri"].Count != 1 || request.Query["redirect_uri"] != fixedRedirectUri
        || request.Query["state"].Count != 1 || request.Query["response_type"] != "code")
        return Results.BadRequest(new { error = "invalid_authorization_request" });
    var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    codes[code] = DateTimeOffset.UtcNow.AddMinutes(1);
    return Results.Redirect(QueryHelpers.AddQueryString(fixedRedirectUri,
        new Dictionary<string, string?> { ["state"] = request.Query["state"], ["code"] = code }));
});
app.MapPost("/token", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    if (form["grant_type"] != "authorization_code" || form["redirect_uri"] != fixedRedirectUri
        || form["code"].Count != 1 || !codes.TryRemove(form["code"].ToString(), out var expires) || expires <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "invalid_grant" });
    return Results.Json(new { access_token = "synthetic-sample-token", token_type = "Bearer" });
});
await app.RunAsync();
