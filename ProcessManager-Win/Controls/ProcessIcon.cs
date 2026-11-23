using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ProcessManager.Win.Controls;

public sealed class ProcessIcon : Grid
{
    // 仅在 UI 线程访问；同一路径共享加载任务和图标，限制首次加载的并发量。
    private static readonly Dictionary<string, Task<BitmapImage?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LoadSlots = new(4);
    private readonly FontIcon _fallback = new() { Glyph = "\uE8A5", FontSize = 18 };
    private readonly Image _image = new();

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(ProcessIcon), new PropertyMetadata(null, OnFilePathChanged));

    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public ProcessIcon()
    {
        Children.Add(_fallback);
        Children.Add(_image);
    }

    private static async void OnFilePathChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ProcessIcon)sender;
        control._image.Source = null;
        control._fallback.Visibility = Visibility.Visible;
        if (args.NewValue is not string path || string.IsNullOrWhiteSpace(path)) return;

        if (!Cache.TryGetValue(path, out var loading))
        {
            loading = LoadAsync(path);
            Cache[path] = loading;
        }
        var icon = await loading;
        // 列表容器可能已被复用，不能把旧请求的图标应用到另一个进程。
        if (!string.Equals(control.FilePath, path, StringComparison.OrdinalIgnoreCase) || icon is null) return;
        control._image.Source = icon;
        control._fallback.Visibility = Visibility.Collapsed;
    }

    private static async Task<BitmapImage?> LoadAsync(string path)
    {
        await LoadSlots.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null) return null;
            var icon = new BitmapImage();
            await icon.SetSourceAsync(thumbnail);
            return icon;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // 文件已退出、不可访问或不含可用图标时，保留默认图标。
            return null;
        }
        finally
        {
            LoadSlots.Release();
        }
    }
}
