using Microsoft.Extensions.Logging;

namespace ORelay.Aspire.Hosting;

internal static partial class SessionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ORelay registered the allocated callback before application startup.")]
    internal static partial void Registered(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "ORelay renewed the callback registration lease for {ResourceName}.")]
    internal static partial void Renewed(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Information, Message = "ORelay registered the allocated callback for {ResourceName}.")]
    internal static partial void RelayRegistered(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay renewal disconnected for {ResourceName}. Retrying until lease expiry.")]
    internal static partial void RelayDisconnected(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Error, Message = "ORelay registration lost for {ResourceName}. Explicit restart is required.")]
    internal static partial void RelayLost(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Information, Message = "ORelay registration stopped for {ResourceName}.")]
    internal static partial void RelayStopped(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay cleanup failed for {ResourceName}. The registration will expire.")]
    internal static partial void RelayCleanupFailed(ILogger logger, string resourceName);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay could not publish registration details to the Aspire dashboard.")]
    internal static partial void DashboardFailed(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay renewal disconnected. Retrying the existing registration until lease expiry.")]
    internal static partial void Disconnected(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "ORelay registration lost. Explicitly restart this resource or AppHost before beginning another authorization flow. Pending flows cannot be recovered.")]
    internal static partial void Lost(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay cleanup failed. The stopped registration will expire.")]
    internal static partial void CleanupFailed(ILogger logger);
}
