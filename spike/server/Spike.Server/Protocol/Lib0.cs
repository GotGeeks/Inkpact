using System.Buffers;
using System.Text;

namespace Spike.Server.Protocol;

/// <summary>The invalid input that closes a socket with a policy violation.</summary>
public sealed class ProtocolException(string message) : Exception(message);

/// <summary>Writes lib0 encoding, the format y-protocols uses on the wire.</summary>
public sealed class Lib0Writer
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public void WriteVarUint(ulong value)
    {
        while (value > 0x7F)
        {
            WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        WriteByte((byte)value);
    }

    public void WriteVarString(string value) => WriteVarUint8Array(Encoding.UTF8.GetBytes(value));

    public void WriteVarUint8Array(ReadOnlySpan<byte> bytes)
    {
        WriteVarUint((ulong)bytes.Length);
        _buffer.Write(bytes);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    private void WriteByte(byte value)
    {
        _buffer.GetSpan(1)[0] = value;
        _buffer.Advance(1);
    }
}

/// <summary>Reads lib0 encoding; throws <see cref="ProtocolException"/> on malformed input.</summary>
public sealed class Lib0Reader(ReadOnlyMemory<byte> data)
{
    // lib0 numbers are JavaScript numbers, so anything over 2^53 is not valid input.
    private const int MaxVarUintBytes = 8;

    private int _position;

    public int Position => _position;

    public bool HasMore => _position < data.Length;

    public ulong ReadVarUint()
    {
        ulong value = 0;
        for (var i = 0; i < MaxVarUintBytes; i++)
        {
            var b = ReadByte();
            value |= (ulong)(b & 0x7F) << (7 * i);
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new ProtocolException("VarUint too long");
    }

    public string ReadVarString()
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(ReadVarUint8Array().Span);
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException("Invalid UTF-8 string");
        }
    }

    public ReadOnlyMemory<byte> ReadVarUint8Array()
    {
        var length = ReadVarUint();
        if (length > (ulong)(data.Length - _position))
        {
            throw new ProtocolException("Length past end of message");
        }

        var slice = data.Slice(_position, (int)length);
        _position += (int)length;
        return slice;
    }

    private byte ReadByte()
    {
        if (_position >= data.Length)
        {
            throw new ProtocolException("Unexpected end of message");
        }

        return data.Span[_position++];
    }
}
