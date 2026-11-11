namespace SimpleVpn.Core.Models;

/// <summary>On-disk format of profiles.json.</summary>
public sealed record ProfilesDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Guid? SelectedId { get; set; }

    public IReadOnlyList<ServerProfile> Profiles { get; set; } = [];
}
