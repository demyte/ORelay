using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ORelay.Server;

public static class RelayServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory registry and expiry sweep. Hosts can map the
    /// endpoints with <see cref="RelayServerEndpoints.MapRelayEndpoints"/>.
    /// </summary>
    public static IServiceCollection AddRelayServer(
        this IServiceCollection services,
        RelayServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RegistrationStore(
            sp.GetRequiredService<TimeProvider>(),
            options.LeaseDuration,
            options.MaxRegistrations));
        services.AddHostedService<RegistrationExpiryService>();
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, RelayJsonContext.Default));
        return services;
    }

    public static IServiceCollection AddRelayServer(
        this IServiceCollection services,
        RelayServerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        services.AddSingleton(timeProvider);
        return services.AddRelayServer(options);
    }
}
