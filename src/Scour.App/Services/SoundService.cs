using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Scour.App.Services;

public enum UiSound
{
    Click,
    Nav,
    Scan,
    Success,
    Warning,
    Startup,
}

/// <summary>Short interface sounds. Several can overlap; all respect the Settings switch.</summary>
public static class SoundService
{
    private static readonly Dictionary<UiSound, Uri> Files = new();
    private static readonly List<MediaPlayer> Players = [];
    private static DateTime _lastClick;

    public static bool Enabled { get; set; } = true;
    public static double Volume { get; set; } = 0.45;

    /// <summary>Copies the embedded WAVs to a cache folder (MediaPlayer needs file URIs).</summary>
    public static void Initialize()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scour", "Sounds");
            Directory.CreateDirectory(dir);
            foreach (var s in Enum.GetValues<UiSound>())
            {
                var name = s.ToString().ToLowerInvariant() + ".wav";
                var target = Path.Combine(dir, name);
                var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Scour;component/Assets/Sounds/{name}"));
                if (res is null) continue;
                using (var src = res.Stream)
                using (var dst = File.Create(target))
                    src.CopyTo(dst);
                Files[s] = new Uri(target);
            }
        }
        catch { Enabled = false; }
    }

    public static void Play(UiSound sound)
    {
        if (!Enabled || !Files.TryGetValue(sound, out var uri)) return;
        if (sound == UiSound.Click)
        {
            // Rapid repeated clicks shouldn't machine-gun.
            if ((DateTime.UtcNow - _lastClick).TotalMilliseconds < 60) return;
            _lastClick = DateTime.UtcNow;
        }
        try
        {
            var player = new MediaPlayer { Volume = sound == UiSound.Click ? Volume * 0.6 : Volume };
            player.MediaEnded += (_, _) =>
            {
                player.Close();
                Players.Remove(player);
            };
            Players.Add(player); // keep a reference until it finishes
            player.Open(uri);
            player.Play();
        }
        catch { /* audio device unavailable */ }
    }
}
