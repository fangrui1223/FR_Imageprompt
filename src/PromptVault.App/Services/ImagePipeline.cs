using System.Security.Cryptography;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PromptVault.App.Services;

public static class ImagePipeline
{
    public static BitmapFrame DecodeFirstFrame(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("图片没有可读取的画面。");
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (CanFallbackToGdi(path, ex))
        {
            return BitmapFrame.Create(LoadWithGdi(path));
        }
    }

    public static string PixelHash(BitmapSource source)
    {
        BitmapSource normalized = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(normalized.PixelWidth * 4);
        var row = new byte[stride];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(normalized.PixelWidth));
        hash.AppendData(BitConverter.GetBytes(normalized.PixelHeight));
        for (var y = 0; y < normalized.PixelHeight; y++)
        {
            normalized.CopyPixels(new System.Windows.Int32Rect(0, y, normalized.PixelWidth, 1), row, stride, 0);
            hash.AppendData(row);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void SaveJpegThumbnail(BitmapSource source, string path, int maxPixels, int quality)
    {
        var scale = Math.Min(1d, maxPixels / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource output = source;
        if (scale < 1d)
        {
            output = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            output.Freeze();
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }

    public static void SaveClipboardPng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }

    public static BitmapSource LoadPreview(string path, int decodeWidth = 520)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (CanFallbackToGdi(path, ex))
        {
            var bitmap = LoadWithGdi(path, decodeWidth);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private static bool CanFallbackToGdi(string path, Exception ex)
    {
        var extension = Path.GetExtension(path);
        if (!new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        return ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException;
    }

    private static BitmapSource LoadWithGdi(string path, int decodeWidth = 0)
    {
        using var source = DrawingImage.FromFile(path);
        var width = source.Width;
        var height = source.Height;
        if (decodeWidth > 0 && width > decodeWidth)
        {
            height = Math.Max(1, (int)Math.Round(height * (decodeWidth / (double)width)));
            width = decodeWidth;
        }

        using var bitmap = new DrawingBitmap(width, height, DrawingPixelFormat.Format32bppPArgb);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            DrawingPixelFormat.Format32bppPArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[Math.Abs(stride) * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null, buffer, Math.Abs(stride));
            image.Freeze();
            return image;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
