using System.Buffers.Binary;
using System.Text;

namespace ArxDb.Storage;

// Знімок (checkpoint) поточного стану в пам'яті — після нього WAL
// можна безпечно обрізати до заголовка. Формат:
//   header: "ASNP"(4) + version u16 + reserved u16
//   N записів: [len u32][crc32 u32][keyLen u32][key][valLen u32][val]
//   footer:    [entryCount u64][crc32 over entryCount bytes]
//
// Знімок БЕЗ валідного футера повністю ігнорується при відкритті —
// саме так напівзаписаний .snap.tmp (збій живлення посеред запису
// знімка) стає нешкідливим: ArxDb.Open просто відкидає його і
// відновлюється зі старого знімка (якого щойно записаний .tmp ще не
// встиг замінити через File.Move) плюс WAL.
public static class SnapshotFile
{
    private static readonly byte[] MagicHeader = "ASNP"u8.ToArray();
    private const int HeaderSize = 8;
    private const int FooterSize = 12; // entryCount(8) + crc32(4)

    public static void Write(string path, IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        stream.Write(MagicHeader);
        Span<byte> versionAndReserved = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(versionAndReserved, 1);
        stream.Write(versionAndReserved);

        ulong count = 0;
        Span<byte> frameHeader = stackalloc byte[8];
        foreach (var (key, value) in entries)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var payload = new byte[4 + keyBytes.Length + 4 + value.Length];
            int pos = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)keyBytes.Length);
            pos += 4;
            keyBytes.CopyTo(payload.AsSpan(pos));
            pos += keyBytes.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(pos), (uint)value.Length);
            pos += 4;
            value.CopyTo(payload.AsSpan(pos));

            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader, (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.Slice(4), Crc32C.Compute(payload));
            stream.Write(frameHeader);
            stream.Write(payload);
            count++;
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, count);
        var footerCrc = Crc32C.Compute(footer.Slice(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(footer.Slice(8), footerCrc);
        stream.Write(footer);

        stream.Flush(true);
    }

    /// <returns>null, якщо файл не існує АБО не має валідного футера (трактується як "знімка нема").</returns>
    public static Dictionary<string, byte[]>? TryRead(string path)
    {
        if (!File.Exists(path)) return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < HeaderSize + FooterSize) return null;

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (header[0] != 'A' || header[1] != 'S' || header[2] != 'N' || header[3] != 'P') return null;

        // Спершу перевіряємо футер — якщо його немає чи він
        // пошкоджений, увесь файл трактуємо як "знімка нема" замість
        // спроби вгадати, скільки записів встигло дописатись.
        stream.Seek(-FooterSize, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[FooterSize];
        stream.ReadExactly(footer);
        ulong expectedCount = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        uint footerCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(8));
        if (Crc32C.Compute(footer.Slice(0, 8)) != footerCrc) return null;

        stream.Seek(HeaderSize, SeekOrigin.Begin);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var frameHeaderBuf = new byte[8];
        long entriesEnd = stream.Length - FooterSize;

        while (stream.Position < entriesEnd)
        {
            if (ReadFully(stream, frameHeaderBuf) < 8) return null;
            uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(frameHeaderBuf);
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameHeaderBuf.AsSpan(4));

            var payload = new byte[payloadLen];
            if (ReadFully(stream, payload) < payloadLen) return null;
            if (Crc32C.Compute(payload) != storedCrc) return null;

            int pos = 0;
            uint keyLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos));
            pos += 4;
            var key = Encoding.UTF8.GetString(payload, pos, (int)keyLen);
            pos += (int)keyLen;
            uint valLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(pos));
            pos += 4;
            var value = payload.AsSpan(pos, (int)valLen).ToArray();

            result[key] = value;
        }

        return (ulong)result.Count == expectedCount ? result : null;
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
