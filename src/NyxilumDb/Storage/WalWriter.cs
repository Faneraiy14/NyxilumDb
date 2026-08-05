using System.Buffers.Binary;
using System.Text;

namespace NyxilumDb.Storage;

// Пише WAL-файл: 8-байтний заголовок раз при створенні, далі кадри
// (payloadLen+crc32+payload) для кожного Set/Delete. FsyncMode.Always
// — Flush(true) (=FlushFileBuffers на Windows) після КОЖНОГО запису:
// це єдиний режим, де "виклик повернувся" реально означає "переживе
// зникнення живлення", а не лише "переживе падіння процесу" (те
// лишається у буфері ОС, який процес-crash не чіпає, а вимкнення
// живлення — так).
public sealed class WalWriter : IDisposable
{
    private static readonly byte[] MagicHeader = "AWAL"u8.ToArray();
    private const ushort FormatVersion = 1;
    public const int HeaderSize = 8; // "AWAL"(4) + version u16(2) + reserved u16(2)

    private readonly FileStream _stream;
    private readonly FsyncMode _fsyncMode;

    public string Path { get; }
    public long Length => _stream.Length;

    private WalWriter(FileStream stream, string path, FsyncMode fsyncMode)
    {
        _stream = stream;
        Path = path;
        _fsyncMode = fsyncMode;
    }

    public static WalWriter OpenForAppend(string path, FsyncMode fsyncMode)
    {
        bool isNew = !File.Exists(path);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var writer = new WalWriter(stream, path, fsyncMode);
        if (isNew || stream.Length == 0)
        {
            writer.WriteHeader();
        }
        stream.Seek(0, SeekOrigin.End);
        return writer;
    }

    private void WriteHeader()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(MagicHeader);
        Span<byte> versionAndReserved = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(versionAndReserved, FormatVersion);
        _stream.Write(versionAndReserved);
        _stream.Flush(true);
    }

    public void Append(WalRecord record)
    {
        var payload = record.EncodePayload();
        var crc = Crc32C.Compute(payload);

        Span<byte> frameHeader = stackalloc byte[WalRecord.FrameHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader, (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.Slice(4), crc);

        _stream.Write(frameHeader);
        _stream.Write(payload);

        if (_fsyncMode == FsyncMode.Always)
            _stream.Flush(true);
        else
            _stream.Flush(false);
    }

    // Використовується WalReader-ом після replay, щоб відкинути хвіст
    // після першого пошкодженого/обірваного запису — саме тому цей
    // метод приймає позицію ЗЗОВНІ, а не рахує сам.
    public void TruncateTo(long length)
    {
        _stream.SetLength(length);
        _stream.Seek(0, SeekOrigin.End);
        _stream.Flush(true);
    }

    // Компакція: після знімка WAL більше не потрібен — обрізаємо до
    // самого заголовка (не видаляємо файл, щоб не губити файловий
    // дескриптор/шлях, яким уже володіє цей writer).
    public void ResetToHeaderOnly()
    {
        _stream.SetLength(0);
        WriteHeader();
        _stream.Seek(0, SeekOrigin.End);
    }

    public void Flush() => _stream.Flush(true);

    public void Dispose() => _stream.Dispose();
}

public enum FsyncMode
{
    // Flush(true) після кожного запису — переживає втрату живлення.
    Always,
    // Покладається на буфер ОС — переживає падіння процесу (crash),
    // але НЕ втрату живлення протягом кількох секунд до запису на диск.
    OsBuffered,
}
