using System.Buffers.Binary;
using System.Text;

namespace HaloMeister.Core;

/// <summary>
/// Low-level primitives for the "BlamGlue" tagged-property serialization used by the
/// Halo campaign progression save blob.
///
/// Strings are Unreal-style FStrings: int32 byte-count (including the null terminator),
/// then the bytes. A negative count means UTF-16LE and the magnitude is the character
/// count including the null terminator.
/// </summary>
internal static class BlamPrimitives
{
    public static int ReadInt32(byte[] data, ref int pos)
    {
        Require(data, pos, 4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;
        return v;
    }

    public static byte ReadByte(byte[] data, ref int pos)
    {
        Require(data, pos, 1);
        return data[pos++];
    }

    public static string ReadString(byte[] data, ref int pos)
    {
        int start = pos;
        int count = ReadInt32(data, ref pos);
        if (count == 0) return string.Empty;

        if (count > 0)
        {
            Require(data, pos, count);
            // Trim the trailing null terminator.
            int len = count > 0 && data[pos + count - 1] == 0 ? count - 1 : count;
            string s = Encoding.UTF8.GetString(data, pos, len);
            pos += count;
            return s;
        }

        int chars = -count;
        int bytes = chars * 2;
        Require(data, pos, bytes);
        int byteLen = chars > 0 ? bytes - 2 : 0;
        string w = Encoding.Unicode.GetString(data, pos, byteLen);
        pos += bytes;
        return w;
    }

    public static void WriteInt32(List<byte> dst, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
        dst.AddRange(tmp.ToArray());
    }

    public static void WriteString(List<byte> dst, string value)
    {
        // Preserve the game's encoding rule: plain ASCII stays single-byte, anything
        // else is promoted to UTF-16 with a negative length, exactly like FString.
        bool ascii = true;
        foreach (char c in value)
        {
            if (c > 127) { ascii = false; break; }
        }

        if (ascii)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(dst, bytes.Length + 1);
            dst.AddRange(bytes);
            dst.Add(0);
        }
        else
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value);
            WriteInt32(dst, -(value.Length + 1));
            dst.AddRange(bytes);
            dst.Add(0);
            dst.Add(0);
        }
    }

    private static void Require(byte[] data, int pos, int need)
    {
        if (pos < 0 || pos + need > data.Length)
        {
            throw new BlamFormatException(
                $"Unexpected end of data at offset 0x{pos:X} (needed {need} more byte(s), " +
                $"buffer is {data.Length} bytes).", pos);
        }
    }
}

/// <summary>Thrown when the save blob does not match the expected layout.</summary>
public sealed class BlamFormatException : Exception
{
    public int Offset { get; }

    public BlamFormatException(string message, int offset = -1) : base(message) => Offset = offset;
}
