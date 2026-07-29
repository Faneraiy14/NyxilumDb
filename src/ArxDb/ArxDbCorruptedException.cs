namespace ArxDb;

public sealed class ArxDbCorruptedException : Exception
{
    public ArxDbCorruptedException(string message) : base(message) { }
}
