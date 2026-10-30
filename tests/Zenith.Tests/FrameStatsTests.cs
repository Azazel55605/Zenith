using Zenith.Gui;

namespace Zenith.Tests;

public sealed class FrameStatsTests
{
    [Fact]
    public void SceneFramesCountAgainstTheFrameBudgetUntilReset()
    {
        FrameStats.ResetWindow();
        Assert.Equal(0, FrameStats.AverageFrameMicros);
        FrameStats.AddSceneFrame(10_000); FrameStats.AddSceneFrame(20_000); FrameStats.AddSceneFrame(60_000);
        Assert.Equal(3, FrameStats.SceneFrames);
        Assert.Equal(30_000, FrameStats.AverageFrameMicros);
        Assert.Equal(60_000, FrameStats.MaxFrameMicros);
        Assert.Equal(2, FrameStats.FramesOver16Ms);
        Assert.Equal(1, FrameStats.FramesOver33Ms);
        FrameStats.ResetWindow();
        Assert.Equal(0, FrameStats.SceneFrames);
        Assert.Equal(0, FrameStats.MaxFrameMicros);
    }
}
