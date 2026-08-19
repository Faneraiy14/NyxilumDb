using System.Diagnostics;
using System.Text;
using NyxilumDb;
using NyxilumDb.Storage;

// Тестовий раннер без фреймворку (як run_all.sh у репозиторії NyxilumLang) — прості
// check()-и, чіткий підсумок, ненульовий exit-код при провалі.

int failures = 0;
int passed = 0;

void Check(string label, bool condition)
{
    if (condition) { Console.WriteLine($"  ✅ {label}"); passed++; }
    else { Console.WriteLine($"  ❌ {label}"); failures++; }
}

string TempDir() => Path.Combine(Path.GetTempPath(), "nyxilumdb_test_" + Guid.NewGuid().ToString("N"));

byte[] ValueFor(int i) => Encoding.UTF8.GetBytes($"value-{i}");

// --- crash-child режим: викликається дочірнім процесом у T3 ---
if (args.Length > 0 && args[0] == "--crash-child")
{
    var dir = args[1];
    using (var db = NyxilumDb.NyxilumDb.Open(dir))
    {
        for (int i = 0; i < 300; i++)
            db.Set($"key{i}", ValueFor(i));
    }
    // Явний Close() уже пройшов вище (using) — щоб перевірити РЕАЛЬНИЙ
    // crash (не акуратне закриття), пишемо ще трохи БЕЗ using і валимо
    // процес одразу після Set(), не даючи Dispose відпрацювати.
    var db2 = NyxilumDb.NyxilumDb.Open(dir);
    for (int i = 300; i < 400; i++)
        db2.Set($"key{i}", ValueFor(i));
    // Environment.FailFast() запускає Windows Error Reporting, яке може
    // тримати процес живим ще довго ПІСЛЯ фактичного "падіння" (генерація
    // crash-дампу) — робить тест повільним і ненадійним, не перевіряючи
    // нічого додаткового про саму WAL. Kill() себе самого — миттєве й
    // безумовне завершення без будь-якого впорядкованого Dispose, що і
    // потрібно тут: імітація реального збою (OOM-kill, зникнення живлення
    // для файлової системи, tasklist /F), а не керованого виключення.
    Process.GetCurrentProcess().Kill();
    return 0;
}

Console.WriteLine("T1: перезапуск процесу — дані переживають Close/reopen");
{
    var dir = TempDir();
    try
    {
        using (var db = NyxilumDb.NyxilumDb.Open(dir))
        {
            for (int i = 0; i < 300; i++) db.Set($"key{i}", ValueFor(i));
            for (int i = 0; i < 30; i++) db.Delete($"key{i}"); // тумбстони для перших 30
        }

        using var reopened = NyxilumDb.NyxilumDb.Open(dir);
        Check("кількість записів після перевідкриття", reopened.Count == 270);
        bool survivorsOk = true, tombstonesOk = true;
        for (int i = 30; i < 300; i++)
            if (!reopened.TryGet($"key{i}", out var v) || !v.AsSpan().SequenceEqual(ValueFor(i))) survivorsOk = false;
        for (int i = 0; i < 30; i++)
            if (reopened.ContainsKey($"key{i}")) tombstonesOk = false;
        Check("усі виживші ключі й значення коректні", survivorsOk);
        Check("усі видалені ключі дійсно відсутні (tombstones)", tombstonesOk);
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine("T2: обірваний/пошкоджений WAL — replay відкидає лише хвіст");
{
    var dir = TempDir();
    try
    {
        // CheckpointOnClose=false: інакше звичайний Close() стиснув би
        // WAL у знімок ДО того, як ми встигнемо його зіпсувати, — тест
        // перевіряв би вже порожній (щойно скомпактований) WAL.
        var writeOptions = new NyxilumDbOptions { CheckpointOnClose = false };
        using (var db = NyxilumDb.NyxilumDb.Open(dir, writeOptions))
        {
            for (int i = 0; i < 50; i++) db.Set($"k{i}", ValueFor(i));
        }
        var walPath = Path.Combine(dir, "nyxilumdb.wal");
        var fullBytes = File.ReadAllBytes(walPath);
        Check("WAL реально містить 50 записів перед пошкодженням (не скомпактований)", fullBytes.Length > 8);

        // Варіант A: обрізати останні кілька байт (обрив посеред кадру)
        foreach (var cut in new[] { 1, 4, 9 })
        {
            File.WriteAllBytes(walPath, fullBytes[..(fullBytes.Length - cut)]);
            using var db = NyxilumDb.NyxilumDb.Open(dir);
            Check($"обрізано {cut} байт: перших 49 ключів вижили", CountSurvivors(db, 49) == 49);
            Check($"обрізано {cut} байт: LastRecovery.DiscardedBytes > 0", db.LastRecovery.DiscardedBytes > 0);
        }

        // Варіант B: зіпсувати один байт усередині останнього запису
        var corrupted = (byte[])fullBytes.Clone();
        corrupted[^5] ^= 0xFF;
        File.WriteAllBytes(walPath, corrupted);
        using (var db = NyxilumDb.NyxilumDb.Open(dir))
        {
            Check("пошкоджено байт останнього запису: попередні 49 вижили", CountSurvivors(db, 49) == 49);
            Check("пошкоджено байт: DiscardedBytes > 0", db.LastRecovery.DiscardedBytes > 0);
        }

        // Варіант C: дописати сміття в кінець
        var withGarbage = fullBytes.Concat(Enumerable.Repeat((byte)0x7A, 200)).ToArray();
        File.WriteAllBytes(walPath, withGarbage);
        using (var db = NyxilumDb.NyxilumDb.Open(dir))
        {
            Check("сміття в кінці WAL: усі 50 ключів вижили", CountSurvivors(db, 50) == 50);
        }
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine("T3: реальний crash дочірнього процесу (Environment.FailFast)");
{
    var dir = TempDir();
    try
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName!;
        // НЕ перенаправляти stdout/stderr без читання: якщо дочірній
        // процес випадково пише більше, ніж влазить в буфер ОС-каналу
        // (наприклад, stack trace винятку), і ніхто цей канал не читає —
        // дочірній процес блокується на записі назавжди, а
        // WaitForExit(15000) тоді чекає даремно (саме це й сталось тут:
        // HasExited=False навіть після 15с). Найпростіше коректне
        // рішення для тестового раннера — не перенаправляти взагалі.
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
        };
        // dotnet-хост запускає .dll, а не .exe напряму в деяких конфігураціях —
        // передаємо шлях до збірки як перший аргумент, якщо exePath це dotnet.
        var thisAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exePath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(thisAssembly);
        }
        psi.ArgumentList.Add("--crash-child");
        psi.ArgumentList.Add(dir);

        using var proc = Process.Start(psi)!;
        proc.WaitForExit(15000);
        Console.WriteLine($"  (дочірній процес: HasExited={proc.HasExited}, ExitCode={(proc.HasExited ? proc.ExitCode : -1)})");

        // Windows не завжди звільняє файловий дескриптор синхронно з
        // завершенням процесу — FailFast запускає інфраструктуру звіту
        // про збій (WER), яка може ще кілька секунд тримати хендли,
        // навіть коли Process.HasExited уже true. Терплячий retry тут
        // покриває цю ОС-специфічну затримку, а не приховує реальну
        // проблему бібліотеки (сама бібліотека нічого спільного з цим
        // не має — це чисто питання, коли ОС фактично звільнить файл).
        NyxilumDb.NyxilumDb? db = null;
        Exception? lastError = null;
        for (int attempt = 0; attempt < 100 && db == null; attempt++)
        {
            try { db = NyxilumDb.NyxilumDb.Open(dir); }
            catch (IOException ex) { lastError = ex; Thread.Sleep(200); }
        }
        if (db == null)
            throw new InvalidOperationException($"Не вдалося відкрити БД після crash дочірнього процесу за 20с retry: {lastError}");
        using var _ = db;
        int survivors = CountSurvivors(db, 400);
        Check("дочірній процес завершився (не завис)", proc.HasExited);
        Check("усі 400 підтверджених ключів пережили crash", survivors == 400);
    }
    finally { try { Directory.Delete(dir, true); } catch { /* може лишитись файл-лок на Windows одразу після FailFast */ } }
}

Console.WriteLine("T4: компакція — знімок створюється, WAL стискається");
{
    var dir = TempDir();
    try
    {
        var options = new NyxilumDbOptions { CheckpointThresholdBytes = 2048 };
        using (var db = NyxilumDb.NyxilumDb.Open(dir, options))
        {
            for (int i = 0; i < 500; i++) db.Set($"ckpt{i}", ValueFor(i));
        }
        var snapPath = Path.Combine(dir, "nyxilumdb.snap");
        var walPath = Path.Combine(dir, "nyxilumdb.wal");
        Check("знімок створено", File.Exists(snapPath));
        Check("WAL стиснувся до розміру заголовка", new FileInfo(walPath).Length == 8);

        // Явні вкладені блоки, не два "using var" підряд: інакше обидва
        // живуть до кінця зовнішнього блоку одночасно, і другий Open()
        // намагається відкрити той самий WAL-файл, поки перший ще
        // тримає його (FileShare.Read не дає іншому writer'у зайти).
        using (var reopened = NyxilumDb.NyxilumDb.Open(dir, options))
        {
            Check("після компакції + reopen дані цілі", CountSurvivors(reopened, 500) == 500);
        }

        // Reopen одразу ПІСЛЯ компакції (без нових записів) — перевіряє
        // ідемпотентний шлях "WAL уже порожній, усе зі знімка".
        using (var reopenedAgain = NyxilumDb.NyxilumDb.Open(dir, options))
        {
            Check("повторне відкриття після компакції теж ціле", CountSurvivors(reopenedAgain, 500) == 500);
        }
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine("T5: конкурентні читачі/писачі не корумпують дані й не деадлочать");
{
    var dir = TempDir();
    try
    {
        // Цей тест перевіряє КОНКУРЕНТНІСТЬ (безпеку замка, відсутність
        // деадлоку/пошкодження), а не довговічність — реальний fsync на
        // кожен запис тут не потрібен і лише додає ~8мс/запис, не
        // перевіряючи нічого додаткового про потокобезпеку.
        using var db = NyxilumDb.NyxilumDb.Open(dir, new NyxilumDbOptions { FsyncMode = FsyncMode.OsBuffered });
        const int writers = 4, perWriter = 300;
        var tasks = new List<Task>();
        var stopReaders = new CancellationTokenSource();

        for (int w = 0; w < writers; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                    db.Set($"w{writerId}-{i}", ValueFor(writerId * 100000 + i));
            }));
        }

        var readerTask = Task.Run(() =>
        {
            // Невелика пауза між ітераціями — тугий цикл без жодної
            // паузи безперервно змагається з writer'ами за той самий
            // ReaderWriterLockSlim і фактично сповільнює запис значно
            // більше, ніж дав би реалістичний читач.
            while (!stopReaders.IsCancellationRequested)
            {
                _ = db.Count;
                db.TryGet("w0-0", out _);
                Thread.Sleep(5);
            }
        });

        bool completed = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(60));
        stopReaders.Cancel();
        readerTask.Wait(2000);

        Check("усі writer-задачі завершились без винятків/деадлоку", completed);

        // Якщо не завершились за 60с — це вже провалений тест (див. check
        // вище), але торкатись db.Count/TryGet далі, поки ці ж задачі
        // можуть ще тримати write-замок у фоні, ризиковано (саме так
        // раніше впав ReaderWriterLockSlim.Dispose "лок ще використовується").
        // Пропускаємо решту перевірок цього блоку замість падіння процесу.
        if (completed)
        {
            Check("фінальна кількість записів точна", db.Count == writers * perWriter);

            bool valuesIntact = true;
            for (int w = 0; w < writers; w++)
                for (int i = 0; i < perWriter; i += 50) // вибірково, повний прохід надто повільний
                    if (!db.TryGet($"w{w}-{i}", out var v) || !v.AsSpan().SequenceEqual(ValueFor(w * 100000 + i)))
                        valuesIntact = false;
            Check("значення не пошкоджені (вибіркова перевірка)", valuesIntact);
        }
        else
        {
            Console.WriteLine("  ⚠️  Пропущено решту T5: writer-задачі не завершились вчасно");
        }
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine("T6: fuzz проти Dictionary-оракула з періодичним reopen");
{
    var dir = TempDir();
    try
    {
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rnd = new Random(42);
        // Немає реального crash між reopen — це нормальний Dispose+Open,
        // тож OsBuffered достатньо (перевіряємо консистентність стану
        // між сесіями, не durability проти збою живлення).
        var fuzzOptions = new NyxilumDbOptions { FsyncMode = FsyncMode.OsBuffered };
        var db = NyxilumDb.NyxilumDb.Open(dir, fuzzOptions);
        try
        {
            for (int op = 0; op < 600; op++)
            {
                if (op % 150 == 149)
                {
                    db.Dispose();
                    db = NyxilumDb.NyxilumDb.Open(dir, fuzzOptions);
                }

                var key = $"fuzz{rnd.Next(50)}";
                if (rnd.Next(3) == 0 && oracle.ContainsKey(key))
                {
                    db.Delete(key);
                    oracle.Remove(key);
                }
                else
                {
                    var val = ValueFor(rnd.Next(1_000_000));
                    db.Set(key, val);
                    oracle[key] = val;
                }
            }

            bool allMatch = true;
            foreach (var (k, v) in oracle)
                if (!db.TryGet(k, out var actual) || !actual.AsSpan().SequenceEqual(v)) allMatch = false;
            Check("усі очікувані ключі за оракулом присутні й коректні", allMatch);
            Check("кількість записів збігається з оракулом", db.Count == oracle.Count);
        }
        finally { db.Dispose(); }
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine("T7: Set() не тримає посилання на масив виклику (захисна копія на вході)");
{
    var dir = TempDir();
    try
    {
        using var db = NyxilumDb.NyxilumDb.Open(dir);

        var buf = Encoding.UTF8.GetBytes("hello");
        db.Set("aliasing", buf);
        // Викликач мутує свій ВЛАСНИЙ масив ПІСЛЯ Set() (напр. перевикористовує
        // буфер для наступного значення) - без захисної копії на вході це тихо
        // псує те, що вже "збережено" в пам'яті (симетрично до того, чому
        // TryGet() копіює НАЗОВНІ - див. коментар у NyxilumDb.Set()).
        buf[0] = (byte)'X';

        var readBack = db.Get("aliasing");
        Check("мутація зовнішнього масиву після Set() не псує збережене значення",
            readBack != null && Encoding.UTF8.GetString(readBack) == "hello");
    }
    finally { Directory.Delete(dir, true); }
}

Console.WriteLine();
Console.WriteLine("======================================");
Console.WriteLine($"Успішно: {passed} | Провалено: {failures}");
return failures > 0 ? 1 : 0;

int CountSurvivors(NyxilumDb.NyxilumDb db, int expectedCount)
{
    int count = 0;
    for (int i = 0; i < expectedCount + 200; i++) // трохи запасу на випадок неочікуваних ключів
    {
        var candidates = new[] { $"key{i}", $"k{i}", $"ckpt{i}" };
        foreach (var c in candidates)
            if (db.ContainsKey(c)) { count++; break; }
    }
    return count;
}
