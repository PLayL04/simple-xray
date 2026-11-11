namespace SimpleVpn.Core.Models;

/// <summary>On-disk format of profiles.json.</summary>
public sealed record ProfilesDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid? SelectedId { get; init; }

    public IReadOnlyList<ServerProfile> Profiles { get; init; } = [];
}
