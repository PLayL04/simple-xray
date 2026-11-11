using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace SimpleVpn.Core.Qr;

/// <summary>Uncompressed 32-bit image (BGRA or RGBA: QR decoding only needs luminance).</summary>
public sealed record RawImage(byte[] Pixels, int Width, int Height)
{
    /// <summary>Upper bound for decoded images (a 8K screen is ~33 M pixels).</summary>
    public const int MaxPixels = 40_000_000;
}

/// <summary>QR module grid without the quiet zone; <c>this[x, y]</c> is true for a dark module.</summary>
public sealed class QrMatrix
{
    private readonly bool[] _modules;

    internal QrMatrix(int size, bool[] modules)
    {
        Size = size;
        _modules = modules;
    }

    public int Size { get; }

    public bool this[int x, int y] => _modules[(y * Size) + x];
}

/// <summary>QR encoding (to show a server link) and decoding (from a picture or a screenshot). ZXing.Net, no UI.</summary>
public static class QrCodec
{
    public static QrMatrix Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.MARGIN] = 0,
        };
        var bits = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
        var size = bits.Width;
        var modules = new bool[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[(y * size) + x] = bits[x, y];
            }
        }

        return new QrMatrix(size, modules);
    }

    /// <summary>Texts of all QR codes found in the image (a screenshot may hold several).</summary>
    public static IReadOnlyList<string> DecodeAll(RawImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > RawImage.MaxPixels
            || image.Pixels.Length < image.Width * image.Height * 4)
        {
            return [];
        }

        var source = new RGBLuminanceSource(image.Pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        };

        var found = new QRCodeMultiReader().decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints);
        if (found is { Length: > 0 })
        {
            return [.. found.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal)];
        }

        // The multi-reader misses some single, tightly cropped codes.
        var single = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints);
        return single?.Text is { Length: > 0 } text ? [text] : [];
    }
}
