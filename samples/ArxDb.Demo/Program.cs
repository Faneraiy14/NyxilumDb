using System.Text;
using ArxDb;

var dir = Path.Combine(Path.GetTempPath(), "arxdb-demo");
Console.WriteLine($"Відкриваю базу в {dir}");

using (var db = ArxDb.ArxDb.Open(dir))
{
    Console.WriteLine($"Записів при відкритті: {db.Count} (recovery: {db.LastRecovery})");

    db.Set("greeting", Encoding.UTF8.GetBytes("Привіт, ArxDb!"));
    db.Set("user:1", Encoding.UTF8.GetBytes("Святослав"));
    db.Set("user:2", Encoding.UTF8.GetBytes("Аліна"));

    Console.WriteLine("greeting = " + Encoding.UTF8.GetString(db.Get("greeting")!));

    Console.WriteLine("Ключі з префіксом 'user:':");
    foreach (var (key, value) in db.Scan("user:"))
        Console.WriteLine($"  {key} = {Encoding.UTF8.GetString(value)}");

    db.Delete("user:2");
    Console.WriteLine($"Після видалення user:2, всього ключів: {db.Count}");
}

Console.WriteLine("Закрито (з компакцією, якщо WAL непорожній). Запусти ще раз — дані переживуть перезапуск.");
