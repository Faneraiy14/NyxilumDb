using NyxilumDb.Storage;

namespace NyxilumDb;

// Embedded key-value сховище з WAL-довговічністю. Одна відкрита теку —
// одна база: усередині завжди рівно 3 можливих файли (nyxilumdb.snap,
// nyxilumdb.wal, nyxilumdb.snap.tmp — останній лише під час компакції).
//
// Модель конкурентності: один спільний ReaderWriterLockSlim на все
// сховище (single-writer / multi-reader). Запис WAL відбувається
// ВСЕРЕДИНІ write-замка, тому порядок запису в лог завжди збігається з
// порядком застосування до пам'яті — без цього два одночасних Set()
// могли б потрапити в лог в іншому порядку, ніж застосувались, і
// replay після падіння відновив би інший фінальний стан, ніж живий
// процес мав насправді.
public sealed class NyxilumDb : IDisposable
{
    private const string SnapshotFileName = "nyxilumdb.snap";
    private const string SnapshotTempFileName = "nyxilumdb.snap.tmp";
    private const string WalFileName = "nyxilumdb.wal";

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly MemTable _memTable;
    private readonly WalWriter _wal;
    private readonly NyxilumDbOptions _options;
    private readonly string _snapshotPath;
    private readonly string _snapshotTempPath;
    private bool _disposed;

    public RecoveryInfo LastRecovery { get; }

    private NyxilumDb(MemTable memTable, WalWriter wal, NyxilumDbOptions options, string snapshotPath, string snapshotTempPath, RecoveryInfo recovery)
    {
        _memTable = memTable;
        _wal = wal;
        _options = options;
        _snapshotPath = snapshotPath;
        _snapshotTempPath = snapshotTempPath;
        LastRecovery = recovery;
    }

    public static NyxilumDb Open(string directoryPath, NyxilumDbOptions? options = null)
    {
        options ??= new NyxilumDbOptions();
        Directory.CreateDirectory(directoryPath);

        var snapshotPath = Path.Combine(directoryPath, SnapshotFileName);
        var snapshotTempPath = Path.Combine(directoryPath, SnapshotTempFileName);
        var walPath = Path.Combine(directoryPath, WalFileName);

        var snapshot = SnapshotFile.TryRead(snapshotPath);
        var memTable = new MemTable();
        if (snapshot != null)
        {
            foreach (var (key, value) in snapshot)
                memTable.Set(key, value);
        }

        var replay = WalReader.Replay(walPath);
        foreach (var record in replay.Records)
        {
            if (record.Op == WalOp.Set)
                memTable.Set(record.Key, record.Value ?? []);
            else
                memTable.Delete(record.Key);
        }

        // КРИТИЧНО: якщо replay відкинув "хвіст" (обрив/пошкодження),
        // сам WAL-файл на диску досі містить цей хвіст. Якщо відкрити
        // WalWriter "як є" й почати дописувати, нові записи підуть
        // ПІСЛЯ пошкодженого хвоста — і наступний replay зупиниться на
        // ньому ще до того, як дійде до щойно дописаних валідних
        // записів. Обрізати файл до LastGoodOffset треба ДО відкриття
        // writer'а, а не покладатись на те, що writer сам це зробить.
        if (replay.DiscardedBytes > 0 && File.Exists(walPath))
        {
            using var truncateStream = new FileStream(walPath, FileMode.Open, FileAccess.Write, FileShare.None);
            truncateStream.SetLength(replay.LastGoodOffset);
            truncateStream.Flush(true);
        }

        var wal = WalWriter.OpenForAppend(walPath, options.FsyncMode);

        var recovery = new RecoveryInfo(replay.Records.Count, replay.DiscardedBytes, snapshot != null);
        var db = new NyxilumDb(memTable, wal, options, snapshotPath, snapshotTempPath, recovery);

        // Компакція вже на старті, якщо WAL і так завеликий (напр.
        // процес довго не закривався) — інакше перший-ліпший запис
        // одразу ж перевищить поріг і компактиться, роблячи Open
        // непередбачувано повільним лише через порядок операцій.
        if (wal.Length > options.CheckpointThresholdBytes)
            db.Checkpoint();

        return db;
    }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _memTable.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public bool TryGet(string key, out byte[] value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_memTable.TryGet(key, out var stored))
            {
                value = (byte[])stored.Clone(); // захисна копія — виклик не повинен мутувати внутрішній стан
                return true;
            }
            value = [];
            return false;
        }
        finally { _lock.ExitReadLock(); }
    }

    public byte[]? Get(string key) => TryGet(key, out var value) ? value : null;

    public bool ContainsKey(string key)
    {
        _lock.EnterReadLock();
        try { return _memTable.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    public void Set(string key, byte[] value)
    {
        ValidateKey(key);
        if (value.Length > _options.MaxValueBytes)
            throw new ArgumentException($"Значення завелике: {value.Length} байт, максимум {_options.MaxValueBytes}");

        // Захисна копія ДО збереження, дзеркально до TryGet() (та копіює
        // НАЗОВНІ) - без цього виклик, який мутує свій масив ПІСЛЯ Set()
        // (напр. перевикористовує буфер для наступного значення), тихо
        // псував би те, що вже "збережено" в пам'яті: MemTable.Set()
        // кладе саме це посилання, WAL-запис на диску лишається коректним
        // (записується одразу), але Get() після такої мутації повертав би
        // зіпсовані дані з пам'яті, що розходяться з тим, що на диску.
        var stored = (byte[])value.Clone();

        _lock.EnterWriteLock();
        try
        {
            _wal.Append(new WalRecord { Op = WalOp.Set, Key = key, Value = stored });
            _memTable.Set(key, stored);
            MaybeCheckpointLocked();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Delete(string key)
    {
        ValidateKey(key);
        _lock.EnterWriteLock();
        try
        {
            _wal.Append(new WalRecord { Op = WalOp.Delete, Key = key, Value = null });
            bool existed = _memTable.Delete(key);
            MaybeCheckpointLocked();
            return existed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public IReadOnlyList<string> Keys(string? prefix = null)
    {
        _lock.EnterReadLock();
        try { return _memTable.Keys(prefix); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<KeyValuePair<string, byte[]>> Scan(string prefix)
    {
        _lock.EnterReadLock();
        try { return _memTable.Scan(prefix); }
        finally { _lock.ExitReadLock(); }
    }

    public IReadOnlyList<KeyValuePair<string, byte[]>> Range(string startInclusive, string endExclusive)
    {
        _lock.EnterReadLock();
        try { return _memTable.Range(startInclusive, endExclusive); }
        finally { _lock.ExitReadLock(); }
    }

    private void MaybeCheckpointLocked()
    {
        if (_wal.Length > _options.CheckpointThresholdBytes)
            CheckpointLocked();
    }

    public void Checkpoint()
    {
        _lock.EnterWriteLock();
        try { CheckpointLocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    // Алгоритм компакції (виконується під write-замком):
    //   1. Пишемо ВЕСЬ поточний стан у .snap.tmp, fsync (всередині SnapshotFile.Write).
    //   2. Атомарно підміняємо .snap.tmp -> .snap (File.Move з overwrite).
    //   3. Обрізаємо WAL до самого заголовка.
    //
    // Аналіз падінь:
    //   - Падіння ДО кроку 2: старий .snap і повний WAL лишаються
    //     недоторканими — наче компакції й не було.
    //   - Падіння МІЖ кроком 2 і 3: WAL все ще містить записи, які вже
    //     є у щойно записаному знімку. Це НЕШКІДЛИВО, бо Set/Delete —
    //     ідемпотентні (останній запис за ключем перемагає): повторне
    //     застосування вже застосованого запису не змінює результат.
    //     Саме ця ідемпотентність — уся основа коректності алгоритму.
    private void CheckpointLocked()
    {
        SnapshotFile.Write(_snapshotTempPath, _memTable.All());
        File.Move(_snapshotTempPath, _snapshotPath, overwrite: true);
        _wal.ResetToHeaderOnly();
    }

    public void Flush()
    {
        _lock.EnterWriteLock();
        try { _wal.Flush(); }
        finally { _lock.ExitWriteLock(); }
    }

    public void Close()
    {
        if (_disposed) return;
        _lock.EnterWriteLock();
        try
        {
            if (_options.CheckpointOnClose && _wal.Length > WalWriter.HeaderSize) // більше за "пустий" (лише заголовок AWAL)
                CheckpointLocked();
            _wal.Dispose();
            _disposed = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void Dispose()
    {
        Close();
        _lock.Dispose();
    }

    private void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Ключ не може бути порожнім");
        if (System.Text.Encoding.UTF8.GetByteCount(key) > _options.MaxKeyBytes)
            throw new ArgumentException($"Ключ завеликий: максимум {_options.MaxKeyBytes} байт у UTF-8");
    }
}
