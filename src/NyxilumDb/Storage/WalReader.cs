using System.Buffers.Binary;
using NyxilumDb;

namespace NyxilumDb.Storage;

// Читає WAL і відтворює записи в порядку запису. Обрив посеред диска
// (збій живлення/crash саме під час запису кадру) МАЄ виявлятись і
// обрізатись, а не трактуватись як помилка всього файлу — WAL це
// впорядкований причинний ланцюжок: усе ДО обриву лишається дійсним,
// усе ПІСЛЯ обриву не можна довіряти (не тому що воно обов'язково
// зіпсоване, а тому що немає способу відрізнити "зіпсоване" від
// "випадково виглядає як валідний кадр" на пошкодженому хвості).
public static class WalReader
{
    private const int HeaderSize = WalWriter.HeaderSize;
    private const int MaxRecordBytes = 64 * 1024 * 1024;

    public sealed record ReplayResult(List<WalRecord> Records, long LastGoodOffset, long DiscardedBytes);

    public static ReplayResult Replay(string path)
    {
        if (!File.Exists(path))
            return new ReplayResult([], HeaderSize, 0);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long fileLength = stream.Length;

        if (fileLength < HeaderSize)
        {
            // Навіть заголовок не встиг дописатись — трактуємо як
            // порожній WAL, а не помилку: викликач (NyxilumDb.Open)
            // перезапише заголовок при наступному відкритті на запис.
            return new ReplayResult([], 0, fileLength);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (header[0] != 'A' || header[1] != 'W' || header[2] != 'A' || header[3] != 'L')
            throw new NyxilumDbCorruptedException($"WAL-файл має неправильну сигнатуру: {path}");

        var records = new List<WalRecord>();
        long goodOffset = HeaderSize;
        var frameHeader = new byte[WalRecord.FrameHeaderSize];

        while (true)
        {
            long frameStart = stream.Position;
            int read = ReadFully(stream, frameHeader);
            if (read < WalRecord.FrameHeaderSize)
            {
                // Чистий хвіст (0 байт) — нормальне завершення файлу.
                // Частковий хвіст (1..7 байт) — обрив рівно на межі
                // кадру, теж просто відкидаємо решту.
                break;
            }

            uint payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.AsSpan(4));

            if (payloadLen > MaxRecordBytes)
            {
                // Або сміття (не наш формат), або запис пошкоджений так,
                // що довжина стала абсурдною, — в обох випадках довіряти
                // подальшому вмісту не можна.
                stream.Position = frameStart;
                break;
            }

            var payload = new byte[payloadLen];
            int payloadRead = ReadFully(stream, payload);
            if (payloadRead < payloadLen)
            {
                // Обірваний запис посеред payload — саме той сценарій,
                // що імітує ручне обрізання файлу в тестах.
                stream.Position = frameStart;
                break;
            }

            uint actualCrc = Crc32C.Compute(payload);
            if (actualCrc != storedCrc)
            {
                stream.Position = frameStart;
                break;
            }

            WalRecord record;
            try
            {
                record = WalRecord.DecodePayload(payload);
            }
            catch (NyxilumDbCorruptedException)
            {
                stream.Position = frameStart;
                break;
            }

            records.Add(record);
            goodOffset = stream.Position;
        }

        long discarded = fileLength - goodOffset;
        return new ReplayResult(records, goodOffset, discarded);
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
