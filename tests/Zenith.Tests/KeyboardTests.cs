using System;
using System.Reflection;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Input;

namespace Zenith.Tests;

public class KeyboardTests
{
    [Fact]
    public void PhysicalEdgesPairInOrderRejectRepeatAndRecoverFromOverflow()
    {
        Span<ushort> batch = stackalloc ushort[KeyboardEdges.Capacity];
        KeyboardEdges.SetEnabled(true);
        try
        {
            KeyboardEdges.Capture(0x11, true);
            KeyboardEdges.Capture(0x11, false);
            KeyboardEdges.Capture(0x11, false);
            KeyboardEdges.Capture(0x2a, false);
            KeyboardEdges.Capture(0x11, true);
            Assert.Equal(3, KeyboardEdges.Drain(batch));
            Assert.Equal(new ushort[] { 0x11, 0x2a, 0x111 }, batch[..3].ToArray());
            for (int i = 0; i < KeyboardEdges.Capacity; i++) { KeyboardEdges.Capture(0x11, false); KeyboardEdges.Capture(0x11, true); }
            Assert.Equal(-1, KeyboardEdges.Drain(batch));
            KeyboardEdges.Capture(0x2a, false); // still physically held, not a new press
            KeyboardEdges.Capture(0x2a, true);
            Assert.Equal(1, KeyboardEdges.Drain(batch));
            Assert.Equal(0x12a, batch[0]);
        }
        finally { KeyboardEdges.SetEnabled(false); }
        KeyboardEdges.Capture(0x11, false);
        Assert.Equal(0, KeyboardEdges.Drain(batch));
    }

    private static KeyEvent Convert(byte scan, bool shift = false, bool num = false, bool caps = false, bool altGr = false)
    {
        var method = typeof(ScanMapBase).GetMethod("ConvertScanCode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (KeyEvent)method.Invoke(new GermanKeyboardLayout(), new object[] {
            KeyboardScanPolicy.LayoutScan(scan), altGr, shift, altGr,
            KeyboardScanPolicy.UseNumLock(scan, num), caps, false })!;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GermanSlashDoesNotDependOnLocks(bool num, bool caps)
    {
        Assert.Equal('/', Convert(8, shift: true, num: num, caps: caps).KeyChar);
    }

    [Fact]
    public void GermanIsoKeysAndAltGr()
    {
        Assert.Equal('#', Convert(0x2b).KeyChar);
        Assert.Equal('<', Convert(0x56).KeyChar);
        Assert.Equal('>', Convert(0x56, shift: true).KeyChar);
        Assert.Equal('|', Convert(0x56, altGr: true).KeyChar);
        Assert.Equal('@', Convert(0x10, altGr: true).KeyChar);
        Assert.Equal('{', Convert(8, altGr: true).KeyChar);
        Assert.Equal('z', Convert(0x15).KeyChar);
        Assert.Equal('Z', Convert(0x15, caps: true).KeyChar);
        Assert.Equal('z', Convert(0x15, shift: true, caps: true).KeyChar);
    }

    [Fact]
    public void DedicatedArrowsRemainNavigationWhileKeypadKeepsNumLock()
    {
        Assert.Equal(ConsoleKeyEx.UpArrow, Convert(KeyboardScanPolicy.Ps2(0x48, true), num: true).Key);
        Assert.Equal(ConsoleKeyEx.LeftArrow, Convert(KeyboardScanPolicy.Virtio(0x4b, 105), num: true).Key);
        Assert.Equal(ConsoleKeyEx.Num8, Convert(0x48, num: true).Key);
        Assert.Equal('8', Convert(0x48, num: true).KeyChar);
        Assert.Equal(0x60, KeyboardScanPolicy.Ps2(0x38, true));
        Assert.Equal(0x56, KeyboardScanPolicy.Virtio(0, 86));
        Assert.Equal(0x5b, KeyboardScanPolicy.Virtio(0, 125)); Assert.Equal(0x5c, KeyboardScanPolicy.Virtio(0, 126));   // Meta
    }
}
