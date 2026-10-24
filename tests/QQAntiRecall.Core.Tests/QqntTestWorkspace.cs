namespace QQAntiRecall.Core.Tests;

/// <summary>
/// Owns an isolated synthetic QQ installation for one QQNT.dll integrity test.
/// </summary>
internal sealed class QqntTestWorkspace : IDisposable
{
    /// <summary>
    /// Creates unique directories on the current test volume.
    /// </summary>
    internal QqntTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "QQAntiRecall.QqntTests", Guid.NewGuid().ToString("N"));
        InstallRoot = Path.Combine(Root, "QQNT");
        Runtime = new TestRuntime(Path.Combine(Root, "local-app-data"));
        Directory.CreateDirectory(InstallRoot);
    }

    /// <summary>
    /// Gets the isolated root that is deleted on dispose.
    /// </summary>
    internal string Root { get; }

    /// <summary>
    /// Gets the synthetic QQ installation root.
    /// </summary>
    internal string InstallRoot { get; }

    /// <summary>
    /// Gets the controlled runtime shared with the wrapper.node tests.
    /// </summary>
    internal TestRuntime Runtime { get; }

    /// <summary>
    /// Creates a service bound to this test's controlled runtime.
    /// </summary>
    /// <returns>The service under test.</returns>
    internal QqntIntegrityService CreateService() => new(Runtime);

    /// <summary>
    /// Creates QQ.exe plus one version directory holding a synthetic QQNT.dll.
    /// </summary>
    /// <param name="version">Version directory name.</param>
    /// <returns>The absolute DLL path and the original bytes.</returns>
    internal (string Root, string FilePath, byte[] Bytes) CreateInstaller(string version)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllBytes(Path.Combine(InstallRoot, "QQ.exe"), []);
        var bytes = QqntTestImage.Create();
        var filePath = CreateVersion(version, bytes);
        return (InstallRoot, filePath, bytes);
    }

    /// <summary>
    /// Creates one version directory containing the supplied QQNT.dll bytes.
    /// </summary>
    /// <param name="version">Version directory name.</param>
    /// <param name="bytes">Image bytes to write.</param>
    /// <returns>The absolute DLL path.</returns>
    internal string CreateVersion(string version, byte[] bytes)
    {
        var directory = Path.Combine(InstallRoot, "versions", version);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "QQNT.dll");
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    /// <summary>
    /// Removes only this test's generated unique root.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
