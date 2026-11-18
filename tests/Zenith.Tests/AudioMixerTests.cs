using Zenith.Core.Audio;
namespace Zenith.Tests;

public sealed class AudioMixerTests
{
    private static byte[] Pcm(short left, short right, int frames = 1)
    {
        var b = new byte[frames * 4];
        for (int i = 0; i < b.Length; i += 4)
        { b[i] = (byte)left; b[i + 1] = (byte)(left >> 8); b[i + 2] = (byte)right; b[i + 3] = (byte)(right >> 8); }
        return b;
    }
    private static short Left(byte[] b) => (short)(b[0] | b[1] << 8);
    [Fact] public void MixingSaturatesWithoutWrappingAndHonorsEachStreamGain()
    {
        var m = new AudioMixer(); long a = m.Open(1, 1000, "one", 0), b = m.Open(2, 1000, "two", 0);
        Assert.Equal(4, m.Transfer(1, a, Pcm(30000, -30000), true));
        Assert.Equal(4, m.Transfer(2, b, Pcm(10000, -10000), true));
        var output = new byte[4]; Assert.Equal(32768, m.Mix(output, 100, false));
        Assert.Equal(short.MaxValue, Left(output)); Assert.Equal(0, output[2]); Assert.Equal(128, output[3]);
        m.Transfer(1, a, Pcm(10000, 10000), true); m.Find(a)!.Volume = 50;
        Assert.Equal(2500, m.Mix(output, 50, false)); Assert.Equal(2500, Left(output));
    }
    [Fact] public void MuteConsumesPcmButNeverLeaksSamples()
    {
        var m = new AudioMixer(); long a = m.Open(1, 1, "app", 0); m.Find(a)!.Muted = true;
        m.Transfer(1, a, Pcm(20000, -20000), true); var b = new byte[4];
        Assert.Equal(0, m.Mix(b, 100, false)); Assert.All(b, x => Assert.Equal(0, x)); Assert.Equal(0, m.Find(a)!.Count);
        m.Find(a)!.Muted = false; m.Transfer(1, a, Pcm(20000, -20000), true);
        m.Mix(b, 100, true); Assert.All(b, x => Assert.Equal(0, x));
    }
    [Fact] public void QueueCapacityAndWholeFramesSurviveWrapAround()
    {
        var m = new AudioMixer(); long h = m.Open(1, 1, "app", 0); var block = Pcm(1234, -5678, 2048);
        for (int i = 0; i < 4; i++) { Assert.Equal(8192, m.Transfer(1, h, block, true)); }
        Assert.Equal(-11, m.Transfer(1, h, block, true));
        var output = new byte[4096]; m.Mix(output, 100, false); Assert.Equal(4096, m.Transfer(1, h, block, true));
        Assert.Equal(AudioMixer.QueueBytes, m.Find(h)!.Count); Assert.Equal(1234, Left(output));
        Assert.Equal(-22, m.Transfer(1, h, new byte[3], true));
    }
    [Fact] public void CaptureFansOutDropsOldestFramesAndMutesIndividually()
    {
        var m = new AudioMixer(); long a = m.Open(1, 10, "a", 1), b = m.Open(2, 10, "b", 1);
        m.Find(b)!.Muted = true;
        for (int i = 0; i < 10; i++) { m.Capture(Pcm((short)(i * 100), -100, 1024), 100, false); }
        var data = new byte[4]; Assert.Equal(4, m.Transfer(1, a, data, false)); Assert.Equal(200, Left(data));
        Assert.Equal(2048, m.Find(a)!.Dropped); Assert.Equal(32764, m.Find(a)!.Count);
        Assert.Equal(4, m.Transfer(2, b, data, false)); Assert.Equal(0, Left(data));
    }
    [Fact] public void OwnersDirectionAndGenerationCannotBeConfused()
    {
        var m = new AudioMixer(); long a = m.Open(1, 10, "a", 0); var data = new byte[4];
        Assert.Equal(-9, m.Transfer(2, a, data, true)); Assert.Equal(-9, m.Close(2, a));
        Assert.Equal(-22, m.Transfer(1, a, data, false)); Assert.Equal(0, m.Close(1, a));
        long b = m.Open(1, 10, "a", 0); Assert.NotEqual(a, b); Assert.Equal(-9, m.Transfer(1, a, data, true));
        m.Release(1); Assert.False(m.Active(0));
    }
    [Fact] public void StreamLimitIsBoundedAndReleaseReturnsCapacity()
    {
        var m = new AudioMixer(); for (int i = 0; i < 16; i++) { Assert.True(m.Open(i, 10, "app", 0) > 0); }
        Assert.Equal(-24, m.Open(30, 10, "no", 0)); m.Release(5); Assert.True(m.Open(30, 10, "yes", 1) > 0);
    }
}
