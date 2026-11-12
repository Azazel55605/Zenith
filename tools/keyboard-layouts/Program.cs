using System; using System.Reflection; using System.Linq; using System.Collections; using System.Text;
var dir = args[0];
AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var p = System.IO.Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); return System.IO.File.Exists(p) ? Assembly.LoadFrom(p) : null; };
var asm = Assembly.LoadFrom(System.IO.Path.Combine(dir, "Cosmos.Kernel.System.dll"));
var mappingType = asm.GetType("Cosmos.Kernel.System.Keyboard.KeyMapping");
var full = mappingType.GetConstructors().Single(c => c.GetParameters().Length == 13);
string[] order = { "ScanCode", "Value", "Shift", "NumLock", "CapsLock", "ShiftCapsLock", "ShiftNumLock", "ControlAlt", "ControlAltShift", "Control", "ControlShift", "Key", "NumLockKey" };
object Get(object k, string f) => mappingType.GetProperty(f, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(k);
var code = new StringBuilder();
code.AppendLine("// Generated from the Cosmos 3.0.89 layouts (BSD 3-Clause, see LICENSE) with the Meta keys added;");
code.AppendLine("// regenerate with tools/keyboard-layouts when Cosmos changes. Every mapping uses the full constructor.");
code.AppendLine("using Cosmos.Kernel.System.Keyboard;\n\nnamespace Zenith.Core.Input;\n");
int total = 0;
foreach (var (name, cls) in new[] { ("USStandardLayout", "UsLayout"), ("FRStandardLayout", "FrLayout"), ("ESStandardLayout", "EsLayout"), ("GBStandardLayout", "GbLayout"), ("TRStandardLayout", "TrLayout"), ("USDvorakLayout", "DvorakLayout") })
{
    var t = asm.GetType("Cosmos.Kernel.System.Keyboard.ScanMaps." + name);
    var o = Activator.CreateInstance(t, true);
    t.BaseType.GetMethod("EnsureKeysInitialized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(o, null);
    var keys = (IList)t.BaseType.GetProperty("Keys", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(o);
    code.AppendLine($"/// <summary>Cosmos {name} plus Meta (0x5B, 0x5C).</summary>");
    code.AppendLine($"internal sealed class {cls} : ScanMapBase\n{{\n    protected override void InitializeKeys()\n    {{");
    bool left = false, right = false;
    foreach (var k in keys)
    {
        var values = order.Select(f => Get(k, f)).ToArray();
        // The full constructor must reproduce every field.
        var copy = full.Invoke(values);
        foreach (var f in order) if (!Equals(Get(copy, f), Get(k, f))) throw new Exception(name + " field " + f + " differs");
        byte scan = (byte)values[0]; left |= scan == 0x5b; right |= scan == 0x5c;
        string C(object v) => "(char)" + (int)(char)v;
        code.AppendLine($"        Keys.Add(new KeyMapping(0x{scan:x2}, {string.Join(", ", values.Skip(1).Take(10).Select(C))}, ConsoleKeyEx.{values[11]}, ConsoleKeyEx.{values[12]}));");
        total++;
    }
    if (!left) code.AppendLine("        Keys.Add(new KeyMapping(0x5b, ConsoleKeyEx.LWin));");
    if (!right) code.AppendLine("        Keys.Add(new KeyMapping(0x5c, ConsoleKeyEx.RWin));");
    code.AppendLine("    }\n}\n");
}
System.IO.File.WriteAllText(args[1], code.ToString());
Console.WriteLine("mappings " + total);
