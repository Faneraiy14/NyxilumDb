namespace NyxilumDb.Storage;

// CRC-32C (Castagnoli, поліном 0x1EDC6F41, той самий, що в iSCSI/ext4/
// RocksDB) — потрібен, щоб виявити пошкоджений/обрізаний запис у WAL
// чи знімку (диск не гарантує атомарність запису одного блока при
// збої живлення посеред запису). Таблично-побітова реалізація,
// достатньо швидка для одного проходу на запис — не потребує
// апаратних інтринсиків для першої версії.
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78; // reversed 0x1EDC6F41
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
