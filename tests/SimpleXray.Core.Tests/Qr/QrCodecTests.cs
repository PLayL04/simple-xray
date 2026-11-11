using SimpleXray.Core.Qr;

namespace SimpleXray.Core.Tests.Qr;

public sealed class QrCodecTests
{
    /// <summary>Draws QR codes side by side on a white canvas (BGRA), like a screenshot.</summary>
    internal static RawImage Render(int scale, params QrMatrix[] codes)
    {
        const int quiet = 4;
        var cell = codes.Max(c => c.Size) + (2 * quiet);
        var width = cell * scale * codes.Length + 40;
        var height = cell * scale + 40;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            var left = 20 + (i * cell * scale) + (quiet * scale);
            var top = 20 + (quiet * scale);
            for (var y = 0; y < code.Size * scale; y++)
            {
                for (var x = 0; x < code.Size * scale; x++)
                {
                    if (code[x / scale, y / scale])
                    {
                        var o = (((top + y) * width) + left + x) * 4;
                        pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;
                    }
                }
            }
        }

        return new RawImage(pixels, width, height);
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsLink()
    {
        var image = Render(4, QrCodec.Encode(TestLinks.XhttpTls));

        Assert.Equal([TestLinks.XhttpTls], QrCodec.DecodeAll(image));
    }

    [Fact]
    public void Decode_FindsSeveralCodesOnOneScreen()
    {
        var image = Render(3, QrCodec.Encode(TestLinks.XhttpTls), QrCodec.Encode("https://example.com/"));

        var texts = QrCodec.DecodeAll(image);

        Assert.Contains(TestLinks.XhttpTls, texts);
        Assert.Contains("https://example.com/", texts);
    }

    [Fact]
    public void Decode_ImageWithoutCode_ReturnsEmpty()
    {
        var pixels = new byte[200 * 100 * 4];
        Array.Fill(pixels, (byte)255);

        Assert.Empty(QrCodec.DecodeAll(new RawImage(pixels, 200, 100)));
    }

    [Fact]
    public void Decode_InconsistentSize_ReturnsEmpty()
    {
        Assert.Empty(QrCodec.DecodeAll(new RawImage(new byte[10], 100, 100)));
    }

    [Fact]
    public void Encode_HasNoQuietZone()
    {
        var code = QrCodec.Encode("x");

        // Version 1 is 21 modules; the finder pattern starts at the very corner.
        Assert.Equal(21, code.Size);
        Assert.True(code[0, 0]);
    }
}
