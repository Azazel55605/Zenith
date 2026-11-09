using Mono.Cecil;
using Mono.Cecil.Cil;

// The IntelliMouse knock (sample rates 200, 100, 80, then Get Device ID) leaves a PS/2
// mouse reporting 80 times a second: pointer motion and drags arrived in visible steps.
// Set 200 reports a second (the PS/2 maximum, as Linux psmouse does) after the ID is read.
internal static class PointerRateFixup
{
    internal static bool Apply(ModuleDefinition module)
    {
        var mouse = module.GetType("Cosmos.Kernel.HAL.X64.Devices.Input.PS2Mouse")
            ?? throw new InvalidOperationException("Cosmos PS/2 mouse changed");
        var knock = mouse.Methods.Single(m => m.Name == "TryEnableScrollWheel");
        var code = knock.Body.Instructions;
        var sends = code.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Name == "SendCommand").ToArray();
        var read = code.Where(i => i.Operand is MethodReference m && m.Name == "ReadByteAfterAck").ToArray();
        if (read.Length != 1) { throw new InvalidOperationException("Cosmos PS/2 device ID read changed"); }
        if (sends.Length == 5)
        {
            if (sends[4].Previous.Previous.Previous.Previous.Previous != read[0])
            { throw new InvalidOperationException("Incomplete PS/2 report rate adaptation"); }
            return false;
        }
        if (sends.Length != 4 || code.IndexOf(sends[0]) != 5) { throw new InvalidOperationException("Cosmos PS/2 knock sequence changed"); }
        var first = code.Take(6).ToArray();
        if (first[0].OpCode != OpCodes.Ldarg_0 || first[1].OpCode != OpCodes.Ldc_I4 || (int)first[1].Operand != 0xF3
            || first[2].OpCode != OpCodes.Ldc_I4 || (int)first[2].Operand != 200 || first[3].OpCode != OpCodes.Conv_U1
            || first[4].OpCode != OpCodes.Newobj || read[0].Next.OpCode != OpCodes.Ldc_I4_3 || read[0].Next.Next.OpCode != OpCodes.Ceq)
        { throw new InvalidOperationException("Cosmos PS/2 sample rate command changed"); }
        var il = knock.Body.GetILProcessor();
        var cursor = read[0];
        foreach (var source in first)
        {
            var copy = source.Operand switch
            {
                int value => il.Create(source.OpCode, value),
                MethodReference method => il.Create(source.OpCode, method),
                null => il.Create(source.OpCode),
                _ => throw new InvalidOperationException("Cosmos PS/2 sample rate operand changed")
            };
            il.InsertAfter(cursor, copy); cursor = copy;
        }
        knock.Body.MaxStackSize = Math.Max(knock.Body.MaxStackSize, 5);
        Console.WriteLine("Adapted PS/2 mouse: 200 reports a second after the wheel knock instead of 80");
        return true;
    }
}
