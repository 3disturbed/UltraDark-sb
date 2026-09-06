using SexyBiscuit.Engine.Networking;

namespace Cookies.RemotePlayers;

/// <summary>
/// Finds servers announcing themselves on the local network, so a menu can list them.
/// </summary>
public sealed class LanBrowser
{
    private readonly List<LanServerInfo> _found = new();

    /// <summary>Servers seen since the last search started, newest first.</summary>
    public IReadOnlyList<LanServerInfo> Servers => _found;

    /// <summary>True between <see cref="Search"/> and its timeout.</summary>
    public bool IsSearching { get; private set; }

    /// <summary>Raised for each server as it answers, so a list can grow while the search runs.</summary>
    public event Action<LanServerInfo>? ServerFound;

    /// <summary>
    /// Broadcasts on <paramref name="port"/> and collects whatever answers within
    /// <paramref name="seconds"/>.
    /// </summary>
    public void Search(int port = 9050, float seconds = 2f)
    {
        _found.Clear();
        IsSearching = true;

        try
        {
            LanDiscovery.Discover(port, info =>
            {
                // A server that answers twice is one server.
                if (_found.Any(s => s.Address == info.Address && s.Port == info.Port)) return;

                _found.Insert(0, info);
                ServerFound?.Invoke(info);
            }, seconds);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[remote-players] LAN search failed: " + ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }
}
