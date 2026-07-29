namespace ArxDb;

// Заповнюється при кожному Open() — навіть коли discardedBytes==0, це
// явний сигнал "recovery відбувся й нічого не знайшов", а не мовчазна
// відсутність інформації.
public sealed record RecoveryInfo(int RecordsReplayed, long DiscardedBytes, bool SnapshotLoaded);
