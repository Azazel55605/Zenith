using System.Reflection;
using Cosmos.Kernel.System.Keyboard;
using Zenith.Core.Input;

namespace Zenith.Tests;

public class KeyboardTests
{
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
    }
}
