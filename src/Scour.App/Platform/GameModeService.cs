using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Scour.Core.Gaming;
using Scour.Core.Platform;

namespace Scour.App.Platform;

/// <summary>Turns Game mode on and off: the registry boosts via the engine, power plans and process priorities here.</summary>
public sealed class GameModeService(GameMode engine, IRegistry registry)
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    /// <summary>The "Best performance" power mode (the slider in Settings → Power).</summary>
    public static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static GameModeService Create(string dataDir, IRegistry registry, IFileSystem fs) =>
        new(new GameMode(registry, fs, Path.Combine(dataDir, "game-mode.json")), registry);

    public GameModeState? State => engine.Load();
    public bool IsOn => State is not null;

    /// <summary>Turns Game mode on with the chosen boosts. Returns one line per boost describing what happened.</summary>
    public List<string> Enable(IReadOnlyCollection<string> boostIds, string? forGameId = null)
    {
        var boosts = GameMode.All.Where(b => boostIds.Contains(b.Id)).ToList();
        var state = engine.Load() ?? new GameModeState { StartedUtc = DateTime.UtcNow, StartedForGame = forGameId };
        state.Boosts = boosts.Select(b => b.Id).Union(state.Boosts).ToList();
        var log = new List<string>();

        if (boosts.Any(b => b.Kind == BoostKind.PowerPlan))
        {
            try { log.Add(SwitchPowerPlan(state)); }
            catch (Exception e) { log.Add("Power plan: " + e.Message); }
        }
        engine.Save(state);

        try
        {
            engine.ApplyRegistry(state, boosts);
            if (GameMode.ChangesMouse(state.Boosts)) ApplyMouseSettings();
            if (boosts.Any(b => b.Kind == BoostKind.Registry)) BroadcastSettingsChange();
        }
        catch (Exception e) { log.Add("Settings: " + e.Message); }

        if (boosts.Any(b => b.Kind == BoostKind.BackgroundPriority))
        {
            int n = LowerBackgroundApps(state);
            log.Add(n == 0 ? "No background apps to slow down right now." : $"Lowered the priority of {n} background app(s).");
        }
        engine.Save(state);
        return log;
    }

    /// <summary>Turns Game mode off, restoring everything it changed. Returns problems, if any.</summary>
    public List<string> Disable()
    {
        var state = engine.Load();
        if (state is null) return [];
        var errors = new List<string>();

        try { RestorePowerPlan(state); }
        catch (Exception e) { errors.Add("Power plan: " + e.Message); }

        errors.AddRange(engine.RestoreRegistry(state));
        try
        {
            if (GameMode.ChangesMouse(state.Boosts)) ApplyMouseSettingsFromRegistry();
            BroadcastSettingsChange();
        }
        catch (Exception e) { errors.Add("Settings: " + e.Message); }

        RestorePriorities(state);
        if (errors.Count == 0) engine.Clear();
        else
        {
            // Keep the state so "Turn off" can be tried again, but only with what still needs restoring.
            state.Lowered.Clear();
            state.PreviousPowerScheme = state.PreviousPowerOverlay = null;
            engine.Save(state);
        }
        return errors;
    }

    // ---------------------------------------------------------------- power

    private static string SwitchPowerPlan(GameModeState state)
    {
        var active = ActiveScheme();
        var schemes = Schemes();
        var target = schemes.Contains(UltimatePerformance) ? UltimatePerformance : schemes.Contains(HighPerformance) ? HighPerformance : (Guid?)null;

        // Laptops with Modern Standby only have Balanced; there the power mode slider is what counts.
        if (target is null)
        {
            if (active != Balanced && schemes.Contains(Balanced))
            {
                state.PreviousPowerScheme ??= active;
                Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, Balanced));
            }
            state.PreviousPowerOverlay ??= CurrentOverlay() ?? Guid.Empty;
            Check(NativeMethods.PowerSetActiveOverlayScheme(BestPerformanceOverlay));
            return "Power mode set to Best performance.";
        }
        if (active == target) return "Already on the " + Name(target.Value) + " power plan.";
        state.PreviousPowerScheme ??= active;
        Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, target.Value));
        return "Switched to the " + Name(target.Value) + " power plan.";
    }

    private static void RestorePowerPlan(GameModeState state)
    {
        if (state.PreviousPowerScheme is { } scheme && Schemes().Contains(scheme))
            Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, scheme));
        if (state.PreviousPowerOverlay is { } overlay && OverlaySupported())
            Check(NativeMethods.PowerSetActiveOverlayScheme(overlay));
    }

    private static string Name(Guid scheme) => scheme == UltimatePerformance ? "Ultimate performance" : "High performance";

    private static Guid? ActiveScheme()
    {
        if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { NativeMethods.LocalFree(ptr); }
    }

    private static HashSet<Guid> Schemes()
    {
        var result = new HashSet<Guid>();
        var buffer = new byte[16];
        for (uint i = 0; ; i++)
        {
            uint size = (uint)buffer.Length;
            if (NativeMethods.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.ACCESS_SCHEME, i, buffer, ref size) != 0) break;
            result.Add(new Guid(buffer));
        }
        return result;
    }

    private static Guid? CurrentOverlay()
    {
        try { return NativeMethods.PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static bool OverlaySupported() => CurrentOverlay() is not null;

    private static void Check(uint error)
    {
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
    }

    // ---------------------------------------------------------------- priorities

    private static int LowerBackgroundApps(GameModeState state)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcesses();
        try
        {
            var candidates = processes.Select(p =>
            {
                try { return new ProcessCandidate(p.Id, p.ProcessName, p.SessionId, p.PriorityClass == ProcessPriorityClass.Normal); }
                catch { return null; } // protected or already gone
            }).OfType<ProcessCandidate>();
            int n = 0;
            foreach (var c in GameMode.ToLower(candidates, session))
            {
                var p = processes.First(x => x.Id == c.Pid);
                try
                {
                    var started = p.StartTime.ToUniversalTime();
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    state.Lowered.Add(new LoweredProcess(c.Pid, c.Name, started));
                    n++;
                }
                catch { /* exited or refused */ }
            }
            return n;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static void RestorePriorities(GameModeState state)
    {
        foreach (var l in state.Lowered)
        {
            try
            {
                using var p = Process.GetProcessById(l.Pid);
                // Same process (not a new one that reused the PID) and nobody changed it since.
                if (Math.Abs((p.StartTime.ToUniversalTime() - l.StartTimeUtc).TotalSeconds) < 1 &&
                    p.PriorityClass == ProcessPriorityClass.BelowNormal)
                    p.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch { /* exited */ }
        }
    }

    // ---------------------------------------------------------------- live settings

    /// <summary>The registry alone isn't read again until sign-in; tell Windows about the mouse change now.</summary>
    private static void ApplyMouseSettings()
    {
        int[] values = [0, 0, 0];
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, values, NativeMethods.SPIF_SENDCHANGE);
    }

    private void ApplyMouseSettingsFromRegistry()
    {
        var key = RegKeyPath.CurrentUser(@"Control Panel\Mouse");
        int Read(string name, int fallback) => int.TryParse(registry.GetString(key, name), out var v) ? v : fallback;
        int[] values = [Read("MouseThreshold1", 6), Read("MouseThreshold2", 10), Read("MouseSpeed", 1)];
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, values, NativeMethods.SPIF_SENDCHANGE);
    }

    private static void BroadcastSettingsChange() =>
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, IntPtr.Zero, "Policy",
                                         NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
}
