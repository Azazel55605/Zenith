namespace Zenith.Gui.Terminal;

/// <summary>ANSI intent is retained in scrollback, so existing output remains legible
/// when the user's appearance changes. Explicit literal client colors stay literal.</summary>
internal static class TerminalColors
{
    private static readonly uint[] Dark =
    {
        0xffb4b7c0, 0xfff0707a, 0xff8bcba4, 0xffe7bb70, 0xff8baaff, 0xffb6a0ff, 0xff7ecac7, 0xfff1f2f4,
        0xffb4b7c0, 0xffff8a93, 0xffa6e8b5, 0xffffdc8f, 0xff9db6ff, 0xffddb0f5, 0xff8fe6f2, 0xffffffff,
    };
    private static readonly uint[] Light =
    {
        0xff202126, 0xffae325d, 0xff277347, 0xff886010, 0xff3157b7, 0xff7042be, 0xff197571, 0xff5c606b,
        0xff5c606b, 0xff9e284e, 0xff21623c, 0xff76530e, 0xff294b9f, 0xff6037a8, 0xff14635f, 0xff202126,
    };
    public static uint Resolve(int index, bool light) => (light ? Light : Dark)[index & 15];
}
