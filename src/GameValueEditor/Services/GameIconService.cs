using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class GameIconService
{
    private readonly string _iconsDirectory;

    public GameIconService(string iconsDirectory)
    {
        _iconsDirectory = iconsDirectory;
    }

    public string Save(GameProfile game, ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return game.IconFileName;
        Directory.CreateDirectory(_iconsDirectory);
        var fileName = $"{game.Id:N}.png";
        var path = Path.Combine(_iconsDirectory, fileName);
        var temporaryPath = path + ".tmp";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(temporaryPath)) encoder.Save(stream);
        File.Move(temporaryPath, path, true);
        game.IconFileName = fileName;
        game.IconSource = Load(fileName);
        return fileName;
    }

    public ImageSource? Load(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var path = Path.Combine(_iconsDirectory, Path.GetFileName(fileName));
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
