using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Cosmos.Kernel.System.Keyboard;
using MyOS.Gui;
using MyOS.Gui.Graphics;

namespace MyOS.Apps;

/// <summary>A minimal plain-text scratchpad: type, Backspace, Enter. The view follows the end of the text.</summary>
internal sealed class NotesWindow : Window
{
    private const int Padding = 20;
    private readonly StringBuilder _text = new();
    private bool _caretVisible = true;

    public NotesWindow() : base("Notes", 460, 340)
    {
    }

    public override bool Update()
    {
        // Blink at 2 Hz while focused; stay solid when not.
        bool visible = !IsFocused || Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0;
        if (visible == _caretVisible)
        {
            return false;
        }

        _caretVisible = visible;
        return true;
    }

    public override void OnKey(KeyEvent key)
    {
        if (key.Key == ConsoleKeyEx.Backspace)
        {
            if (_text.Length > 0)
            {
                _text.Length--;
            }
        }
        else if (key.Key == ConsoleKeyEx.Enter)
        {
            _text.Append('\n');
        }
        else if (key.KeyChar >= ' ')
        {
            _text.Append(key.KeyChar);
        }
    }

    public override void DrawContent(Surface surface, Rect content)
    {
        int x = content.X + Padding;
        int width = content.W - 2 * Padding;
        int lineHeight = Fonts.Regular.GetLineHeight(Theme.TextBody) + 2;

        if (_text.Length == 0)
        {
            surface.DrawText("Start typing…", Fonts.Regular, Theme.TextBody, Theme.TextMuted, x + 3, content.Y + Padding);
        }

        List<string> lines = Fonts.Wrap(_text.ToString(), Fonts.Regular, Theme.TextBody, width - 4);
        int visible = (content.H - 2 * Padding) / lineHeight;
        int first = lines.Count > visible ? lines.Count - visible : 0;

        int y = content.Y + Padding;
        for (int i = first; i < lines.Count; i++)
        {
            surface.DrawText(lines[i], Fonts.Regular, Theme.TextBody, Theme.TextPrimary, x, y);
            y += lineHeight;
        }

        if (IsFocused && _caretVisible)
        {
            string last = lines[^1];
            int caretX = x + Fonts.Regular.MeasureString(last, Theme.TextBody) + 1;
            surface.FillRect(new Rect(caretX, y - lineHeight + 1, 2, lineHeight - 4), Theme.Accent);
        }
    }
}
