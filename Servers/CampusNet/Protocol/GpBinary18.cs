using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace CampusNet.Protocol;

/// <summary>
/// Photon GpBinary 1.8 - what the Photon Voice / newer client libraries speak (the PUN connections
/// use 1.6). Compact: counts are varints, ints are zig-zag varints, type codes are numeric.
/// Enough of it to run the name-server hop for the voice client and to hand it addresses.
/// </summary>
public static class GpBinary18
{
    // GpType
    private const byte TUnknown = 0, TBoolean = 2, TByte = 3, TShort = 4, TFloat = 5, TDouble = 6, TString = 7, TNull = 8,
        TCompressedInt = 9, TCompressedLong = 10, TInt1 = 11, TInt1_ = 12, TInt2 = 13, TInt2_ = 14, TL1 = 15, TL1_ = 16,
        TL2 = 17, TL2_ = 18, TCustom = 19, TDictionary = 20, THashtable = 21, TObjectArray = 23, TOperationRequest = 24,
        TOperationResponse = 25, TEventData = 26, TBooleanFalse = 27, TBooleanTrue = 28, TShortZero = 29, TIntZero = 30,
        TLongZero = 31, TFloatZero = 32, TDoubleZero = 33, TByteZero = 34, TArray = 64, TByteArray = 67, TCustomSlim = 128;

    public ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _b; public int Pos;
        public Reader(ReadOnlySpan<byte> b, int pos = 0) { _b = b; Pos = pos; }
        public byte U8() => _b[Pos++];
        public uint VarUInt() { uint v = 0; int shift = 0; while (true) { byte b = U8(); v |= (uint)(b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; } }
        public ulong VarULong() { ulong v = 0; int shift = 0; while (true) { byte b = U8(); v |= (ulong)(b & 0x7F) << shift; if ((b & 0x80) == 0) return v; shift += 7; } }
        public int ZigInt() { uint u = VarUInt(); return (int)(u >> 1) ^ -(int)(u & 1); }
        public long ZigLong() { ulong u = VarULong(); return (long)(u >> 1) ^ -(long)(u & 1); }
        public short I16() { var v = BinaryPrimitives.ReadInt16BigEndian(_b.Slice(Pos)); Pos += 2; return v; }
        public float F32() { var v = BinaryPrimitives.ReadSingleBigEndian(_b.Slice(Pos)); Pos += 4; return v; }
        public double F64() { var v = BinaryPrimitives.ReadDoubleBigEndian(_b.Slice(Pos)); Pos += 8; return v; }
        public byte[] Bytes(int n) { var v = _b.Slice(Pos, n).ToArray(); Pos += n; return v; }
        public string Str() { int n = (int)VarUInt(); var s = Encoding.UTF8.GetString(_b.Slice(Pos, n)); Pos += n; return s; }

        public object? Value() => Value(U8());
        public object? Value(byte t)
        {
            switch (t)
            {
                case TNull: return null;
                case TBoolean: return U8() != 0;
                case TBooleanFalse: return false;
                case TBooleanTrue: return true;
                case TByte: return U8();
                case TByteZero: return (byte)0;
                case TShort: return I16();
                case TShortZero: return (short)0;
                case TFloat: return F32();
                case TFloatZero: return 0f;
                case TDouble: return F64();
                case TDoubleZero: return 0d;
                case TString: return Str();
                case TCompressedInt: return ZigInt();
                case TIntZero: return 0;
                case TInt1: return (int)U8();
                case TInt1_: return -(int)U8();
                case TInt2: return (int)(ushort)I16();
                case TInt2_: return -(int)(ushort)I16();
                case TCompressedLong: return ZigLong();
                case TLongZero: return 0L;
                case TL1: return (long)U8();
                case TL1_: return -(long)U8();
                case TL2: return (long)(ushort)I16();
                case TL2_: return -(long)(ushort)I16();
                case TByteArray: { int n = (int)VarUInt(); return Bytes(n); }
                case THashtable:
                {
                    int n = (int)VarUInt(); var h = new Hashtable(n);
                    for (int i = 0; i < n; i++) { var k = Value(); var v = Value(); if (k != null) h[k] = v; }
                    return h;
                }
                case TDictionary:
                {
                    byte kt = U8(), vt = U8(); int n = (int)VarUInt();
                    var d = new TypedDictionary { KeyType = kt, ValueType = vt };
                    for (int i = 0; i < n; i++) { var k = kt == TUnknown ? Value() : Value(kt); var v = vt == TUnknown ? Value() : Value(vt); d.Entries.Add(new(k, v)); }
                    return d;
                }
                case TObjectArray: { int n = (int)VarUInt(); var a = new object?[n]; for (int i = 0; i < n; i++) a[i] = Value(); return a; }
                case TCustom: { byte code = U8(); int n = (int)VarUInt(); return new CustomValue(code, Bytes(n)); }
                default:
                    if ((t & TCustomSlim) != 0) { byte code = (byte)(t & 0x7F); int n = (int)VarUInt(); return new CustomValue(code, Bytes(n)); }
                    if ((t & TArray) != 0)
                    {
                        int n = (int)VarUInt(); byte et = (byte)(t & ~TArray);
                        if (et == TBoolean)
                        {
                            // Packed: 8 per byte, least significant bit first.
                            var a = new bool[n]; int i = 0;
                            while (i < n) { byte bits = U8(); for (int k = 0; k < 8 && i < n; k++, i++) a[i] = (bits & (1 << k)) != 0; }
                            return a;
                        }
                        if (et == TString) { var a = new string[n]; for (int i = 0; i < n; i++) a[i] = Str(); return a; }
                        if (et == TCompressedInt) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = ZigInt(); return a; }
                        var arr = new object?[n];
                        for (int i = 0; i < n; i++) arr[i] = Value(et);
                        return new TypedArray(et, arr);
                    }
                    throw new InvalidDataException($"GpBinary18: unknown type {t} at {Pos - 1}");
            }
        }

        public Dictionary<byte, object?> Params() => Params(null);

        public Dictionary<byte, object?> Params(Dictionary<byte, byte[]>? raw)
        {
            int n = U8(); var d = new Dictionary<byte, object?>(n);
            for (int i = 0; i < n; i++)
            {
                byte k = U8(); int start = Pos;
                d[k] = Value();
                if (raw != null) raw[k] = _b.Slice(start, Pos - start).ToArray();
            }
            return d;
        }
    }

    public static GpBinary16.OperationRequest ReadOperationRequest(ReadOnlySpan<byte> body)
    {
        var r = new Reader(body);
        var op = new GpBinary16.OperationRequest { Code = r.U8(), Raw = new() };
        op.Params = r.Params(op.Raw);
        return op;
    }

    public sealed class Writer
    {
        private byte[] _b = new byte[256]; public int Length;
        private void Ensure(int n) { if (Length + n > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, Length + n)); }
        public void U8(byte v) { Ensure(1); _b[Length++] = v; }
        public void Raw(ReadOnlySpan<byte> s) { Ensure(s.Length); s.CopyTo(_b.AsSpan(Length)); Length += s.Length; }
        public void VarUInt(uint v) { while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; } U8((byte)v); }
        public void I16(short v) { Ensure(2); BinaryPrimitives.WriteInt16BigEndian(_b.AsSpan(Length), v); Length += 2; }
        public void Str(string s) { var n = Encoding.UTF8.GetByteCount(s); VarUInt((uint)n); Ensure(n); Encoding.UTF8.GetBytes(s, _b.AsSpan(Length)); Length += n; }
        public byte[] ToArray() => _b.AsSpan(0, Length).ToArray();

        public void Value(object? v)
        {
            switch (v)
            {
                case null: U8(TNull); break;
                case bool o: U8(o ? TBooleanTrue : TBooleanFalse); break;
                case byte b: if (b == 0) U8(TByteZero); else { U8(TByte); U8(b); } break;
                case short k: if (k == 0) U8(TShortZero); else { U8(TShort); I16(k); } break;
                case int i:
                    if (i == 0) U8(TIntZero);
                    else { U8(TCompressedInt); uint z = (uint)((i << 1) ^ (i >> 31)); VarUInt(z); }
                    break;
                case long l:
                    if (l == 0) U8(TLongZero);
                    else { U8(TCompressedLong); ulong z = (ulong)((l << 1) ^ (l >> 63)); while (z >= 0x80) { U8((byte)(z | 0x80)); z >>= 7; } U8((byte)z); }
                    break;
                case float f: if (f == 0) U8(TFloatZero); else { U8(TFloat); Ensure(4); BinaryPrimitives.WriteSingleBigEndian(_b.AsSpan(Length), f); Length += 4; } break;
                case double d: if (d == 0) U8(TDoubleZero); else { U8(TDouble); Ensure(8); BinaryPrimitives.WriteDoubleBigEndian(_b.AsSpan(Length), d); Length += 8; } break;
                case string s: U8(TString); Str(s); break;
                case byte[] x: U8(TByteArray); VarUInt((uint)x.Length); Raw(x); break;
                case string[] a: U8(TArray | TString); VarUInt((uint)a.Length); foreach (var e in a) Str(e); break;
                case int[] n: U8(TArray | TCompressedInt); VarUInt((uint)n.Length); foreach (var e in n) { uint z = (uint)((e << 1) ^ (e >> 31)); VarUInt(z); } break;
                case Hashtable h: U8(THashtable); VarUInt((uint)h.Count); foreach (DictionaryEntry e in h) { Value(e.Key); Value(e.Value); } break;
                case object?[] z: U8(TObjectArray); VarUInt((uint)z.Length); foreach (var e in z) Value(e); break;
                case bool[] ba:
                {
                    U8(TArray | TBoolean); VarUInt((uint)ba.Length);
                    for (int i = 0; i < ba.Length; i += 8)
                    {
                        byte bits = 0;
                        for (int k = 0; k < 8 && i + k < ba.Length; k++) if (ba[i + k]) bits |= (byte)(1 << k);
                        U8(bits);
                    }
                    break;
                }
                case RawValue rv: Raw(rv.Bytes); break;
                case CustomValue c: U8(TCustom); U8(c.Code); VarUInt((uint)c.Data.Length); Raw(c.Data); break;
                default: throw new NotSupportedException($"GpBinary18: cannot serialize {v.GetType()}");
            }
        }

        public void Params(IReadOnlyDictionary<byte, object?> p)
        {
            U8((byte)p.Count);
            foreach (var e in p) { U8(e.Key); Value(e.Value); }
        }
    }

    public static byte[] OperationResponseMessage(byte opCode, short returnCode, string? debug, IReadOnlyDictionary<byte, object?> p, bool internalOp = false)
    {
        var w = new Writer();
        w.U8(GpBinary16.Magic); w.U8(internalOp ? GpBinary16.MsgInternalOperationResponse : GpBinary16.MsgOperationResponse);
        w.U8(opCode); w.I16(returnCode); w.Value(debug); w.Params(p);
        return w.ToArray();
    }

    public static byte[] EventMessage(byte eventCode, IReadOnlyDictionary<byte, object?> p)
    {
        var w = new Writer();
        w.U8(GpBinary16.Magic); w.U8(GpBinary16.MsgEvent);
        w.U8(eventCode); w.Params(p);
        return w.ToArray();
    }
}

/// <summary>An already-encoded 1.8 value (type byte included), written back out byte for byte. How
/// a Photon Chat message is relayed: whatever the sender serialised - custom types included -
/// reaches the subscribers exactly as sent, never decoded and re-encoded.</summary>
public sealed class RawValue
{
    public readonly byte[] Bytes;
    public RawValue(byte[] bytes) { Bytes = bytes; }
    public override string ToString() => $"raw[{Bytes.Length}]";
}
