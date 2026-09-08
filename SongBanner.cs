using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using QRCoder;
using SkiaSharp;

namespace LyricifyIsland;

internal static class SongBanner
{
    private const int OuterMargin = 40;
    private const int Padding = 64;
    private const int Inset = OuterMargin + Padding;

    private static double HeaderTop(bool portrait) =>
        portrait ? CoverBounds(true, 1080).Bottom + 48 : Inset;

    private static SKRect CoverBounds(bool portrait, int width) =>
        SKRect.Create(Inset, Inset, portrait ? width - 2 * Inset : 400, portrait ? width - 2 * Inset : 400);

    internal static int MeasureHeight(bool portrait, double textBottom, bool showBrand)
    {
        var coverBottom = CoverBounds(portrait, portrait ? 1080 : 1280).Bottom;
        var footerHeight = showBrand ? 30 : 0;
        var bottom = Math.Max(coverBottom, textBottom) + (footerHeight > 0 ? 32 + footerHeight : 0);
        return (int)Math.Ceiling((bottom + Inset) / 2) * 2;
    }

    internal static string? SpotifyUrl(string id, PlaybackSourcePreference source)
    {
        if (source != PlaybackSourcePreference.Spotify)
            return null;
        const string prefix = "spotify:track:";
        if (id.StartsWith(prefix, StringComparison.Ordinal))
            id = id[prefix.Length..];
        return id.Length == 22 && id.All(char.IsAsciiLetterOrDigit)
            ? $"https://open.spotify.com/track/{id}" : null;
    }

    internal static byte[] Render(TrackInfo track, LyricLine? line, bool portrait,
        bool showTranslation, string? spotifyUrl, bool showBrand = false, double scale = 2)
    {
        var width = portrait ? 1080 : 1280;
        var height = Content(null, track, line, portrait, showTranslation, spotifyUrl is not null, showBrand, out var qrSize);
        var artwork = Background(track, portrait, spotifyUrl, scale, width, height, qrSize);
        using var backgroundStream = new MemoryStream(artwork);
        using var background = new Bitmap(backgroundStream);
        using var target = new RenderTargetBitmap(new PixelSize((int)(width * scale), (int)(height * scale)),
            new Vector(96 * scale, 96 * scale));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(background, new Rect(0, 0, width, height));
            Content(context, track, line, portrait, showTranslation, spotifyUrl is not null, showBrand, out _);
        }
        using var output = new MemoryStream();
        target.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    private static int Content(DrawingContext? context, TrackInfo track, LyricLine? line,
        bool portrait, bool showTranslation, bool showQr, bool showBrand, out double qrSize)
    {
        var width = portrait ? 1080 : 1280;
        var cover = CoverBounds(portrait, width);
        var x = portrait ? Inset : (double)cover.Right + Padding;
        var textWidth = width - Inset - x;
        var y = HeaderTop(portrait);
        var titleSize = portrait ? 70 : 66;
        var artistSize = portrait ? 38 : 32;
        qrSize = 0;
        if (showQr)
        {
            // At most two title lines; shrinking its width can only add one line.
            for (var pass = 0; pass < 3; pass++)
            {
                var available = textWidth - qrSize - 32;
                qrSize = Text(null, track.Title, 0, 0, available, titleSize, "#FFF8EE", 2, bold: true)
                    + 20 + Text(null, string.Join(" / ", track.Artists), 0, 0, available, artistSize, "#F0E4D7");
            }
        }
        var metadataWidth = textWidth - (showQr ? qrSize + 32 : 0);
        y += Text(context, track.Title, x, y, metadataWidth, titleSize, "#FFF8EE", 2, bold: true) + 20;
        y += Text(context, string.Join(" / ", track.Artists), x, y,
            metadataWidth, artistSize, "#F0E4D7") + 10;
        y += Text(context, track.Album, x, y, metadataWidth, portrait ? 32 : 26, "#B8B4B1");
        if (!string.IsNullOrWhiteSpace(line?.Text))
        {
            y += 32;
            context?.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#35FFFFFF"))),
                new Point(x, y - 20), new Point(x + textWidth, y - 20));
            y += Text(context, line.Text, x, y, textWidth, portrait ? 40 : 34, "#EEE7DC", 2);
            if (showTranslation && !string.IsNullOrWhiteSpace(line.Translation))
            {
                y += 8;
                y += Text(context, line.Translation, x, y, textWidth,
                    portrait ? 30 : 24, "#B6B7B5", portrait ? 2 : 1);
            }
        }
        var height = MeasureHeight(portrait, y, showBrand);
        if (showBrand)
            Text(context, "Lyricify Island", x, height - Inset - (portrait ? 30 : 25),
                300, portrait ? 24 : 20, "#C3BFB7");
        return height;
    }

    private static double Text(DrawingContext? context, string text, double x, double y,
        double width, double size, string color, int lines = 1, bool bold = false)
    {
        using var layout = new TextLayout(text,
            new Typeface(new FontFamily("Noto Sans, Noto Sans CJK SC"),
                weight: bold ? FontWeight.SemiBold : FontWeight.Normal),
            size, new SolidColorBrush(Color.Parse(color)),
            textWrapping: TextWrapping.Wrap, textTrimming: TextTrimming.CharacterEllipsis,
            maxWidth: width, maxLines: lines, lineHeight: size * 1.25);
        if (context is not null)
            layout.Draw(context, new Point(x, y));
        return layout.Height;
    }

    private static byte[] Background(TrackInfo track, bool portrait, string? spotifyUrl, double scale, int width, int height, double qrSize)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)(width * scale), (int)(height * scale),
            SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        var card = new SKRect(OuterMargin, OuterMargin, width - OuterMargin, height - OuterMargin);
        using var rounded = new SKRoundRect(card, 30);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 105), IsAntialias = true };
        using var blur = SKImageFilter.CreateDropShadow(0, 0, 12, 12, new SKColor(0, 0, 0, 125));
        shadow.ImageFilter = blur;
        canvas.DrawRoundRect(rounded, shadow);
        canvas.Save();
        canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, true);
        canvas.Clear(new SKColor(28, 33, 37));
        using var cover = track.AlbumArtBytes.IsDefaultOrEmpty
            ? null : SKImage.FromEncodedData(track.AlbumArtBytes.AsSpan());
        if (cover is not null)
        {
            using var backgroundBlur = SKImageFilter.CreateBlur(55, 55);
            using var paint = new SKPaint { ImageFilter = backgroundBlur, IsAntialias = true };
            var expanded = SKRect.Inflate(card, 90, 90);
            DrawCover(canvas, cover, expanded, paint);
        }
        using (var shade = new SKPaint())
        {
            shade.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                [new SKColor(16, 18, 22, 150), new SKColor(12, 20, 25, 235)],
                null, SKShaderTileMode.Clamp);
            canvas.DrawRect(card, shade);
        }
        var coverRect = CoverBounds(portrait, width);
        using var coverRound = new SKRoundRect(coverRect, 14);
        canvas.DrawRoundRect(coverRound, shadow);
        canvas.Save();
        canvas.ClipRoundRect(coverRound, SKClipOperation.Intersect, true);
        using var coverPaint = new SKPaint { IsAntialias = true, Color = new SKColor(48, 56, 62) };
        canvas.DrawRect(coverRect, coverPaint);
        if (cover is not null)
            DrawCover(canvas, cover, coverRect, coverPaint);
        else
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Noto Sans"), coverRect.Width * .28f);
            coverPaint.Color = new SKColor(179, 185, 187);
            canvas.DrawText("♪", coverRect.MidX, coverRect.MidY + font.Size * .3f,
                SKTextAlign.Center, font, coverPaint);
        }
        canvas.Restore();
        if (spotifyUrl is not null)
        {
            using var data = QRCodeGenerator.GenerateQrCode(spotifyUrl, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix; // Includes the four-module quiet zone.
            var module = (float)(qrSize / matrix.Count);
            var x = width - Inset - matrix.Count * module;
            var y = (float)HeaderTop(portrait);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(x, y, x + matrix.Count * module, y + matrix.Count * module),
                module * 3, module * 3, paint);
            paint.Color = SKColors.Black;
            for (var row = 0; row < matrix.Count; row++)
                for (var column = 0; column < matrix.Count; column++)
                {
                    var inLeft = column >= 4 && column < 11;
                    var inTop = row >= 4 && row < 11;
                    var inRight = column >= matrix.Count - 11 && column < matrix.Count - 4;
                    var inBottom = row >= matrix.Count - 11 && row < matrix.Count - 4;
                    if (inLeft && (inTop || inBottom) || inRight && inTop)
                        continue;
                    if (matrix[row][column])
                        canvas.DrawRoundRect(SKRect.Create(x + column * module, y + row * module, module, module),
                            module * .2f, module * .2f, paint);
                }
            foreach (var (column, row) in new[] { (4, 4), (matrix.Count - 11, 4), (4, matrix.Count - 11) })
            {
                var finder = SKRect.Create(x + column * module, y + row * module, 7 * module, 7 * module);
                paint.Color = SKColors.Black;
                canvas.DrawRoundRect(finder, module * .5f, module * .5f, paint);
                finder.Inflate(-module, -module);
                paint.Color = SKColors.White;
                canvas.DrawRoundRect(finder, module * .5f, module * .5f, paint);
                finder.Inflate(-module, -module);
                paint.Color = SKColors.Black;
                canvas.DrawRoundRect(finder, module * .5f, module * .5f, paint);
            }
        }
        canvas.Restore();
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static void DrawCover(SKCanvas canvas, SKImage image, SKRect target, SKPaint paint)
    {
        var scale = Math.Max(target.Width / image.Width, target.Height / image.Height);
        var width = target.Width / scale;
        var height = target.Height / scale;
        var source = SKRect.Create((image.Width - width) / 2, (image.Height - height) / 2, width, height);
        canvas.DrawImage(image, source, target, new SKSamplingOptions(SKFilterMode.Linear), paint);
    }
}
