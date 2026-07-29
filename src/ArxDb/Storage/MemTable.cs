namespace ArxDb.Storage;

// SortedDictionary — уже збалансоване дерево (червоно-чорне), дає
// O(log n) точкові операції І впорядкований обхід для range/prefix
// сканів безкоштовно. Повноцінне B-Tree на диску для v1 нічого не
// додало б: весь набір даних однаково мусить влізти в пам'ять (тут
// немає підкачки сторінок з диска), тож справжня цінність B-Tree —
// саме дискова частина — тут не застосовна. Цей клас — навмисний шов
// для заміни на реальний дисковий індекс у v2.
public sealed class MemTable
{
    private readonly SortedDictionary<string, byte[]> _data = new(StringComparer.Ordinal);

    public int Count => _data.Count;

    public bool TryGet(string key, out byte[] value) => _data.TryGetValue(key, out value!);

    public bool ContainsKey(string key) => _data.ContainsKey(key);

    public void Set(string key, byte[] value) => _data[key] = value;

    public bool Delete(string key) => _data.Remove(key);

    // Матеріалізуємо список одразу під замком виклику (не лінивий
    // ітератор): лінивий yield, що тримає замок під час виконання
    // коду користувача між ітераціями, — це дедлок, що чекає на слушний
    // момент. Простий O(n) прохід — навмисно без "розумного" раннього
    // виходу по діапазону: SortedDictionary впорядкований за Ordinal,
    // тож коректний ранній вихід можливий, але легко помилитись на
    // межових випадках префіксів; для v1 однозначна коректність
    // важливіша за O(log n + k).
    public List<KeyValuePair<string, byte[]>> Scan(string? prefix)
    {
        var result = new List<KeyValuePair<string, byte[]>>();
        foreach (var kv in _data)
        {
            if (prefix == null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                result.Add(kv);
        }
        return result;
    }

    public List<KeyValuePair<string, byte[]>> Range(string startInclusive, string endExclusive)
    {
        var result = new List<KeyValuePair<string, byte[]>>();
        foreach (var kv in _data)
        {
            if (string.CompareOrdinal(kv.Key, startInclusive) < 0) continue;
            if (string.CompareOrdinal(kv.Key, endExclusive) >= 0) break;
            result.Add(kv);
        }
        return result;
    }

    public IReadOnlyList<string> Keys(string? prefix)
    {
        if (prefix == null) return _data.Keys.ToList();
        return _data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    public IEnumerable<KeyValuePair<string, byte[]>> All() => _data;
}
