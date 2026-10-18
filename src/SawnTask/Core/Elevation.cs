using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace SawnTask.Core;

// "Restart as administrator": start a new copy of SawnTask through the UAC prompt
// (the "runas" verb) and let the caller close this one.
public static class Elevation
{
    const int ERROR_CANCELLED = 1223; // the user pressed No on the UAC prompt

    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // declined = the user pressed No on the UAC prompt (not an error, just a choice).
    public static bool Restart(string arguments, out bool declined, out string error)
    {
        declined = false;
        error = "";
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED)
        {
            declined = true;
            return false;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    // Command-line arguments break on a trailing backslash before a quote ("C:\" becomes
    // C:" ), so drive roots travel as "C:" and get their backslash back on arrival.
    public static string QuoteForArgs(string path) => "\"" + path.TrimEnd('\\') + "\"";

    public static string FromArgs(string arg) => arg.Length == 2 && arg[1] == ':' ? arg + "\\" : arg;
}
