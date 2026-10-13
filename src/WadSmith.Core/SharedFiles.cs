namespace WadSmith.Core;

/// <summary>
/// Reads and replaces files that another program may be looking at. On Windows a virus scanner or the search indexer
/// opens a file the moment it is written, and for that moment a plain read or a rename over it fails with a sharing
/// violation; these wait the moment out (about a second and a half at most) instead of failing the read or the save.
/// </summary>
public static class SharedFiles
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int AccessDenied = unchecked((int)0x80070005);
    // Elsewhere .NET keeps FileShare.None with flock, and a file locked that way is refused with EWOULDBLOCK.
    private static readonly int WouldBlock = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35 : 11;
    private static readonly int[] Waits = [25, 50, 100, 200, 400, 800];

    /// <summary>The whole file, read without stopping anyone else from reading, writing or replacing it.</summary>
    public static byte[] ReadAllBytes(string path) => Patiently(() =>
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var data = new byte[f.Length];
        f.ReadExactly(data);
        return data;
    }, renaming: false);

    /// <summary>The whole file as text (UTF-8 unless it starts with another encoding's mark), read as <see cref="ReadAllBytes"/> reads.</summary>
    public static string ReadAllText(string path) => Patiently(() =>
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var r = new StreamReader(f);
        return r.ReadToEnd();
    }, renaming: false);

    /// <summary>Moves <paramref name="from"/> over <paramref name="to"/> in one step.</summary>
    public static void Replace(string from, string to) => Patiently(() =>
    {
        File.Move(from, to, overwrite: true);
        return 0;
    }, renaming: true);

    private static T Patiently<T>(Func<T> act, bool renaming)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return act();
            }
            catch (Exception ex) when (attempt < Waits.Length && Busy(ex, renaming))
            {
                Thread.Sleep(Waits[attempt]);
            }
        }
    }

    // Windows reports a rename over a file another program holds open as access denied, not as a sharing violation.
    private static bool Busy(Exception ex, bool renaming) =>
        ex is IOException { HResult: SharingViolation or LockViolation } || (!OperatingSystem.IsWindows() && ex is IOException { HResult: var errno } && errno == WouldBlock) || (renaming && OperatingSystem.IsWindows() && ex is UnauthorizedAccessException { HResult: AccessDenied });
}
