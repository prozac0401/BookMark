namespace WorkBookmark.App;

/// <summary>MSI asks this user's existing instance to release files; never terminates unrelated processes.</summary>
internal static class InstallerLifetime
{
    internal static int RequestShutdown(string instanceName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(instanceName, out var running)) return 0;
            using (running)
            {
                if (EventWaitHandle.TryOpenExisting(instanceName + "_Shutdown", out var signal))
                    using (signal) signal.Set();
                bool acquired;
                try { acquired = running.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 1;
                running.ReleaseMutex();
                return 0;
            }
        }
        catch (UnauthorizedAccessException) { return 1; }
        catch (IOException) { return 1; }
    }
}
