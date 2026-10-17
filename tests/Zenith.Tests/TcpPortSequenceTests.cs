using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Zenith.Core.Network.Transport;

namespace Zenith.Tests;

public sealed class TcpPortSequenceTests
{
    [Fact]
    public void AllocationRequiresInitializationAndCannotBeReseeded()
    {
        Assert.False(TcpPortAllocation.Initialized);
        Assert.Throws<IOException>(() => TcpPortAllocation.Next());
        var expected = new TcpPortSequence(0x318acdf0u);
        TcpPortAllocation.Initialize(0x318acdf0u);
        Assert.True(TcpPortAllocation.Initialized);
        Assert.Equal(expected.Next(), TcpPortAllocation.Next());
        TcpPortAllocation.Initialize(0u);
        Assert.Equal(expected.Next(), TcpPortAllocation.Next());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(0x147abcdfu)]
    public void VisitsEntireDynamicRangeBeforeRepeating(uint seed)
    {
        var sequence = new TcpPortSequence(seed);
        ushort first = sequence.Next();
        var ports = new HashSet<ushort> { first };
        for (int i = 1; i < 16384; i++) { Assert.True(ports.Add(sequence.Next())); }
        Assert.Equal(49152, (int)ports.Min()); Assert.Equal(65535, (int)ports.Max());
        Assert.Equal(first, sequence.Next());
    }

    [Fact]
    public void SeedChangesStartAndStride()
    {
        var a = new TcpPortSequence(123u);
        var b = new TcpPortSequence(124u);
        var c = new TcpPortSequence(123u | (2u << 14));
        Assert.NotEqual(a.Next(), b.Next()); Assert.Equal(49275, (int)c.Next());
        Assert.NotEqual(a.Next(), c.Next());
    }

    [Fact]
    public void ConcurrentCandidatesStayUniqueAcrossOneCycle()
    {
        var sequence = new TcpPortSequence(0xa5877fffu); var ports = new ConcurrentBag<ushort>();
        Parallel.For(0, 16384, _ => ports.Add(sequence.Next()));
        Assert.Equal(16384, ports.Distinct().Count());
    }

    [Fact]
    public void SkippingOccupiedCandidatesDoesNotRestartSequence()
    {
        var sequence = new TcpPortSequence(0xffffffffu);
        var occupied = new HashSet<ushort>();
        for (int i = 0; i < 10; i++) { occupied.Add(sequence.Next()); }
        Assert.DoesNotContain(sequence.Next(), occupied);
    }
}
