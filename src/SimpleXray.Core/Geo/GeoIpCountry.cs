using System.Net;
using System.Text;

namespace SimpleXray.Core.Geo;

/// <summary>
/// Country of an IP address from the local geoip.dat (protobuf <c>GeoIPList</c>: entries with
/// <c>country_code</c> = 1, repeated <c>CIDR {bytes ip = 1; uint32 prefix = 2}</c> = 2, <c>reverse_match</c> = 3).
/// Only two-letter country entries are read; category lists ("ru-blocked", "private") are skipped by length.
/// Streams the file once per lookup (tens of milliseconds), which is fine for one lookup per server.
/// </summary>
public static class GeoIpCountry
{
    /// <summary>Lower-case country code, or null when not found. Throws <see cref="InvalidDataException"/> for a malformed file.</summary>
    public static string? Find(string geoipPath, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var target = address.GetAddressBytes();
        using var stream = new FileStream(geoipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        var length = stream.Length;
        while (stream.Position < length)
        {
            if (ReadVarint(stream) != 0x0A)
            {
                throw new InvalidDataException("Unexpected top-level tag.");
            }

            var entryLength = (long)ReadVarint(stream);
            var entryEnd = stream.Position + entryLength;
            if (entryLength <= 0 || entryEnd > length)
            {
                throw new InvalidDataException("Entry length is out of range.");
            }

            var code = TryReadLeadingCode(stream, entryEnd);
            if (CountryFlags.IsCountryCode(code))
            {
                var body = new byte[entryEnd - stream.Position];
                stream.ReadExactly(body);
                if (Matches(body, target))
                {
                    return code!.ToLowerInvariant();
                }
            }

            stream.Position = entryEnd;
        }

        return null;
    }

    /// <summary>Reads field 1 when it is the first field of the entry (it always is in geoip.dat).</summary>
    private static string? TryReadLeadingCode(Stream stream, long entryEnd)
    {
        if (ReadVarint(stream) != 0x0A)
        {
            return null;
        }

        var size = (long)ReadVarint(stream);
        if (size is <= 0 or > 64 || stream.Position + size > entryEnd)
        {
            return null;
        }

        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>True when one of the entry's CIDRs contains the address and the entry is not reverse-matched.</summary>
    private static bool Matches(ReadOnlySpan<byte> body, byte[] target)
    {
        var matched = false;
        var pos = 0;
        while (pos < body.Length)
        {
            var tag = ReadVarint(body, ref pos);
            switch (tag)
            {
                case 0x12: // CIDR
                    var size = (int)ReadVarint(body, ref pos);
                    if (size < 0 || pos + size > body.Length)
                    {
                        throw new InvalidDataException("CIDR length is out of range.");
                    }

                    matched |= !matched && CidrContains(body.Slice(pos, size), target);
                    pos += size;
                    break;
                case 0x18: // reverse_match
                    if (ReadVarint(body, ref pos) != 0)
                    {
                        return false;
                    }

                    break;
                default:
                    SkipField(body, ref pos, tag);
                    break;
            }
        }

        return matched;
    }

    private static bool CidrContains(ReadOnlySpan<byte> cidr, byte[] target)
    {
        ReadOnlySpan<byte> ip = default;
        var prefix = -1;
        var pos = 0;
        while (pos < cidr.Length)
        {
            var tag = ReadVarint(cidr, ref pos);
            if (tag == 0x0A)
            {
                var size = (int)ReadVarint(cidr, ref pos);
                if (size < 0 || pos + size > cidr.Length)
                {
                    throw new InvalidDataException("IP length is out of range.");
                }

                ip = cidr.Slice(pos, size);
                pos += size;
            }
            else if (tag == 0x10)
            {
                prefix = (int)ReadVarint(cidr, ref pos);
            }
            else
            {
                SkipField(cidr, ref pos, tag);
            }
        }

        if (ip.Length != target.Length || prefix < 0 || prefix > ip.Length * 8)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        if (!ip[..fullBytes].SequenceEqual(target.AsSpan(0, fullBytes)))
        {
            return false;
        }

        var rest = prefix % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (ip[fullBytes] & mask) == (target[fullBytes] & mask);
    }

    private static void SkipField(ReadOnlySpan<byte> data, ref int pos, ulong tag)
    {
        switch (tag & 7)
        {
            case 0:
                ReadVarint(data, ref pos);
                break;
            case 1:
                pos += 8;
                break;
            case 2:
                pos += (int)ReadVarint(data, ref pos);
                break;
            case 5:
                pos += 4;
                break;
            default:
                throw new InvalidDataException($"Unsupported wire type in tag {tag}.");
        }

        if (pos > data.Length)
        {
            throw new InvalidDataException("Field is out of range.");
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length)
            {
                throw new InvalidDataException("Unexpected end of entry.");
            }

            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return result;
            }
        }

        throw new InvalidDataException("Varint is too long.");
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
