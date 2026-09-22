using Microsoft.Extensions.Logging;

namespace ORelay.Aspire.Hosting;

internal static partial class SessionLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "ORelay registered the allocated callback before application startup.")]
    internal static partial void Registered(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay renewal disconnected. Retrying the existing registration until lease expiry.")]
    internal static partial void Disconnected(ILogger logger);
    [LoggerMessage(Level = LogLevel.Error, Message = "ORelay registration lost. Explicitly restart this resource or AppHost before beginning another authorization flow. Pending flows cannot be recovered.")]
    internal static partial void Lost(ILogger logger);
    [LoggerMessage(Level = LogLevel.Warning, Message = "ORelay cleanup failed. The stopped registration will expire.")]
    internal static partial void CleanupFailed(ILogger logger);
}
