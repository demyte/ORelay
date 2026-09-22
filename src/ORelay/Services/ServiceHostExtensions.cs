using Microsoft.Extensions.Hosting;

namespace ORelay.Services;

/// <summary>
/// Enables the official Generic Host lifetime for an installed service. The
/// foreground CLI keeps the normal console lifetime because these extensions
/// activate only when the process is running under the platform manager.
/// </summary>
public static class ServiceHostExtensions
{
    public static IHostBuilder UseORelayServiceLifetime(this IHostBuilder builder, string? serviceName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var effectiveServiceName = string.IsNullOrWhiteSpace(serviceName)
            ? ServiceIdentity.DefaultName
            : serviceName.Trim();
        if (!ServiceIdentity.IsValidName(effectiveServiceName))
        {
            throw new ArgumentException(
                "The service name may contain only letters, numbers, '.', '-' and '_'.",
                nameof(serviceName));
        }

        if (OperatingSystem.IsWindows())
        {
            return builder.UseWindowsService(options => options.ServiceName = effectiveServiceName);
        }

        if (OperatingSystem.IsLinux())
        {
            return builder.UseSystemd();
        }

        return builder;
    }
}
