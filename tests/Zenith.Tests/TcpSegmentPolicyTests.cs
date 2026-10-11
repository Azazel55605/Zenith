using Zenith.Core.Network.Transport;
namespace Zenith.Tests;
public sealed class TcpSegmentPolicyTests
{
    [Fact]
    public void AdvertisedWindowClosesAndReopensWhenApplicationDrains()
    {
        var buffer = new BoundedTcpBuffer(); Assert.Equal(8192, buffer.Window);
        Assert.True(buffer.Append(new byte[8192])); Assert.Equal(0, buffer.Window);
        buffer.Advance(4096); Assert.Equal(4096, buffer.Window); buffer.Advance(4096); Assert.Equal(8192, buffer.Window);
    }
    [Fact]
    public void OverflowCannotAdvertiseFreshReceiveSpace()
    {
        var buffer = new BoundedTcpBuffer(); Assert.False(buffer.Append(new byte[BoundedTcpBuffer.Maximum + 1])); Assert.Equal(0, buffer.Window);
    }
    [Theory]
    [InlineData(100u,8192u,100u,1440u,true)]
    [InlineData(100u,8192u,100u,8192u,true)]
    [InlineData(100u,0u,100u,0u,true)]
    [InlineData(100u,8192u,101u,1440u,false)]
    [InlineData(100u,8192u,99u,1440u,false)]
    [InlineData(100u,8192u,100u,8193u,false)]
    [InlineData(uint.MaxValue-10,8192u,uint.MaxValue-10,1440u,true)]
    public void OnlyContiguousPayloadFitsAdvertisedWindow(uint next,uint window,uint sequence,uint bytes,bool expected)
        => Assert.Equal(expected,TcpSegmentPolicy.Accept(next,window,sequence,bytes));
}
