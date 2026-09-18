using WorkBookmark.Core;

namespace WorkBookmark.App;

public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static string? directory;
    public static void Initialize(string dataDirectory) => directory = dataDirectory;
    public static void Write(string stage, ResultCode code, long elapsedMilliseconds = 0)
    {
        if (directory is null) return;
        // Stage is a program-owned token. Exception text and document metadata are never logged.
        if (stage.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) stage = "Operation";
        lock (Gate)
        {
            try
            {
                string current = Path.Combine(directory, "diagnostic.log");
                if (File.Exists(current) && new FileInfo(current).Length >= 1024 * 1024)
                {
                    string first = Path.Combine(directory, "diagnostic.1.log");
                    string second = Path.Combine(directory, "diagnostic.2.log");
                    if (File.Exists(first)) File.Move(first, second, true);
                    File.Move(current, first, true);
                }
                File.AppendAllText(current, $"{DateTimeOffset.UtcNow:O} stage={stage} code={code} elapsed_ms={elapsedMilliseconds} app=0.1.0 os={Environment.OSVersion.Version}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
