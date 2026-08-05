using NyxilumDb.Storage;

namespace NyxilumDb;

public sealed class NyxilumDbOptions
{
    public FsyncMode FsyncMode { get; init; } = FsyncMode.Always;

    // Після цього розміру WAL — компакція (знімок + обрізання WAL).
    public long CheckpointThresholdBytes { get; init; } = 4 * 1024 * 1024;

    public int MaxKeyBytes { get; init; } = 1024;
    public int MaxValueBytes { get; init; } = 16 * 1024 * 1024;

    // За замовчуванням Close() компактує непорожній WAL — швидший
    // наступний Open() (нема чого реплеїти). Вимкнути варто хіба що
    // для діагностики/тестів, де потрібен доступ саме до "сирого" WAL
    // одразу після сесії запису, а не до вже стиснутого стану.
    public bool CheckpointOnClose { get; init; } = true;
}
