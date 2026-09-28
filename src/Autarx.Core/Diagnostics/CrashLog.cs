namespace Autarx.Core.Diagnostics;

/// <summary>
/// Local crash log: one file per unhandled exception under
/// &lt;TEMP&gt;/autarx/crashes/. Purely local — nothing is sent anywhere;
/// the user can attach a log when reporting an issue.
/// </summary>
public static class CrashLog
{
    public static string? Write(string source, Exception exception)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "autarx", "crashes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"autarx-{source}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"""
                autarx {source} crash — {DateTime.UtcNow:u}

                {exception.GetType().FullName}: {exception.Message}

                {exception.StackTrace}
                """);
            return path;
        }
        catch (Exception)
        {
            // a failing crash logger must never take the process down harder
            return null;
        }
    }
}
