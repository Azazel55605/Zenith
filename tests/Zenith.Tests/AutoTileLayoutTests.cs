using System.Collections.Generic;
using System.Linq;
using Xunit;
using Zenith.Gui;
using Zenith.Gui.Graphics;
using Limits = Zenith.Gui.AutoTileLayout.Limits;

namespace Zenith.Tests;

public class AutoTileLayoutTests
{
    private static readonly Limits Terminal = new(360, 200, int.MaxValue, int.MaxValue);
    private static readonly Limits Native = new(600, 524, int.MaxValue, int.MaxValue);
    private static readonly Rect Work = new(0, 56, 1280, 744);

    private static List<Rect> Arrange(Rect work, Limits[] windows, int selected = 0, int stack = 1, int split = 55,
        int innerGap = 0, int outerGap = 0)
    {
        List<Rect> slots = new();
        AutoTileLayout.Arrange(work, windows, split, innerGap, outerGap, selected, stack, slots);
        for (int i = 0; i < slots.Count; i++)
        {
            Rect slot = slots[i];
            if (slot.IsEmpty) { continue; }
            Assert.True(work.Contains(slot.X, slot.Y) && work.Contains(slot.Right - 1, slot.Bottom - 1));
            Assert.InRange(slot.W, windows[i].MinWidth, windows[i].MaxWidth);
            Assert.InRange(slot.H, windows[i].MinHeight, windows[i].MaxHeight);
            for (int j = 0; j < i; j++) { Assert.True(slot.Intersect(slots[j]).IsEmpty); }
        }
        return slots;
    }

    [Fact]
    public void EmptyAndSingleWorkspaceUseThePanelWorkArea()
    {
        Assert.Empty(Arrange(Work, []));
        Assert.Equal(Work, Assert.Single(Arrange(Work, [Terminal])));
        Assert.Equal(new Rect(0, 56, 700, 600), Assert.Single(Arrange(Work, [new(360, 200, 700, 600)])));
    }

    [Fact]
    public void SingleTileUsesOnlyTheOuterGapInsideThePanelWorkArea()
    {
        Assert.Equal(Work, Assert.Single(Arrange(Work, [Terminal], innerGap: 32)));
        Assert.Equal(new Rect(12, 68, 1256, 720), Assert.Single(Arrange(Work, [Terminal], innerGap: 9, outerGap: 12)));
    }

    [Fact]
    public void IndependentGapsSeparateTheMasterAndEachStackRowInEitherOrientation()
    {
        var wide = Arrange(Work, [Terminal, Terminal, Terminal], split: 50, innerGap: 9, outerGap: 12);
        Assert.Equal(new Rect(12, 68, 623, 720), wide[0]);
        Assert.Equal(new Rect(644, 68, 624, 355), wide[1]);
        Assert.Equal(new Rect(644, 432, 624, 356), wide[2]);
        var portrait = Arrange(new Rect(56, 0, 744, 1280), [Terminal, Terminal, Terminal],
            split: 50, innerGap: 9, outerGap: 4);
        Assert.Equal(new Rect(60, 4, 736, 631), portrait[0]);
        Assert.Equal(new Rect(60, 644, 363, 632), portrait[1]);
        Assert.Equal(new Rect(432, 644, 364, 632), portrait[2]);
    }

    [Fact]
    public void TightDimensionsReduceGapsAndKeepOverflowWindowsReachable()
    {
        var rows = Arrange(new Rect(0, 48, 720, 403), [Terminal, Terminal, Terminal], innerGap: 32);
        Assert.Equal(new Rect(0, 48, 360, 403), rows[0]);
        Assert.Equal(new Rect(360, 48, 360, 200), rows[1]);
        Assert.Equal(new Rect(360, 251, 360, 200), rows[2]);

        var page = Arrange(new Rect(0, 48, 736, 425), [Terminal, Terminal, Terminal], selected: 2, innerGap: 32, outerGap: 32);
        Assert.Equal(new Rect(8, 80, 360, 361), page[0]);
        Assert.True(page[1].IsEmpty);
        Assert.Equal(new Rect(368, 80, 360, 361), page[2]);
        var one = Arrange(new Rect(5, 7, 620, 525), [Native, Native], selected: 1, innerGap: 32, outerGap: 32);
        Assert.True(one[0].IsEmpty);
        Assert.Equal(new Rect(15, 7, 600, 525), one[1]);
    }

    [Fact]
    public void LandscapeMasterAndStackDivideAllPixelsAndConstrainRatioToWindowLimits()
    {
        var slots = Arrange(Work, [Native, Native], split: 75);
        Assert.Equal(new Rect(0, 56, 680, 744), slots[0]);
        Assert.Equal(new Rect(680, 56, 600, 744), slots[1]);
        var odd = Arrange(new Rect(5, 9, 1201, 741), [Terminal, Terminal, Terminal, Terminal], split: 50);
        Assert.Equal(new Rect(5, 9, 600, 741), odd[0]);
        Assert.Equal(new Rect(605, 503, 601, 247), odd[3]);
    }

    [Fact]
    public void PortraitMasterSpansTheTopWithTheStackAcrossTheBottom()
    {
        var slots = Arrange(new Rect(0, 0, 810, 1200), [Native, Terminal, Terminal]);
        Assert.Equal(new Rect(0, 0, 810, 660), slots[0]);
        Assert.Equal(new Rect(0, 660, 405, 540), slots[1]);
        Assert.Equal(new Rect(405, 660, 405, 540), slots[2]);
    }

    [Fact]
    public void StackOverflowKeepsTheMasterAndRevealsEveryWindowAtItsLegalSize()
    {
        var sizes = Enumerable.Repeat(Terminal, 17).ToArray();
        for (int i = 1; i < sizes.Length; i++)
        {
            var slots = Arrange(Work, sizes, selected: i);
            Assert.False(slots[0].IsEmpty || slots[i].IsEmpty);
            Assert.Equal(slots, Arrange(Work, sizes, selected: 0, stack: i));
            Assert.InRange(slots.Count(slot => !slot.IsEmpty), 2, 4);
        }
    }

    [Fact]
    public void WhenNeitherSplitFitsTheSelectedWindowOwnsTheWorkspace()
    {
        Rect small = new(0, 44, 800, 556);
        var slots = Arrange(small, [Native, Native, Native], selected: 2);
        Assert.True(slots[0].IsEmpty && slots[1].IsEmpty);
        Assert.Equal(small, slots[2]);
    }

    [Fact]
    public void DirectionalNavigationChoosesAdjacentVisibleWindow()
    {
        var slots = Arrange(Work, [Terminal, Terminal, Terminal]);
        Assert.Equal(1, AutoTileLayout.Neighbor(slots, 0, 1, 0));
        Assert.Equal(2, AutoTileLayout.Neighbor(slots, 1, 0, 1));
        Assert.Equal(0, AutoTileLayout.Neighbor(slots, 2, -1, 0));
        Assert.Equal(-1, AutoTileLayout.Neighbor(slots, 1, 0, -1));
        var overflow = Arrange(Work, [Native, Native, Native], selected: 2);
        Assert.Equal(2, AutoTileLayout.Neighbor(overflow, 0, 1, 0));
    }
}
