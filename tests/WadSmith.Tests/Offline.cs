using System.Runtime.CompilerServices;

namespace WadSmith.Tests;

/// <summary>The tests never reach the web: the app's fetches (a game's cover picture) are off for the whole run.</summary>
internal static class Offline
{
    [ModuleInitializer]
    internal static void Set() => Environment.SetEnvironmentVariable("WADSMITH_OFFLINE", "1");
}
