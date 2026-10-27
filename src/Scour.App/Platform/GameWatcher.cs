using System.Diagnostics;
using System.IO;
using Scour.Core.Gaming;

namespace Scour.App.Platform;

/// <summary>
/// Notices when one of the installed games starts or exits (checked every few seconds). Events are raised on
/// the thread that created the watcher (the UI thread).
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LibraryRefresh = TimeSpan.FromMinutes(30);

    private readonly Func<List<GameInfo>> _findGames;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Dictionary<int, string?> _paths = new();
    private Timer? _timer;
    private List<GameInfo> _games = [];
    private DateTime _gamesFoundUtc;
    private GameInfo? _running;
    private int _misses;
    private int _busy;

    public GameWatcher(Func<List<GameInfo>> findGames) => _findGames = findGames;

    public event Action<GameInfo>? GameStarted;
    public event Action<GameInfo>? GameStopped;

    public GameInfo? Running => _running;
    public bool IsRunning => _timer is not null;

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _running = null;
        _misses = 0;
    }

    /// <summary>Re-reads the game library on the next check (after games were added or removed).</summary>
    public void Invalidate() => _gamesFoundUtc = DateTime.MinValue;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow - _gamesFoundUtc > LibraryRefresh)
            {
                _games = _findGames();
                _gamesFoundUtc = DateTime.UtcNow;
            }
            var found = FindRunningGame();
            if (found is not null)
            {
                _misses = 0;
                if (_running?.Id == found.Id) return;
                var previous = _running;
                _running = found;
                if (previous is not null) Raise(GameStopped, previous);
                Raise(GameStarted, found);
            }
            else if (_running is not null && ++_misses >= 2) // two checks in a row: not just a restart of the game's exe
            {
                var stopped = _running;
                _running = null;
                _misses = 0;
                Raise(GameStopped, stopped);
            }
        }
        catch (Exception e)
        {
            Services.ErrorLog.Write("Watching for games", e);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private GameInfo? FindRunningGame()
    {
        if (_games.Count == 0) return null;
        var processes = Process.GetProcesses();
        try
        {
            var alive = new HashSet<int>();
            GameInfo? match = null;
            foreach (var p in processes)
            {
                alive.Add(p.Id);
                if (!_paths.TryGetValue(p.Id, out var path))
                {
                    path = ProcessRunner.GetProcessPath((uint)p.Id);
                    _paths[p.Id] = path;
                }
                match ??= GameLibrary.GameFor(path, _games);
            }
            foreach (var pid in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(pid);
            return match;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private void Raise(Action<GameInfo>? handler, GameInfo game)
    {
        if (handler is null) return;
        if (_context is null) handler(game);
        else _context.Post(_ => handler(game), null);
    }

    public void Dispose() => Stop();
}
