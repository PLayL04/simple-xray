using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SimpleVpn.Core.Qr;

namespace SimpleVpn.Desktop.Controls;

/// <summary>Draws a <see cref="QrMatrix"/> black on white with a 4-module quiet zone, scaled to whole pixels.</summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<QrMatrix?> MatrixProperty =
        AvaloniaProperty.Register<QrCodeView, QrMatrix?>(nameof(Matrix));

    private const int QuietZone = 4;

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(MatrixProperty);
    }

    public QrMatrix? Matrix
    {
        get => GetValue(MatrixProperty);
        set => SetValue(MatrixProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.White, new Rect(0, 0, side, side));
        if (Matrix is not { } m || side <= 0)
        {
            return;
        }

        var total = m.Size + (2 * QuietZone);
        var module = Math.Max(1, Math.Floor(side / total));
        var offset = Math.Floor((side - (module * m.Size)) / 2);
        for (var y = 0; y < m.Size; y++)
        {
            for (var x = 0; x < m.Size; x++)
            {
                if (m[x, y])
                {
                    context.FillRectangle(Brushes.Black, new Rect(offset + (x * module), offset + (y * module), module, module));
                }
            }
        }
    }
}
