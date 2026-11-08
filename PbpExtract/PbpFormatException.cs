namespace PbpExtract;

/// <summary>
/// The file is a PBP, but it is not a plain popstation image this tool can decompress.
/// </summary>
public sealed class PbpFormatException : Exception
{
    public PbpFormatException(string message) : base(message)
    {
    }

    public PbpFormatException(string message, Exception inner) : base(message, inner)
    {
    }

    public bool IsProtected { get; init; }
}
