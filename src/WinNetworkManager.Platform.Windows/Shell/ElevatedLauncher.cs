using System.ComponentModel;
using System.Diagnostics;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Platform.Windows.Shell;

/// <summary>Outcome of an elevation request.</summary>
public enum ElevationRequestResult : byte
{
    /// <summary>The process was started elevated.</summary>
    Started,

    /// <summary>The user declined the UAC prompt.</summary>
    Cancelled,

    /// <summary>The process could not be started.</summary>
    Failed,
}

/// <summary>Starts a process through the UAC consent prompt ("runas").</summary>
public static class ElevatedLauncher
{
    /// <summary>Launches <paramref name="executable"/> elevated with <paramref name="arguments"/>.</summary>
    /// <param name="executable">Full path of the executable (must be inside the application directory).</param>
    /// <param name="arguments">Arguments; each element is quoted as needed.</param>
    /// <param name="process">The started process (owns a handle; dispose it).</param>
    /// <param name="error">Error description when the result is <see cref="ElevationRequestResult.Failed"/>.</param>
    public static ElevationRequestResult TryLaunch(string executable, IReadOnlyList<string> arguments, out Process? process, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        process = null;
        error = null;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            Arguments = string.Join(' ', arguments.Select(Quote)),
        };

        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                error = "The process could not be started.";
                return ElevationRequestResult.Failed;
            }

            return ElevationRequestResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == (int)Win32Error.ERROR_CANCELLED)
        {
            return ElevationRequestResult.Cancelled;
        }
        catch (Win32Exception ex)
        {
            error = ErrorText.Describe(ex);
            return ElevationRequestResult.Failed;
        }
    }

    /// <summary>Quotes an argument following the CommandLineToArgvW rules.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
