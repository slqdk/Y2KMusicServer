using System.Threading;

namespace Y2KMusicServer.Tray;

internal static class SingleInstance
{
    private static Mutex? _mutex;

    public static bool AcquireOrFocusExisting(string name)
    {
        _mutex = new Mutex(initiallyOwned: true,
                           name: "Global\\" + name,
                           createdNew: out var createdNew);
        return createdNew;
    }
}
