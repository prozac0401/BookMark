using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WorkBookmark.App;
using WorkBookmark.Core;

internal static class ProductionWorkerStartupChecks
{
    internal static async Task<int> RunAsync(string executable, string output)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "WorkBookmark.exe")
            throw new ArgumentException("An existing WorkBookmark.exe is required.", nameof(executable));
        var checks = new List<object>();
        int passed = 0;
        for (int iteration = 1; iteration <= 3; iteration++)
        {
            foreach (Operation operation in new[] { Operation.Capture, Operation.ValidateRelink })
            {
                // Neither path opens, navigates, reads or writes a user document:
                // capture uses an invalid HWND and our own ordinary process token;
                // relink validation refuses web targets before any Shell action.
                var request = new WorkerRequest(1, Guid.NewGuid(), operation, DateTimeOffset.UtcNow.AddSeconds(3),
                    new TargetSnapshot(-1, checked((uint)Environment.ProcessId)),
                    operation == Operation.ValidateRelink
                        ? new CapturedTarget(TargetKind.WebPage, "https://example.invalid/workbookmark-startup-probe", PageTitle: "Synthetic startup probe")
                        : null);
                using var client = new WorkerClient(executable);
                var startedUtc = DateTimeOffset.UtcNow;
                var watch = Stopwatch.StartNew();
                WorkerResponse response = await client.RunAsync(request);
                bool success = response.Code == ResultCode.UnsupportedTarget && response.RequestId == request.RequestId &&
                    response.Target is null && !response.ExternalActionStarted;
                if (success) passed++;
                checks.Add(new { iteration, operation = operation.ToString(), startedUtc, milliseconds = watch.ElapsedMilliseconds,
                    deadlineUtc = request.DeadlineUtc, code = response.Code.ToString(), success, response.ExternalActionStarted });
                Console.WriteLine($"{(success ? "PASS" : "FAIL")} production {operation} {iteration}: {response.Code}, {watch.ElapsedMilliseconds} ms");
            }
        }
        string core = Path.Combine(Path.GetDirectoryName(executable)!, "WorkBookmark.Core.dll");
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            executable, executableSha256 = Hash(executable), coreSha256 = File.Exists(core) ? Hash(core) : null,
            deadlineSeconds = 3, externalUserAppsAccessed = false, passed, total = checks.Count, checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{passed}/{checks.Count} production worker startup checks passed; unsupported synthetic requests only.");
        return passed == checks.Count ? 0 : 1;
    }
}
