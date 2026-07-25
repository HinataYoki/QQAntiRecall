using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace QQAntiRecall.Core;

/// <summary>
/// Isolates platform, process, discovery, and clock inputs used by the patch service.
/// </summary>
internal interface IAntiRecallRuntime
{
    /// <summary>
    /// Indicates whether wrapper.node modification is supported on the current platform.
    /// </summary>
    bool IsWindows { get; }

    /// <summary>
    /// Provides the per-user root beneath which durable backups are stored.
    /// </summary>
    string LocalApplicationDataPath { get; }

    /// <summary>
    /// Supplies UTC timestamps used to order backup manifests.
    /// </summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Reports whether QQ currently owns files that must remain untouched.
    /// </summary>
    bool IsQqRunning();

    /// <summary>
    /// Requests a graceful QQ exit, then terminates remaining QQ processes after a bounded wait.
    /// </summary>
    Task<bool> StopQqAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts QQ from an executable path already verified by the patch service.
    /// </summary>
    void StartQq(string executablePath);

    /// <summary>
    /// Enumerates likely QQ roots in discovery priority order.
    /// </summary>
    IEnumerable<string> EnumerateInstallCandidates();
}

/// <summary>
/// Supplies operating-system values for production patch operations.
/// </summary>
internal sealed class SystemAntiRecallRuntime : IAntiRecallRuntime
{
    private static readonly string[] QqProcessNames = ["QQ", "QQNT"];

    /// <summary>
    /// Reports native Windows support without probing QQ files.
    /// </summary>
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// Returns the current user's local application data directory for private backups.
    /// </summary>
    public string LocalApplicationDataPath => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Returns the current UTC time for backup identity and ordering.
    /// </summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>
    /// Detects both current and legacy QQ process names without retaining process handles.
    /// </summary>
    public bool IsQqRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        List<Process> processes;
        try
        {
            processes = GetQqProcesses();
        }
        catch
        {
            // A failed process query is treated as running so file writes remain fail-closed.
            return true;
        }

        try
        {
            return processes.Count > 0;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Gives QQ a short graceful-exit window before terminating any remaining process tree.
    /// </summary>
    public async Task<bool> StopQqAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        List<Process> processes;
        try
        {
            processes = GetQqProcesses();
        }
        catch
        {
            // The caller must not continue with installation when processes cannot be enumerated.
            return false;
        }

        try
        {
            if (processes.Count == 0)
            {
                return true;
            }

            foreach (Process process in processes)
            {
                TryRequestGracefulExit(process);
            }

            if (await WaitForExitAsync(processes, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            {
                return !IsQqRunning();
            }

            foreach (Process process in processes.Where(process => !HasExited(process)))
            {
                TryTerminateProcessTree(process);
            }

            bool allExited = await WaitForExitAsync(
                processes,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            return allExited && !IsQqRunning();
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    /// <summary>
    /// Launches QQ with its installation directory as the working directory.
    /// </summary>
    public void StartQq(string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("QQ restart is only supported on Windows.");
        }

        var normalizedPath = Path.GetFullPath(executablePath);
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException("未找到用于重启的 QQ.exe。", normalizedPath);
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = normalizedPath,
            WorkingDirectory = Path.GetDirectoryName(normalizedPath)!,
            UseShellExecute = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException("系统未能启动 QQ.exe。");
        }
    }

    /// <summary>
    /// Yields registry install locations before conventional fixed-drive locations.
    /// </summary>
    public IEnumerable<string> EnumerateInstallCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var candidate in EnumerateRegistryInstallLocations())
        {
            yield return candidate;
        }

        foreach (var candidate in EnumerateFixedDriveLocations())
        {
            yield return candidate;
        }
    }

    /// <summary>
    /// Reads uninstall entries whose display name identifies QQ and which provide an install location.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateRegistryInstallLocations()
    {
        var results = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var subKey = uninstall.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName") as string;
                        var installLocation = subKey?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(displayName)
                            && displayName.Contains("QQ", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(installLocation))
                        {
                            results.Add(installLocation);
                        }
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // A protected registry view should not prevent fixed-drive discovery.
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Builds conventional QQNT paths for every ready fixed drive.
    /// </summary>
    private static IEnumerable<string> EnumerateFixedDriveLocations()
    {
        var results = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                results.Add(Path.Combine(drive.RootDirectory.FullName, "Tencent", "QQNT"));
                results.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files", "Tencent", "QQNT"));
                results.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Tencent", "QQNT"));
            }
        }
        catch (IOException)
        {
            // Discovery remains best-effort when a drive disappears during enumeration.
        }

        return results;
    }

    /// <summary>
    /// Enumerates current and legacy QQ process names and removes duplicate process identifiers.
    /// </summary>
    private static List<Process> GetQqProcesses()
    {
        var processes = new List<Process>();
        var processIds = new HashSet<int>();
        try
        {
            foreach (string processName in QqProcessNames)
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    if (processIds.Add(process.Id))
                    {
                        processes.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }
            }

            return processes;
        }
        catch
        {
            DisposeProcesses(processes);
            throw;
        }
    }

    /// <summary>
    /// Requests a normal window close when the process still exposes a main window.
    /// </summary>
    private static bool TryRequestGracefulExit(Process process)
    {
        try
        {
            return !process.HasExited && process.CloseMainWindow();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Terminates the complete QQ process tree when graceful exit did not complete in time.
    /// </summary>
    private static bool TryTerminateProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Polls process handles until every process exits or the bounded timeout elapses.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(
        IReadOnlyList<Process> processes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processes.All(HasExited))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        return processes.All(HasExited);
    }

    /// <summary>
    /// Reports process exit without allowing a stale or inaccessible handle to escape the shutdown workflow.
    /// </summary>
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Releases every process handle acquired during one detection or shutdown attempt.
    /// </summary>
    private static void DisposeProcesses(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// Replaces a destination atomically while preserving its previous bytes at a rollback path.
/// </summary>
internal interface IAtomicFileReplacer
{
    /// <summary>
    /// Atomically promotes source to destination and writes the old destination to rollbackPath.
    /// </summary>
    void Replace(string source, string destination, string rollbackPath);
}

/// <summary>
/// Uses the platform file-replacement primitive for transactional patch commits.
/// </summary>
internal sealed class SystemAtomicFileReplacer : IAtomicFileReplacer
{
    /// <summary>
    /// Atomically replaces a file on its existing volume and retains the displaced file.
    /// </summary>
    public void Replace(string source, string destination, string rollbackPath)
    {
        File.Replace(source, destination, rollbackPath, ignoreMetadataErrors: false);
    }
}
