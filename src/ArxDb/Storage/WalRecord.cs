using System.Buffers.Binary;
using System.Text;
using ArxDb;

namespace ArxDb.Storage;

public enum WalOp : byte
{
    Set = 1,
    Delete = 2,
}

// Один запис WAL на диску:
//   [payloadLen: u32][crc32c: u32][payload...]
// payload:
//   [op: u8][reserved: u8][keyLen: u32][key bytes][valLen: u32][value bytes]
// Для Delete valLen=0 і value bytes відсутні.
//
// crc32c рахується ЛИШЕ над payload — так само, як довжина йде окремо
// від контрольної суми, це дає WalReader спосіб відрізнити "останній
// запис обірваний посеред диска" (не вистачає байтів на заявлену
// payloadLen) від "запис повний, але дані пошкоджені" (crc не збігся).
public readonly struct WalRecord
{
    public required WalOp Op { get; init; }
    public required string Key { get; init; }
    public byte[]? Value { get; init; }

    public const int FrameHeaderSize = 8; // payloadLen(4) + crc32(4)

    public byte[] EncodePayload()
    {
        var keyBytes = Encoding.UTF8.GetBytes(Key);
        var valBytes = Value ?? [];
        var payload = new byte[2 + 4 + keyBytes.Length + 4 + valBytes.Length];
        int pos = 0;
        payload[pos++] = (byte)Op;
        payload[pos++] = 0; // reserved
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)keyBytes.Length);
        pos += 4;
        keyBytes.CopyTo(payload.AsSpan(pos));
        pos += keyBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)valBytes.Length);
        pos += 4;
        valBytes.CopyTo(payload.AsSpan(pos));
        return payload;
    }

    public static WalRecord DecodePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 + 4)
            throw new ArxDbCorruptedException("WAL payload закороткий для заголовка запису");

        int pos = 0;
        var op = (WalOp)payload[pos++];
        pos++; // reserved
        uint keyLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
        pos += 4;
        if (pos + keyLen + 4 > (uint)payload.Length)
            throw new ArxDbCorruptedException("WAL payload закороткий для ключа");
        var key = Encoding.UTF8.GetString(payload.Slice(pos, (int)keyLen));
        pos += (int)keyLen;
        uint valLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(pos));
        pos += 4;
        if (pos + valLen > (uint)payload.Length)
            throw new ArxDbCorruptedException("WAL payload закороткий для значення");
        byte[]? value = valLen > 0 ? payload.Slice(pos, (int)valLen).ToArray() : (op == WalOp.Set ? [] : null);

        return new WalRecord { Op = op, Key = key, Value = value };
    }
}
