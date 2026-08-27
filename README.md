# NyxilumDb

*[Українською](README.uk.md)*

A lightweight embedded key-value database in C#/.NET with a Write-Ahead Log (WAL)
for durability — inspired by the internals of Redis/SQLite. One of three
"from-scratch projects" suggested by an AI review of the GitHub profile
(the other two: [nyxilum-mcp](https://github.com/Faneraiy14/NyxilumMcp) — already
done; a custom HTTP server — still in the backlog).

## Why not just a `Dictionary` with `File.WriteAllText`

Because then a process crash mid-write means lost/corrupted data. The WAL
model: every `Set`/`Delete` is first appended (and `fsync`'d if needed) to
the log, and only THEN applied to memory. On reopen the log is replayed —
even if the last entry was cut off exactly mid-write to disk, everything
BEFORE the cutoff remains valid.

## Quick start

```csharp
using var db = NyxilumDb.NyxilumDb.Open("./mydata");

db.Set("greeting", Encoding.UTF8.GetBytes("Привіт!"));
var value = db.Get("greeting"); // byte[]?

foreach (var (key, val) in db.Scan("user:"))
    Console.WriteLine($"{key} = {Encoding.UTF8.GetString(val)}");

db.Delete("greeting");
```

Full example — [samples/NyxilumDb.Demo](samples/NyxilumDb.Demo/Program.cs).

## Durability model

- `FsyncMode.Always` (default) — `FlushFileBuffers` after EVERY write.
  The only mode where "the call returned" actually means "will survive
  a power loss." Slower (real disk I/O on every write).
- `FsyncMode.OsBuffered` — relies on the OS buffer. Survives a process
  crash, but not a power loss within the few seconds before the actual
  disk write. Significantly faster — good when throughput matters more
  than a "power was cut" level of guarantee.

## Compaction (checkpoint)

A WAL that grows forever isn't an option for anything beyond a one-off
demo. Once `CheckpointThresholdBytes` is exceeded (4 MB by default), the
current in-memory state is written to a snapshot (`.snap`), and the WAL
is truncated back to its header. A snapshot without a valid footer
(checked via checksum) is ignored entirely on open — so a half-written
snapshot after a crash mid-compaction is harmless: `Set`/`Delete` are
idempotent (the last write for a key wins), so replaying already-applied
WAL entries after a failed compaction doesn't change the result.

## Concurrency model

One `ReaderWriterLockSlim` for the whole store: one writer at a time,
any number of readers in parallel. The WAL write happens INSIDE the
write lock — the log's write order always matches the order entries are
applied to memory.

## v1 limitations

- The whole dataset must fit in memory (`SortedDictionary`, not an
  on-disk B-tree) — a deliberate choice for the first version: an
  on-disk index wouldn't add anything while everything's already in RAM.
- `Scan`/`Range` are an O(n) pass, not an optimized on-disk range query.
- One process per database (the WAL file is exclusive to the writer).

## Tests

```bash
dotnet run --project tests/NyxilumDb.Tests
```

24 framework-free checks (like `run_all.sh` in NyxilumLang): process
restart with data surviving Close/reopen; truncated/corrupted WAL
(cut off at 1/4/9 bytes, a corrupted byte inside an entry, trailing
garbage) — verifying replay correctly discards ONLY the tail; a real
crash of a child process (`Process.Kill()`, not a handled exception)
checking that every confirmed write survived; compaction (snapshot
gets created, WAL shrinks, data intact after reopen); concurrent
readers/writers (4 writer threads + a reader, no deadlock or
corruption); a fuzz test against a `Dictionary` oracle with periodic
reopens.

## License

MIT — Faneraiy14.
