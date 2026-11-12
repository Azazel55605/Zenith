using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Zenith.Gui.Shell.Prism;

namespace Zenith.Tests;

public class PrismTests
{
    private static readonly PrismItem[] Candidates =
    {
        new(PrismKind.App, "Meridian", "System settings", "sliders-horizontal", "Meridian", Keywords: "System settings"),
        new(PrismKind.App, "Polaris", "Command line shell", "terminal", "Terminal", Keywords: "Command line shell"),
        new(PrismKind.App, "Observatory", "CPU, memory and running programs", "activity", "Observatory", Keywords: "CPU, memory and running programs"),
        new(PrismKind.Window, "Polaris - root", "Open window", "terminal", "0"),
        new(PrismKind.Setting, "Taskbar", "Meridian · Windows", "sliders-horizontal", "windows.taskbar", Keywords: "Windows"),
        new(PrismKind.Setting, "Animations", "Meridian · Style", "sliders-horizontal", "style.animations", Keywords: "Style"),
        new(PrismKind.Setting, "About", "Meridian · System", "sliders-horizontal", "system.about", Keywords: "System"),
        new(PrismKind.Action, "Shut down", "Power", "zap", "shutdown"),
    };

    [Fact]
    public void PrefixBeatsWordStartBeatsSubstringBeatsLettersInOrder()
    {
        int prefix = PrismSearch.Score("mer", "Meridian"), word = PrismSearch.Score("mer", "Open Meridian"),
            inside = PrismSearch.Score("rid", "Meridian"), letters = PrismSearch.Score("mdn", "Meridian");
        Assert.True(prefix > word && word > inside && inside > letters && letters > 0);
        Assert.Equal(0, PrismSearch.Score("xyz", "Meridian"));
        Assert.Equal(0, PrismSearch.Score("nm", "Meridian"));              // order matters
    }

    [Fact]
    public void TheShownSubtitleIsNotSearched()
    {
        // Every settings page reads "Meridian · Category": typing "mer" must not list them all.
        Assert.DoesNotContain(PrismSearch.Search("mer", Candidates), r => r.Kind == PrismKind.Setting);
        Assert.Contains(PrismSearch.Search("style", Candidates), r => r.Title == "Animations");   // the category is searched
        Assert.Contains(PrismSearch.Search("memory", Candidates), r => r.Title == "Observatory"); // an app's description is
    }

    [Fact]
    public void ResultsAreGroupedBySectionBestFirstAndEndWithACommand()
    {
        var results = PrismSearch.Search("ta", Candidates);
        Assert.Equal(new[] { "Taskbar", "Run \"ta\" in a terminal" }, results.Select(r => r.Title).Where(t => t != "Animations"));
        results = PrismSearch.Search("pol", Candidates);
        Assert.Equal(PrismKind.App, results[0].Kind);
        Assert.Equal(PrismKind.Window, results[1].Kind);
        Assert.Equal(PrismKind.Command, results[^1].Kind);
        // Empty: everything (no command row), in section order.
        var all = PrismSearch.Search("", Candidates, maximum: 50);
        Assert.Equal(Candidates.Length, all.Count);
        Assert.True(all.Select(r => (int)r.Kind).SequenceEqual(all.Select(r => (int)r.Kind).OrderBy(k => k)));
        // A filter keeps one kind and drops the calculator and command rows.
        Assert.All(PrismSearch.Search("a", Candidates, PrismKind.Setting), r => Assert.Equal(PrismKind.Setting, r.Kind));
        Assert.Equal(3, PrismSearch.Search("", Candidates, maximum: 3).Count);
    }

    [Theory]
    [InlineData("12 * (4 + 3)", "84")]
    [InlineData("2^10", "1024")]
    [InlineData("-3 + 5", "2")]
    [InlineData("7 / 2", "3.5")]
    [InlineData("10 % 4", "2")]
    [InlineData("3x4", "12")]
    [InlineData("1.5 + .5", "2")]
    public void CalculatorAnswersFirst(string query, string answer)
    {
        var results = PrismSearch.Search(query, Candidates);
        Assert.Equal(PrismKind.Calculator, results[0].Kind);
        Assert.Equal(answer, results[0].Target);
        Assert.Equal(query + " = " + answer, results[0].Title);
    }

    [Theory]
    [InlineData("12")]            // a number alone is not a calculation
    [InlineData("(1+2")]
    [InlineData("1+")]
    [InlineData("1/0")]
    [InlineData("meridian")]
    public void CalculatorStaysQuietOnNonsense(string query)
        => Assert.DoesNotContain(PrismSearch.Search(query, Candidates), r => r.Kind == PrismKind.Calculator);

    [Fact]
    public void EvaluatorIsBounded()
    {
        Assert.False(PrismSearch.TryEvaluate(new string('(', 40) + "1" + new string(')', 40), out _));
        Assert.False(PrismSearch.TryEvaluate(new string('1', 200), out _));
        Assert.True(PrismSearch.TryEvaluate(new string('(', 10) + "1" + new string(')', 10), out double v) && v == 1);
    }

    [Fact]
    public void LeadingAngleRunsOnlyTheCommand()
    {
        var results = PrismSearch.Search("> ls -l /", Candidates);
        Assert.Single(results);
        Assert.Equal(PrismKind.Command, results[0].Kind);
        Assert.Equal("ls -l /", results[0].Target);
        Assert.Empty(PrismSearch.Search(">", Candidates));
    }

    [Fact]
    public void ActionsNeedRootForPower()
    {
        Assert.Equal(new[] { "logout" }, PrismSearch.Actions(false).Select(a => a.Target));
        Assert.Equal(new[] { "logout", "restart", "shutdown" }, PrismSearch.Actions(true).Select(a => a.Target));
    }

    [Fact]
    public void SettingsPagesMatchTheReviewedInventory()
    {
        string doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/MERIDIAN-PAGES.md"));
        var expected = new System.Collections.Generic.List<(string, string, string)>(); string category = "";
        foreach (string line in doc.Split('\n'))
        {
            var heading = Regex.Match(line, @"^## [0-9]+\. (.+)$"); if (heading.Success) { category = heading.Groups[1].Value.Trim(); }
            var row = Regex.Match(line, @"^\| `([a-z.-]+)` — ([^|]+) \|"); if (row.Success) { expected.Add((row.Groups[1].Value, row.Groups[2].Value.Trim(), category)); }
        }
        Assert.Equal(expected, MeridianPages.All.Select(p => (p.Id, p.Title, p.Category)));
        Assert.Equal(44, PrismSearch.SettingsPages().Count());
    }
}
