namespace SimpleVpn.Core.Geo;

/// <summary>
/// Minimal reader for Xray geo files (protobuf <c>GeoIPList</c> / <c>GeoSiteList</c>): a sequence of
/// field-1 entries, each starting with its category name (<c>country_code</c>, field 1).
/// Used to validate a downloaded file and to check that the categories our presets use still exist.
/// Only the top level is walked (entries are skipped by length), so a 100 MB file takes milliseconds.
/// </summary>
public static class GeoDatFile
{
    /// <summary>Returns the category names (lower case). Throws <see cref="InvalidDataException"/> for a malformed file.</summary>
    public static HashSet<string> ReadCategories(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var length = stream.Length;

        while (stream.Position < length)
        {
            var tag = ReadVarint(stream);
            if (tag != 0x0A)
            {
                throw new InvalidDataException($"Unexpected top-level tag {tag} at {stream.Position}.");
            }

            var entryLength = (long)ReadVarint(stream);
            var entryEnd = stream.Position + entryLength;
            if (entryLength <= 0 || entryEnd > length)
            {
                throw new InvalidDataException("Entry length is out of range.");
            }

            result.Add(ReadCategoryName(stream, entryEnd));
            stream.Position = entryEnd;
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("The file has no categories.");
        }

        return result;
    }

    private static string ReadCategoryName(Stream stream, long entryEnd)
    {
        while (stream.Position < entryEnd)
        {
            var tag = ReadVarint(stream);
            var field = tag >> 3;
            switch (tag & 7)
            {
                case 0:
                    ReadVarint(stream);
                    break;
                case 2:
                    var size = (long)ReadVarint(stream);
                    if (size < 0 || stream.Position + size > entryEnd)
                    {
                        throw new InvalidDataException("Field length is out of range.");
                    }

                    if (field == 1)
                    {
                        if (size is 0 or > 256)
                        {
                            throw new InvalidDataException("Invalid category name.");
                        }

                        var bytes = new byte[size];
                        stream.ReadExactly(bytes);
                        return System.Text.Encoding.UTF8.GetString(bytes).ToLowerInvariant();
                    }

                    stream.Position += size;
                    break;
                case 1:
                    stream.Position += 8;
                    break;
                case 5:
                    stream.Position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported wire type in tag {tag}.");
            }
        }

        throw new InvalidDataException("Entry without a category name.");
    }

    private static ulong ReadVarint(Stream stream)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new InvalidDataException("Unexpected end of file.");
            }

            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new InvalidDataException("Varint is too long.");
    }
}
