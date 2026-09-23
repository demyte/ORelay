using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ORelay.Aspire.Hosting;

/// <summary>A connection to a relay whose process lifetime is independent of this AppHost.</summary>
public sealed class ORelayConnection
{
    internal ORelayConnection(IDistributedApplicationBuilder builder, Uri serverUrl, RelayDashboard dashboard)
    {
        Builder = builder;
        ServerUrl = serverUrl;
        Dashboard = dashboard;
    }

    internal IDistributedApplicationBuilder Builder { get; }
    public Uri ServerUrl { get; }
    internal RelayDashboard Dashboard { get; }
}

public static class ORelayExtensions
{
    /// <summary>Shows an existing relay in Aspire without launching or stopping it.</summary>
    public static ORelayConnection AddORelay(this IDistributedApplicationBuilder builder, string name, Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateUrl(serverUrl);
        if (serverUrl.AbsolutePath != "/")
            throw new ArgumentException("The relay management URL must use the root path.", nameof(serverUrl));
        var resource = builder.AddExternalService(name, serverUrl);
        var dashboard = new RelayDashboard(resource.Resource);
        builder.Services.AddSingleton(dashboard);
        var checkName = $"orelay-{name}-registrations";
        builder.Services.AddHealthChecks().AddCheck(checkName, () => dashboard.Health);
        resource.WithHealthCheck(checkName);
        return new(builder, serverUrl, dashboard);
    }

    /// <summary>Registers an allocated HTTP endpoint and injects ORelay__RegistrationId and ORelay__RedirectUri.</summary>
    public static IResourceBuilder<T> WithORelay<T>(this IResourceBuilder<T> builder,
        ORelayConnection relay, string callbackPath, string endpointName = "http", Uri? callbackUrl = null,
        ORelayCallbackOptions? callbackOptions = null)
        where T : IResourceWithEndpoints, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(relay);
        if (!ReferenceEquals(builder.ApplicationBuilder, relay.Builder))
            throw new ArgumentException("The relay and resource must belong to the same AppHost.", nameof(relay));
        if (string.IsNullOrEmpty(callbackPath) || !callbackPath.StartsWith('/') || callbackPath.StartsWith("//", StringComparison.Ordinal)
            || callbackPath.IndexOfAny(['?', '#']) >= 0)
            throw new ArgumentException("Use an absolute callback path without a query or fragment.", nameof(callbackPath));
        if (callbackUrl is not null) ValidateUrl(callbackUrl);
        if (builder.Resource.Annotations.OfType<ORelayAnnotation>().Any())
            throw new InvalidOperationException("A resource can have only one ORelay registration.");

        var session = new RegistrationSession(new HttpClient { BaseAddress = relay.ServerUrl, Timeout = TimeSpan.FromSeconds(5) });
        session.ResourceName = builder.Resource.Name;
        relay.Dashboard.Add(builder.Resource.Name, session);
        builder.WithAnnotation(new ORelayAnnotation(session));
        builder.WithAnnotation(new ResourceRelationshipAnnotation(relay.Dashboard.Resource, "Reference"));
        relay.Builder.Services.AddSingleton<IHostedService>(_ => session);
        var checkName = $"orelay-{builder.Resource.Name}";
        relay.Builder.Services.AddHealthChecks().AddCheck(checkName, () => session.Health);
        builder.WithHealthCheck(checkName);

        Task<Uri> Destination(CancellationToken ct)
        {
            if (builder.Resource.GetReplicaCount() != 1)
                throw new InvalidOperationException("ORelay requires a single resource instance. Give each worktree its own resource and registration.");
            return CallbackDestination.ResolveAsync(callbackUrl is null ? builder.GetEndpoint(endpointName) : null,
                callbackPath, callbackUrl, callbackOptions ?? new(), ct);
        }
        void SetLogger(IServiceProvider services)
        {
            var loggers = services.GetRequiredService<ResourceLoggerService>();
            session.Logger = loggers.GetLogger(builder.Resource);
            relay.Dashboard.Connect(services);
            session.RelayLogger = loggers.GetLogger(relay.Dashboard.Resource);
        }

        builder.OnResourceEndpointsAllocated(async (_, e, ct) =>
        {
            SetLogger(e.Services);
            await session.EnsureRegisteredAsync(Destination, ct).ConfigureAwait(false);
        });
        builder.OnBeforeResourceStarted(async (_, e, ct) =>
        {
            SetLogger(e.Services);
            await session.EnsureRegisteredAsync(Destination, ct).ConfigureAwait(false);
        });
        builder.WithEnvironment(async context =>
        {
            var registration = await session.EnsureRegisteredAsync(Destination, context.CancellationToken).ConfigureAwait(false);
            context.EnvironmentVariables["ORelay__RegistrationId"] = registration.Id;
            context.EnvironmentVariables["ORelay__RedirectUri"] = registration.RelayCallbackUrl;
        });
        builder.OnResourceStopped((_, _, _) => session.StopRegistrationAsync());
        return builder;
    }

    private static void ValidateUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0
            || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute HTTP(S) URL without credentials, query or fragment.", nameof(url));
    }

    private sealed record ORelayAnnotation(RegistrationSession Session) : IResourceAnnotation;
}
