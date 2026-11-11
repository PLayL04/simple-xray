namespace SimpleXray.Core.Parsing;

public enum LinkParseError
{
    Empty,
    UnsupportedScheme,
    Malformed,
    MissingUserId,
    InvalidUserId,
    MissingAddress,
    InvalidPort,
    InvalidParameter,
}

/// <summary>Thrown for links that cannot be imported. <see cref="Error"/> is mapped to a localized message by the UI.</summary>
public sealed class LinkParseException : Exception
{
    public LinkParseException()
    {
    }

    public LinkParseException(string message)
        : base(message)
    {
    }

    public LinkParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LinkParseException(LinkParseError error, string message)
        : base(message)
    {
        Error = error;
    }

    public LinkParseError Error { get; }
}
