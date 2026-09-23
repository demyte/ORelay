using Microsoft.Extensions.Logging;

namespace ORelay.Server;

internal static partial class RelayServerLog
{
    internal const string Category = "ORelay";

    [LoggerMessage(1, LogLevel.Information, "Listening on {Address}; provider callback {CallbackUrl}")]
    internal static partial void Listening(ILogger logger, string address, string callbackUrl);

    [LoggerMessage(2, LogLevel.Information, "Registered worktree {Registration} -> {Destination} (lease {LeaseSeconds}s)")]
    internal static partial void Registered(ILogger logger, string registration, string destination, int leaseSeconds);

    [LoggerMessage(3, LogLevel.Information, "Callback redirect issued for {Registration} -> {Destination}")]
    internal static partial void Redirected(ILogger logger, string registration, string destination);

    [LoggerMessage(4, LogLevel.Information, "Removed worktree {Registration}")]
    internal static partial void Removed(ILogger logger, string registration);

    [LoggerMessage(5, LogLevel.Warning, "Request rejected: {ErrorCode} (HTTP {StatusCode})")]
    internal static partial void Rejected(ILogger logger, string errorCode, int statusCode);

    [LoggerMessage(6, LogLevel.Information, "Stopping relay")]
    internal static partial void Stopping(ILogger logger);

    [LoggerMessage(7, LogLevel.Information, "Configuration changes applied")]
    internal static partial void ConfigurationApplied(ILogger logger);

    [LoggerMessage(8, LogLevel.Warning, "Could not bind the requested listener; restoring the previous listener and keeping the previous configuration")]
    internal static partial void ListenerRejected(ILogger logger);

    [LoggerMessage(9, LogLevel.Critical, "Could not restore the previous listener; correct the configuration or restart the relay")]
    internal static partial void ListenerRestoreFailed(ILogger logger);

    [LoggerMessage(10, LogLevel.Information, "ORelay {Version} started in {Mode} mode; automatic update scheduling {UpdateScheduling}.")]
    internal static partial void Started(ILogger logger, string version, string mode, string updateScheduling);

    // Destination paths can contain caller-supplied secrets. Only show the
    // validated origin and a short reference to a server-generated ID.
    internal static string DestinationOrigin(string callbackUrl) =>
        new Uri(callbackUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
}
