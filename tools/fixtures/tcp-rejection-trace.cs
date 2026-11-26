// Inspect the rejected-segment path in the exact Cosmos assembly used by the adapter.
using Mono.Cecil;
using Mono.Cecil.Cil;

const string tcp = "Cosmos.Kernel.System.Network.TCP.";
const string trace = "[TCP] Sequence number or segment data invalid, packet passed.\n";
const string serial = "System.Void Cosmos.Kernel.Core.IO.Serial::WriteString(System.String)";
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
var method = assembly.MainModule.GetType(tcp + "Tcp").Methods.Single(m => m.Name == "ReceiveDataInternal");
var code = method.Body.Instructions;
var text = code.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, trace));
var write = text.Next;

if (args[1] == "change-sink")
{
    // Another valid single-string call at the same offset must not be suppressed.
    write.Operand = assembly.MainModule.ImportReference(
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!);
    assembly.Write(args[2]);
    return;
}

if (args[1] == "change-payload")
{
    var established = method.DeclaringType.Methods.Single(m => m.Name == "ProcessEstablished");
    var accessor = established.Body.Instructions.First(i => i.Operand is MethodReference m && m.Name == "get_TcpData");
    var packet = assembly.MainModule.GetType(tcp + "TcpPacket");
    accessor.Operand = packet.Methods.Single(m => m.Name == "get_RawData");
    assembly.Write(args[2]);
    return;
}

bool adapted = args[1] == "adapted";
void Require(bool condition, string failure)
{
    if (!condition) throw new InvalidOperationException(failure);
}

bool Calls(Instruction i, string name) =>
    i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.FullName == name;

Require(adapted ? write.OpCode == OpCodes.Pop : Calls(write, serial), "rejection trace sink");
Require(write.Next.OpCode == OpCodes.Ret, "rejection must return");
var ack = text.Previous;
Require(Calls(ack, $"System.Void {tcp}Tcp::SendEmptyPacket({tcp}TcpFlags)"), "missing or incorrect ACK call");
Require((ack.Previous.OpCode == OpCodes.Ldc_I4_S || ack.Previous.OpCode == OpCodes.Ldc_I4)
    && Convert.ToInt32(ack.Previous.Operand) == 0x10 && ack.Previous.Previous.OpCode == OpCodes.Ldarg_0,
    "ACK flags or connection changed");
var skip = ack.Previous.Previous.Previous;
Require((skip.OpCode == OpCodes.Brtrue || skip.OpCode == OpCodes.Brtrue_S) && skip.Operand == text,
    "received RST must skip ACK");
var rst = skip.Previous;
Require(rst.OpCode == OpCodes.Ldfld && rst.Operand is FieldReference f && f.FullName == $"System.Boolean {tcp}TcpPacket::_rst"
    && rst.Previous.OpCode == OpCodes.Ldarg_1, "reset test changed");

if (adapted)
{
    var check = code.Single(i => Calls(i,
        "System.Boolean Zenith.Core.Network.Transport.TcpSegmentPolicy::ReceiveAllowed(System.UInt32,System.UInt32,System.UInt32,System.UInt32,System.Int32,System.Byte)"));
    Require((check.Next.OpCode == OpCodes.Brfalse || check.Next.OpCode == OpCodes.Brfalse_S)
        && check.Next.Operand == rst.Previous, "rejected segment must still enter ACK path");
}

foreach (var retained in new[] { "[TCP] Packet received on CLOSED connection, ignoring\n",
                                 "[TCP] Packet received in TIME_WAIT, ignoring\n" })
{
    var other = code.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, retained));
    Require(Calls(other.Next, serial), "unrelated TCP trace changed");
}

if (adapted)
{
    // Execute admission, sender ACK checks, payload delivery, RCV.NXT, advertised window and ACK
    // dispatch from the real assembly. Replace only device transmission and packet data extraction.
    var module = assembly.MainModule;
    var connection = method.DeclaringType;
    var packet = module.GetType(tcp + "TcpPacket");
    var payload = new FieldDefinition("FixtureBytes", FieldAttributes.Public, new ArrayType(module.TypeSystem.Byte));
    packet.Fields.Add(payload);
    foreach (var getter in packet.Methods.Where(m => m.Name == "get_TcpDataLength" || m.Name == "get_TcpData"))
    {
        getter.Body = new MethodBody(getter);
        var il = getter.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, payload);
        if (getter.Name == "get_TcpDataLength") { il.Emit(OpCodes.Ldlen); il.Emit(OpCodes.Conv_U2); }
        il.Emit(OpCodes.Ret);
    }
    var sends = new FieldDefinition("FixtureAckCount", FieldAttributes.Public, module.TypeSystem.Int32);
    connection.Fields.Add(sends);
    var send = connection.Methods.Single(m => m.Name == "SendEmptyPacket" && m.Parameters.Count == 1);
    var established = connection.Methods.Single(m => m.Name == "ProcessEstablished");
    foreach (var i in established.Body.Instructions.Where(i => Calls(i, serial)))
    {
        i.OpCode = OpCodes.Pop; i.Operand = null; // The fixture has no UART.
    }
    var pause = connection.Methods.Single(m => m.Name == "SimpleWait");
    pause.Body = new MethodBody(pause); pause.Body.GetILProcessor().Emit(OpCodes.Ret);
    send.Body = new MethodBody(send);
    var stub = send.Body.GetILProcessor();
    stub.Emit(OpCodes.Ldarg_0); stub.Emit(OpCodes.Dup); stub.Emit(OpCodes.Ldfld, sends);
    stub.Emit(OpCodes.Ldc_I4_1); stub.Emit(OpCodes.Add); stub.Emit(OpCodes.Stfld, sends); stub.Emit(OpCodes.Ret);
    var copy = Path.Combine(Path.GetDirectoryName(args[0])!, "instrumented-tcp.dll");
    assembly.Write(copy);
    var context = System.Runtime.Loader.AssemblyLoadContext.Default;
    context.Resolving += (load, name) => {
        string path = Path.Combine(Environment.CurrentDirectory, name.Name + ".dll");
        return File.Exists(path) ? load.LoadFromAssemblyPath(path) : null;
    };
    var actual = context.LoadFromAssemblyPath(copy);
    var type = actual.GetType(connection.FullName, true)!;
    var packetType = actual.GetType(packet.FullName, true)!;
    var tcbType = actual.GetType(tcp + "TransmissionControlBlock", true)!;
    const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    object Raw(Type t) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
    var client = Raw(type);
    var state = Raw(tcbType);
    type.GetField("<TCB>k__BackingField", members)!.SetValue(client, state);
    var statusField = type.GetField("<Status>k__BackingField", members)!;
    statusField.SetValue(client, Enum.ToObject(statusField.FieldType, 3));
    void Put(object obj, string field, object value) => obj.GetType().GetField(field, members)!.SetValue(obj, value);
    void State(string prop, uint val) => tcbType.GetProperty(prop, members)!.SetValue(state, val);
    uint Value(string prop) => (uint)tcbType.GetProperty(prop, members)!.GetValue(state)!;
    State("RcvNxt", 100); State("RcvWnd", 32768); State("SndUna", 0); State("SndNxt", 0);
    void Deliver(uint seq, byte flags, byte[] bytes) {
        var incoming = Raw(packetType);
        Put(incoming, "FixtureBytes", bytes);
        Put(incoming, "<SequenceNumber>k__BackingField", seq);
        Put(incoming, "<FlagBits>k__BackingField", flags);
        Put(incoming, "_ack", (flags & 16) != 0);
        Put(incoming, "_rst", (flags & 4) != 0);
        Put(incoming, "_psh", (flags & 8) != 0);
        Put(incoming, "_fin", (flags & 1) != 0);
        type.GetMethod(method.Name, members)!.Invoke(client, new[] { incoming });
    }
    int AckCount() => (int)type.GetField(sends.Name)!.GetValue(client)!;
    int Bytes() => (int)type.GetMethod("ZenithReceiveCount", members)!.Invoke(client, null)!;
    var window = new byte[32768];
    for (int i = 0; i < window.Length; i += 1460)
        Deliver((uint)(100 + i), 0x10, window.AsSpan(i, Math.Min(1460, window.Length - i)).ToArray());
    uint reopened = 100 + 32768;
    Require(Value("RcvNxt") == reopened && Value("RcvWnd") == 0 && Bytes() == 32768, "full receive window ACK");
    type.GetMethod("AdvanceDataOffset", members)!.Invoke(client, new object[] { 32768 });
    Require(Value("RcvNxt") == reopened && Value("RcvWnd") == 32768 && Bytes() == 0, "application window update");
    var retransmission = new byte[1460];
    new Random(54321).NextBytes(retransmission);
    Deliver(reopened, 0x10, retransmission[..1]);
    Require(Value("RcvNxt") == reopened + 1 && Bytes() == 1, "one-byte window probe accepted");
    Deliver(reopened, 0x10, retransmission);
    Require(Value("RcvNxt") == reopened + 1460 && Bytes() == 1460, "probe overlap must ACK only unseen suffix");
    Deliver(reopened + 1460, 0x10, retransmission);
    Require(Value("RcvNxt") == reopened + 2920 && Bytes() == 2920, "no gap after probe retransmission");
    type.GetMethod("AdvanceDataOffset", members)!.Invoke(client, new object[] { 2920 });
    // Start the following independent segment sequence on the same now-empty socket.
    State("RcvNxt", 100);
    type.GetField(sends.Name)!.SetValue(client, 0);
    Deliver(104, 0x10, "efgh"u8.ToArray());
    Require(Value("RcvNxt") == 100 && Value("RcvWnd") == 32768 && Bytes() == 0 && AckCount() == 1, "gap ACK");
    Deliver(100, 0x10, "abcd"u8.ToArray());
    Require(Value("RcvNxt") == 108 && Value("RcvWnd") == 32760 && Bytes() == 8 && AckCount() == 2, "cumulative gap-fill ACK");
    Deliver(104, 0x10, "efgh"u8.ToArray());
    Require(Value("RcvNxt") == 108 && Bytes() == 8 && AckCount() == 3, "duplicate ACK without duplicate bytes");
    Deliver(120, 0x14, Array.Empty<byte>());
    Require(Value("RcvNxt") == 108 && Bytes() == 8 && AckCount() == 3, "rejected reset must skip ACK");
    Deliver(112, 0x18, "mnop"u8.ToArray());
    Deliver(106, 0x18, "zzijkl"u8.ToArray());
    Require(Value("RcvNxt") == 116 && Bytes() == 16 && AckCount() == 5, "partial retransmission fills the left gap");
    Deliver(119, 0x11, "t"u8.ToArray());
    Require(Value("RcvNxt") == 116 && Bytes() == 16 && AckCount() == 6, "out-of-order FIN must wait");
    Deliver(118, 0x18, "stextra"u8.ToArray());
    Deliver(116, 0x19, "qrst"u8.ToArray());
    Require(Value("RcvNxt") == 121 && Bytes() == 20 && AckCount() == 9, "in-order PSH+FIN stops at the stream boundary");
    Require(Convert.ToInt32(statusField.GetValue(client)) == 8, "pinned FIN transition");
}
