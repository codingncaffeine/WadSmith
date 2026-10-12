namespace WadSmith.Core;

/// <summary>
/// A file that cannot be read as what it claims to be. The message is a plain sentence that can be
/// shown to a user as it is: what was wrong and where.
/// </summary>
public sealed class CorruptDataException : Exception
{
    public CorruptDataException(string message) : base(message)
    {
    }

    public CorruptDataException(string message, Exception inner) : base(message, inner)
    {
    }
}
