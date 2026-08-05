namespace NyxilumDb;

public sealed class NyxilumDbCorruptedException : Exception
{
    public NyxilumDbCorruptedException(string message) : base(message) { }
}
