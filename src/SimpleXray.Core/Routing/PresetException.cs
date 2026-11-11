namespace SimpleXray.Core.Routing;

/// <summary>Why a preset could not be loaded, saved or imported. The UI maps codes to <c>PresetError_&lt;Code&gt;</c>.</summary>
public enum PresetError
{
    /// <summary>Not valid preset JSON.</summary>
    Malformed,

    /// <summary>The file is larger than <see cref="PresetSerializer.MaxSizeBytes"/>.</summary>
    TooLarge,

    /// <summary>Written by a newer version of the app.</summary>
    UnsupportedVersion,

    MissingName,

    /// <summary>A rule has no conditions (it would match all traffic) or an empty entry.</summary>
    EmptyRule,

    InvalidDomain,

    InvalidIp,

    InvalidPort,

    InvalidNetwork,

    InvalidProtocol,

    InvalidDomainStrategy,

    /// <summary>Built-in presets cannot be changed or deleted.</summary>
    BuiltInReadOnly,

    NotFound,

    InvalidUrl,

    DownloadFailed,
}

public sealed class PresetException : Exception
{
    public PresetException(PresetError error, string? detail = null)
        : base(detail is null ? error.ToString() : $"{error}: {detail}")
    {
        Error = error;
        Detail = detail;
    }

    public PresetException()
        : this(PresetError.Malformed)
    {
    }

    public PresetException(string message)
        : this(PresetError.Malformed, message)
    {
    }

    public PresetException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = PresetError.Malformed;
        Detail = message;
    }

    public PresetError Error { get; }

    /// <summary>Offending value or rule number, for the editor and the log.</summary>
    public string? Detail { get; }

    /// <summary>1-based index of the offending rule, when known.</summary>
    public int? RuleNumber { get; init; }
}
