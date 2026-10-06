namespace AirplayFox.Protocol;

/// <summary>TLV8 encode/decode as used by HAP pairing (one level, byte values only).</summary>
public static class Tlv8
{
    public static Dictionary<int, byte[]> Read(byte[] data)
    {
        var result = new Dictionary<int, byte[]>();
        var pos = 0;
        while (pos < data.Length)
        {
            var tag = data[pos];
            var length = data[pos + 1];
            var value = data[(pos + 2)..(pos + 2 + length)];

            if (result.TryGetValue(tag, out var existing))
                result[tag] = existing.Concat(value).ToArray();
            else
                result[tag] = value;

            pos += 2 + length;
        }
        return result;
    }

    public static byte[] Write(IEnumerable<KeyValuePair<int, byte[]>> data)
    {
        using var output = new MemoryStream();
        foreach (var (tag, value) in data)
        {
            var pos = 0;
            var remaining = value.Length;
            while (pos < value.Length)
            {
                var size = Math.Min(remaining, 255);
                output.WriteByte((byte)tag);
                output.WriteByte((byte)size);
                output.Write(value, pos, size);
                pos += size;
                remaining -= size;
            }
        }
        return output.ToArray();
    }
}

public static class TlvValue
{
    public const int Method = 0x00;
    public const int Identifier = 0x01;
    public const int Salt = 0x02;
    public const int PublicKey = 0x03;
    public const int Proof = 0x04;
    public const int EncryptedData = 0x05;
    public const int SeqNo = 0x06;
    public const int Error = 0x07;
    public const int Flags = 0x13;
}
