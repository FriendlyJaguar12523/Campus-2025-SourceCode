using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace CampusNet.Protocol;

/// <summary>
/// A value the client sent as a custom type ('c'): PUN registers Vector3/Quaternion/Vector2/Player
/// etc. under one-letter codes. The server never interprets them - it stores and re-emits the bytes.
/// </summary>
public sealed record CustomValue(byte Code, byte[] Data);

/// <summary>An array the client sent with an explicit element type ('y'): re-emitted the same way.</summary>
public sealed record TypedArray(byte ElemType, object?[] Items, byte CustomCode = 0);

/// <summary>A Dictionary sent with typed keys/values ('D'); key/value type 0 = per-entry typed.</summary>
public sealed class TypedDictionary
{
    public byte KeyType, ValueType;
    public readonly List<KeyValuePair<object?, object?>> Entries = new();
}

/// <summary>
/// Photon GpBinary 1.6 - the wire format PUN uses on every Realtime connection (verified from a live
/// capture, see docs/photon-wire-findings.md). Big-endian throughout. Hashtables are the workhorse:
/// keys are typed values (bytes for system props, strings for custom props).
/// </summary>
public static class GpBinary16
{
    public const byte Magic = 0xF3;

    // Message types (the byte after 0xF3; bit 0x80 = encrypted body).
    public const byte MsgInit = 0, MsgInitResponse = 1, MsgOperationRequest = 2, MsgOperationResponse = 3,
                      MsgEvent = 4, MsgInternalOperationRequest = 6, MsgInternalOperationResponse = 7,
                      MsgMessage = 8, MsgRawMessage = 9;
    public const byte EncryptedFlag = 0x80;

    // ---------------------------------------------------------------- reading

    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _b;
        public int Pos;
        public Reader(ReadOnlySpan<byte> b, int pos = 0) { _b = b; Pos = pos; }
        public int Remaining => _b.Length - Pos;
        public byte U8() => _b[Pos++];
        public short I16() { var v = BinaryPrimitives.ReadInt16BigEndian(_b.Slice(Pos)); Pos += 2; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32BigEndian(_b.Slice(Pos)); Pos += 4; return v; }
        public long I64() { var v = BinaryPrimitives.ReadInt64BigEndian(_b.Slice(Pos)); Pos += 8; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleBigEndian(_b.Slice(Pos)); Pos += 4; return v; }
        public double F64() { var v = BinaryPrimitives.ReadDoubleBigEndian(_b.Slice(Pos)); Pos += 8; return v; }
        public byte[] Bytes(int n) { var v = _b.Slice(Pos, n).ToArray(); Pos += n; return v; }
        public string Str() { int n = I16(); var s = Encoding.UTF8.GetString(_b.Slice(Pos, n)); Pos += n; return s; }

        public object? Value() => Value(U8());

        public object? Value(byte t)
        {
            switch ((char)t)
            {
                case '*': return null;
                case 'b': return U8();
                case 'o': return U8() != 0;
                case 'k': return I16();
                case 'i': return I32();
                case 'l': return I64();
                case 'f': return F32();
                case 'd': return F64();
                case 's': return Str();
                case 'x': { int n = I32(); return Bytes(n); }
                case 'n': { int n = I32(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = I32(); return a; }
                case 'a': { int n = I16(); var a = new string[n]; for (int i = 0; i < n; i++) a[i] = Str(); return a; }
                case 'h':
                {
                    int n = I16(); var h = new Hashtable(n);
                    for (int i = 0; i < n; i++) { var k = Value(); var v = Value(); if (k != null) h[k] = v; }
                    return h;
                }
                case 'D':
                {
                    var d = new TypedDictionary { KeyType = U8(), ValueType = U8() };
                    int n = I16();
                    for (int i = 0; i < n; i++)
                    {
                        var k = d.KeyType == 0 ? Value() : Value(d.KeyType);
                        var v = d.ValueType == 0 ? Value() : Value(d.ValueType);
                        d.Entries.Add(new(k, v));
                    }
                    return d;
                }
                case 'z': { int n = I16(); var a = new object?[n]; for (int i = 0; i < n; i++) a[i] = Value(); return a; }
                case 'y':
                {
                    int n = I16(); byte et = U8();
                    if ((char)et == 'c')
                    {
                        byte code = U8(); var items = new object?[n];
                        for (int i = 0; i < n; i++) { int len = I16(); items[i] = new CustomValue(code, Bytes(len)); }
                        return new TypedArray(et, items, code);
                    }
                    var arr = new object?[n];
                    for (int i = 0; i < n; i++) arr[i] = Value(et);
                    return new TypedArray(et, arr);
                }
                case 'c': { byte code = U8(); int len = I16(); return new CustomValue(code, Bytes(len)); }
                default: throw new InvalidDataException($"GpBinary16: unknown type 0x{t:x2} at {Pos - 1}");
            }
        }

        public Dictionary<byte, object?> Params()
        {
            int n = I16(); var d = new Dictionary<byte, object?>(n);
            for (int i = 0; i < n; i++) { byte k = U8(); d[k] = Value(); }
            return d;
        }
    }

    public sealed class OperationRequest { public byte Code; public Dictionary<byte, object?> Params = new(); public int PayloadBytes; /* wire size of the whole message, for budgets */ public Dictionary<byte, byte[]>? Raw; /* 1.8 only: each param's encoded value, type byte included - relayed verbatim (Photon Chat messages) */ }
    public sealed class OperationResponse { public byte Code; public short ReturnCode; public string? Debug; public Dictionary<byte, object?> Params = new(); }
    public sealed class EventData { public byte Code; public Dictionary<byte, object?> Params = new(); }

    public static OperationRequest ReadOperationRequest(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        var op = new OperationRequest { Code = r.U8() };
        op.Params = r.Params();
        return op;
    }

    public static OperationResponse ReadOperationResponse(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        var op = new OperationResponse { Code = r.U8(), ReturnCode = r.I16() };
        op.Debug = r.Value() as string;
        op.Params = r.Params();
        return op;
    }

    public static EventData ReadEvent(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        var ev = new EventData { Code = r.U8() };
        ev.Params = r.Params();
        return ev;
    }

    // ---------------------------------------------------------------- writing

    public sealed class Writer
    {
        private byte[] _b = new byte[512];
        public int Length;
        public Writer(int cap = 512) { _b = new byte[cap]; }
        private void Ensure(int n) { if (Length + n > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, Length + n)); }
        public void U8(byte v) { Ensure(1); _b[Length++] = v; }
        public void I16(short v) { Ensure(2); BinaryPrimitives.WriteInt16BigEndian(_b.AsSpan(Length), v); Length += 2; }
        public void I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32BigEndian(_b.AsSpan(Length), v); Length += 4; }
        public void I64(long v) { Ensure(8); BinaryPrimitives.WriteInt64BigEndian(_b.AsSpan(Length), v); Length += 8; }
        public void F32(float v) { Ensure(4); BinaryPrimitives.WriteSingleBigEndian(_b.AsSpan(Length), v); Length += 4; }
        public void F64(double v) { Ensure(8); BinaryPrimitives.WriteDoubleBigEndian(_b.AsSpan(Length), v); Length += 8; }
        public void Raw(ReadOnlySpan<byte> s) { Ensure(s.Length); s.CopyTo(_b.AsSpan(Length)); Length += s.Length; }
        public void Str(string s) { var n = Encoding.UTF8.GetByteCount(s); I16((short)n); Ensure(n); Encoding.UTF8.GetBytes(s, _b.AsSpan(Length)); Length += n; }
        public byte[] ToArray() => _b.AsSpan(0, Length).ToArray();
        public ReadOnlySpan<byte> Span => _b.AsSpan(0, Length);

        /// <summary>Write a typed value (type byte + data).</summary>
        public void Value(object? v)
        {
            switch (v)
            {
                case null: U8((byte)'*'); break;
                case byte b: U8((byte)'b'); U8(b); break;
                case bool o: U8((byte)'o'); U8(o ? (byte)1 : (byte)0); break;
                case short k: U8((byte)'k'); I16(k); break;
                case int i: U8((byte)'i'); I32(i); break;
                case long l: U8((byte)'l'); I64(l); break;
                case float f: U8((byte)'f'); F32(f); break;
                case double d: U8((byte)'d'); F64(d); break;
                case string s: U8((byte)'s'); Str(s); break;
                case byte[] x: U8((byte)'x'); I32(x.Length); Raw(x); break;
                case int[] n: U8((byte)'n'); I32(n.Length); foreach (var e in n) I32(e); break;
                case string[] a: U8((byte)'a'); I16((short)a.Length); foreach (var e in a) Str(e); break;
                case Hashtable h:
                    U8((byte)'h'); I16((short)h.Count);
                    foreach (DictionaryEntry e in h) { Value(e.Key); Value(e.Value); }
                    break;
                case TypedDictionary td:
                    U8((byte)'D'); U8(td.KeyType); U8(td.ValueType); I16((short)td.Entries.Count);
                    foreach (var e in td.Entries)
                    {
                        if (td.KeyType == 0) Value(e.Key); else ValueBody(e.Key, td.KeyType);
                        if (td.ValueType == 0) Value(e.Value); else ValueBody(e.Value, td.ValueType);
                    }
                    break;
                case TypedArray ta:
                    U8((byte)'y'); I16((short)ta.Items.Length); U8(ta.ElemType);
                    if ((char)ta.ElemType == 'c')
                    {
                        U8(ta.CustomCode);
                        foreach (var e in ta.Items) { var c = (CustomValue)e!; I16((short)c.Data.Length); Raw(c.Data); }
                    }
                    else foreach (var e in ta.Items) ValueBody(e, ta.ElemType);
                    break;
                case object?[] z: U8((byte)'z'); I16((short)z.Length); foreach (var e in z) Value(e); break;
                case CustomValue c: U8((byte)'c'); U8(c.Code); I16((short)c.Data.Length); Raw(c.Data); break;
                case Dictionary<byte, object?> dbo:   // convenience: byte-keyed dictionary as Hashtable
                    U8((byte)'h'); I16((short)dbo.Count);
                    foreach (var e in dbo) { Value(e.Key); Value(e.Value); }
                    break;
                case Dictionary<int, object?> dio:    // actorNr -> props as Hashtable with int keys
                    U8((byte)'h'); I16((short)dio.Count);
                    foreach (var e in dio) { Value(e.Key); Value(e.Value); }
                    break;
                case Dictionary<object, object?> doo:
                    U8((byte)'h'); I16((short)doo.Count);
                    foreach (var e in doo) { Value(e.Key); Value(e.Value); }
                    break;
                default: throw new NotSupportedException($"GpBinary16: cannot serialize {v.GetType()}");
            }
        }

        /// <summary>Write only the data of a value whose type byte was already written (typed arrays/dictionaries).</summary>
        private void ValueBody(object? v, byte t)
        {
            // Re-use Value() and drop the leading type byte it wrote.
            int start = Length;
            Value(v);
            if (_b[start] != t)
            {
                // Element type mismatch (e.g. int in a 'k' array) - fix up the type byte position.
                throw new InvalidDataException($"GpBinary16: element type {(char)_b[start]} does not match array type {(char)t}");
            }
            Buffer.BlockCopy(_b, start + 1, _b, start, Length - start - 1);
            Length--;
        }

        public void Params(IReadOnlyDictionary<byte, object?> p)
        {
            I16((short)p.Count);
            foreach (var e in p) { U8(e.Key); Value(e.Value); }
        }
    }

    public static byte[] OperationResponseMessage(byte opCode, short returnCode, string? debug, IReadOnlyDictionary<byte, object?> p, bool internalOp = false)
    {
        var w = new Writer();
        w.U8(Magic); w.U8(internalOp ? MsgInternalOperationResponse : MsgOperationResponse);
        w.U8(opCode); w.I16(returnCode); w.Value(debug); w.Params(p);
        return w.ToArray();
    }

    public static byte[] EventMessage(byte eventCode, IReadOnlyDictionary<byte, object?> p)
    {
        var w = new Writer();
        w.U8(Magic); w.U8(MsgEvent);
        w.U8(eventCode); w.Params(p);
        return w.ToArray();
    }

    /// <summary>Body (after the 0xF3 + type header) of an operation response - what gets encrypted when a reply must be.</summary>
    public static byte[] OperationResponseBody(byte opCode, short returnCode, string? debug, IReadOnlyDictionary<byte, object?> p)
    {
        var w = new Writer();
        w.U8(opCode); w.I16(returnCode); w.Value(debug); w.Params(p);
        return w.ToArray();
    }

    public static byte[] InitResponse() => new byte[] { Magic, MsgInitResponse, 0 };

    public static string Describe(object? v, int depth = 0)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return $"\"{s}\"";
            case byte[] x: return $"byte[{x.Length}]";
            case int[] n: return "[" + string.Join(",", n) + "]";
            case string[] a: return "[" + string.Join(",", a.Select(s => "\"" + s + "\"")) + "]";
            case Hashtable h:
                if (depth > 2) return $"{{…{h.Count}}}";
                return "{" + string.Join(", ", h.Cast<DictionaryEntry>().Select(e => Describe(e.Key, depth + 1) + ":" + Describe(e.Value, depth + 1))) + "}";
            case TypedDictionary d: return $"D[{d.Entries.Count}]";
            case TypedArray ta: return $"{(char)ta.ElemType}[{ta.Items.Length}]";
            case object?[] z: return "(" + string.Join(", ", z.Select(e => Describe(e, depth + 1))) + ")";
            case CustomValue c: return $"custom'{(char)c.Code}'[{c.Data.Length}]";
            default: return v.ToString() ?? "?";
        }
    }
}
