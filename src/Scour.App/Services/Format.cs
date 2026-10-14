namespace Scour.App.Services;

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    public static string BytesOrZero(long bytes) => bytes <= 0 ? "0 B" : Bytes(bytes);

    public static string Plural(int n, string singular, string? plural = null) =>
        $"{n:N0} {(n == 1 ? singular : plural ?? singular + "s")}";
}
