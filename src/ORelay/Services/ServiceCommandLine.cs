using System.Text;

namespace ORelay.Services;

/// <summary>Builds the command lines persisted by the two service managers.</summary>
public static class ServiceCommandLine
{
    public static IReadOnlyList<string> BuildArguments(ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = new List<string>
        {
            "server",
            "--config-file",
            request.ConfigurationPath,
        };

        // A custom installation must carry its identity into the server
        // invocation so UseWindowsService can register the matching SCM name.
        if (!string.Equals(request.ServiceName, ServiceIdentity.DefaultName, StringComparison.Ordinal))
        {
            arguments.Add("--service-name");
            arguments.Add(request.ServiceName);
        }

        return arguments;
    }

    /// <summary>
    /// Builds a Windows SCM binary path. SCM stores one command-line string, so
    /// every argument is quoted using the CommandLineToArgvW rules.
    /// </summary>
    public static string BuildWindows(ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Join(
            ' ',
            new[] { QuoteWindowsArgument(request.ExecutablePath) }
                .Concat(BuildArguments(request).Select(QuoteWindowsArgument)));
    }

    /// <summary>Quotes one Windows argument without involving a shell.</summary>
    public static string QuoteWindowsArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes != 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Quotes a systemd ExecStart argument. systemd parses this format itself,
    /// so it needs systemd escapes rather than Windows or shell quoting.
    /// </summary>
    public static string QuoteSystemdArgument(string value)
        => QuoteSystemdValue(value, escapeDollar: true);

    /// <summary>
    /// Quotes a filesystem path used by a systemd directive such as
    /// <c>WorkingDirectory</c>. Percent specifiers still need escaping, but
    /// systemd does not perform ExecStart-style environment expansion here;
    /// preserve a literal dollar sign in the path.
    /// </summary>
    public static string QuoteSystemdPath(string value)
        => QuoteSystemdValue(value, escapeDollar: false);

    private static string QuoteSystemdValue(string value, bool escapeDollar)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '%':
                    builder.Append("%%");
                    break;
                case '$':
                    // systemd expands environment variables in ExecStart. A
                    // doubled dollar keeps a literal path character.
                    builder.Append(escapeDollar ? "$$" : "$");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    public static string BuildSystemdExecStart(ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Join(
            ' ',
            new[] { QuoteSystemdArgument(request.ExecutablePath) }
                .Concat(BuildArguments(request).Select(QuoteSystemdArgument)));
    }
}
