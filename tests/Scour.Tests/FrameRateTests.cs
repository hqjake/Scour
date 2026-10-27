using Scour.Core.Gaming;

namespace Scour.Tests;

public class FrameRateTests
{
    private static long Ms(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);

    [Fact]
    public void Measures_frames_per_second_per_process()
    {
        var f = new FrameRate();
        for (int i = 0; i < 60; i++) f.Add(1, Ms(1000 + i * 1000.0 / 60)); // 60 fps
        for (int i = 0; i < 10; i++) f.Add(2, Ms(1000 + i * 100));        // 10 fps
        Assert.Equal(60, f.Rate(1, Ms(1990))!.Value, 0);
        Assert.Equal(10, f.Rate(2, Ms(1990))!.Value, 0);
        Assert.Equal(1, f.Busiest(Ms(1990)));
    }

    [Fact]
    public void A_process_that_stopped_presenting_has_no_rate()
    {
        var f = new FrameRate();
        f.Add(1, Ms(0));
        f.Add(1, Ms(16));
        Assert.NotNull(f.Rate(1, Ms(500)));
        Assert.Null(f.Rate(1, Ms(5000)));
        Assert.Null(f.Rate(99, Ms(0)));
        Assert.Null(f.Busiest(Ms(9000)));
    }
}
