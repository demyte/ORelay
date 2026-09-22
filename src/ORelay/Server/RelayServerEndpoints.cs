using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ORelay.Server;

/// <summary>
/// Maps the public management and browser callback contracts.
///
/// Management access is intentionally open in this first shared-network
/// increment. The route ID remains an opaque routing identifier and is not
/// described as a credential. A later access policy can add authentication at
/// this boundary without changing the registry or callback contract.
/// </summary>
public static class RelayServerEndpoints
{
    public static IEndpointRouteBuilder MapRelayEndpoints(
        this IEndpointRouteBuilder endpoints,
        RegistrationStore registrations,
        RelayServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        endpoints.MapPost("/registrations", CreateRegistration);

        endpoints.MapPut("/registrations/{id}/lease", RenewRegistration);

        endpoints.MapDelete("/registrations/{id}", DeleteRegistration);

        endpoints.MapGet(options.CallbackRoutePath, RouteCallback);

        endpoints.MapGet("/health", Health);

        return endpoints;
    }

    public static IResult CreateRegistration(
        CreateRegistrationRequest? request,
        HttpContext context,
        RegistrationStore registrations,
        RelayServerOptions options)
    {
        if (request?.CallbackUrl is null)
        {
            return Error(context, StatusCodes.Status400BadRequest, "invalid_request", "callbackUrl is required.");
        }

        var result = registrations.Register(
            request.CallbackUrl,
            options.AllowsNonLoopbackDestinations,
            options.RelayCallbackUrl);
        if (result.IsSuccess)
        {
            return Json(StatusCodes.Status201Created, result.Response!, RelayJsonContext.Default.RegistrationResponse);
        }

        return result.ErrorCode == "capacity_exceeded"
            ? Error(context, StatusCodes.Status503ServiceUnavailable, result.ErrorCode, "The registration capacity has been reached.")
            : Error(context, StatusCodes.Status400BadRequest, result.ErrorCode ?? "invalid_request", "The callback URL is not allowed.");
    }

    public static IResult RenewRegistration(
        string? id,
        HttpContext context,
        RegistrationStore registrations,
        RelayServerOptions options)
    {
        var result = registrations.Renew(id ?? string.Empty, options.RelayCallbackUrl);
        if (!result.IsSuccess)
        {
            return Error(context, StatusCodes.Status404NotFound, "registration_not_found", "The registration is unknown or expired.");
        }

        return Json(StatusCodes.Status200OK, result.Response!, RelayJsonContext.Default.RegistrationResponse);
    }

    public static IResult DeleteRegistration(
        string? id,
        HttpContext context,
        RegistrationStore registrations)
    {
        // DELETE is deliberately idempotent. It does not reveal whether a
        // previous registration existed or had already expired.
        registrations.Delete(id ?? string.Empty);
        SetNoCacheHeaders(context.Response);
        return Results.NoContent();
    }

    public static IResult RouteCallback(HttpContext context, RegistrationStore registrations)
    {
        string? state;
        try
        {
            var values = context.Request.Query["state"];
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
            {
                return Error(context, StatusCodes.Status400BadRequest, "invalid_routing_state", "The callback routing state is invalid.");
            }

            state = values[0];
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or BadHttpRequestException)
        {
            _ = exception;
            return Error(context, StatusCodes.Status400BadRequest, "invalid_routing_state", "The callback routing state is invalid.");
        }

        if (!RegistrationStore.TryExtractRegistrationId(state, out var id))
        {
            return Error(context, StatusCodes.Status400BadRequest, "invalid_routing_state", "The callback routing state is invalid.");
        }

        if (!registrations.TryGet(id, out var registration))
        {
            return Error(context, StatusCodes.Status404NotFound, "registration_not_found", "The callback registration is unknown or expired.");
        }

        // QueryString.Value is the raw request-target query. Keep it attached
        // verbatim so encoded values, repeated fields, and empty values reach
        // the worktree exactly as the provider sent them.
        var rawQuery = context.Request.QueryString.Value;
        if (string.IsNullOrEmpty(rawQuery))
        {
            return Error(context, StatusCodes.Status400BadRequest, "invalid_callback", "The callback query is missing.");
        }

        var location = registration.CallbackUrl + rawQuery;
        SetNoCacheHeaders(context.Response);
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = location;
        return Results.Empty;
    }

    public static SourceGeneratedJsonResult<RelayHealthResponse> Health(HttpContext context)
    {
        SetNoCacheHeaders(context.Response);
        return Json(StatusCodes.Status200OK, new RelayHealthResponse("orelay", "ok"), RelayJsonContext.Default.RelayHealthResponse);
    }

    private static SourceGeneratedJsonResult<RelayErrorResponse> Error(HttpContext context, int statusCode, string code, string message)
    {
        SetNoCacheHeaders(context.Response);
        return Json(statusCode, new RelayErrorResponse(code, message), RelayJsonContext.Default.RelayErrorResponse);
    }

    private static SourceGeneratedJsonResult<T> Json<T>(int statusCode, T value, JsonTypeInfo<T> typeInfo) =>
        new SourceGeneratedJsonResult<T>(statusCode, value, typeInfo);

    private static void SetNoCacheHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    public sealed class SourceGeneratedJsonResult<T>(int statusCode, T value, JsonTypeInfo<T> typeInfo) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            SetNoCacheHeaders(httpContext.Response);
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, value, typeInfo, httpContext.RequestAborted);
        }
    }
}

public sealed record RelayHealthResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("identity")] string Identity,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status);
