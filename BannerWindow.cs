using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace LyricifyIsland;

internal sealed class BannerWindow : Window
{
    private TrackInfo _track;
    private readonly bool _translation;
    private readonly string? _spotifyUrl;
    private readonly ComboBox _orientation = new()
    {
        ItemsSource = new[] { "横版", "竖版" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox _lyrics = new() { Content = "显示歌词" };
    private readonly CheckBox _brand = new() { Content = "显示软件名", IsChecked = false };
    private readonly CheckBox _qr = new() { Content = "显示 Spotify 二维码", IsChecked = true };
    private readonly ComboBox _line = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300 };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _dimensions = new() { Foreground = Brushes.Silver, FontSize = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver };
    private readonly Button _copy = new() { Content = "复制图片", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private Bitmap? _previewBitmap;
    private bool _copying;
    private bool _closed;
    private readonly CancellationTokenSource _coverCancellation = new();
    private Task _coverTask = Task.CompletedTask;
    private string _coverStatus = "";

    internal BannerWindow(TrackInfo track, LyricLine? currentLine, bool translation, string? spotifyUrl)
    {
        _track = track;
        _translation = translation;
        _spotifyUrl = spotifyUrl;
        _qr.IsVisible = spotifyUrl is not null;
        _qr.IsEnabled = spotifyUrl is not null;
        Title = "歌曲分享图";
        Width = 940;
        Height = 700;
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#121212"));
        Foreground = new SolidColorBrush(Color.Parse("#F5F5F5"));
        FontFamily = new FontFamily("Noto Sans CJK SC");
        FontSize = 14;
        RequestedThemeVariant = ThemeVariant.Dark;

        var lines = track.Lyrics.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
        _line.ItemsSource = lines;
        _line.ItemTemplate = new FuncDataTemplate<LyricLine>((line, _) => new TextBlock
        {
            Text = line is null ? "" : $"{TimeSpan.FromMilliseconds(Math.Max(0, line.StartMs)):mm\\:ss}  {line.Text}",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 268
        });
        _line.SelectedItem = lines.Contains(currentLine) ? currentLine : lines.FirstOrDefault();
        _lyrics.IsChecked = currentLine is not null && lines.Length > 0;
        _lyrics.IsEnabled = lines.Length > 0;
        _line.IsEnabled = _lyrics.IsChecked == true;
        AutomationProperties.SetName(_orientation, "图片方向");
        AutomationProperties.SetName(_line, "选择歌词");
        AutomationProperties.SetName(_preview, "歌曲分享图预览");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var controls = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = "歌曲分享图", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = track.Title, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "图片方向" }, _orientation, _dimensions } },
                _lyrics,
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "选择歌词" }, _line } },
                _brand,
                _qr,
                new TextBlock
                {
                    Text = "复制后可粘贴到聊天或文档中。",
                    TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver, FontSize = 12
                },
                _copy,
                _status
            }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), Margin = new Thickness(24) };
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#25282B")), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12), Margin = new Thickness(0, 0, 24, 0), Child = _preview
        });
        var scroll = new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        Content = grid;

        _orientation.SelectionChanged += (_, _) => QueuePreview();
        _line.SelectionChanged += (_, _) => QueuePreview();
        _lyrics.IsCheckedChanged += (_, _) =>
        {
            _line.IsEnabled = _lyrics.IsChecked == true;
            QueuePreview();
        };
        _brand.IsCheckedChanged += (_, _) => QueuePreview();
        _qr.IsCheckedChanged += (_, _) => QueuePreview();
        _copy.Click += async (_, _) => await CopyAsync();
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreview();
        };
        Opened += (_, _) =>
        {
            _coverTask = LoadCoverAsync();
            QueuePreview();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _coverCancellation.Cancel();
            _coverCancellation.Dispose();
            _previewTimer.Stop();
            _preview.Source = null;
            _previewBitmap?.Dispose();
        };
    }

    private byte[] Render(double scale) => SongBanner.Render(_track,
        _lyrics.IsChecked == true ? _line.SelectedItem as LyricLine : null,
        _orientation.SelectedIndex == 1, _translation, _qr.IsChecked == true ? _spotifyUrl : null,
        _brand.IsChecked == true, scale);

    private async Task LoadCoverAsync()
    {
        if (string.IsNullOrWhiteSpace(_track.AlbumArtUrl))
            return;
        _coverStatus = "正在获取高清封面…";
        try
        {
            var bytes = await SpotifyService.LoadImageSafeAsync(_track.AlbumArtUrl, _coverCancellation.Token);
            if (_closed)
                return;
            var loaded = TrackCache.PreferLargerImage([], bytes);
            var image = TrackCache.PreferLargerImage(_track.AlbumArtBytes, loaded);
            _track = _track with { AlbumArtBytes = image };
            _coverStatus = loaded.IsDefaultOrEmpty ? "封面加载失败，将使用缓存封面" : "";
            QueuePreview();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception exception)
        {
            _coverStatus = "封面加载失败，将使用缓存封面";
            if (!_closed)
                _status.Text = _coverStatus;
            Console.Error.WriteLine($"[banner] cover failed: {exception.Message}");
        }
    }

    private void QueuePreview()
    {
        _dimensions.Text = "正在更新尺寸…";
        _status.Text = _coverStatus;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void UpdatePreview()
    {
        if (_closed)
            return;
        try
        {
            using var stream = new MemoryStream(Render(.5));
            var bitmap = new Bitmap(stream);
            _dimensions.Text = $"{bitmap.PixelSize.Width * 4} × {bitmap.PixelSize.Height * 4} px";
            _preview.Source = bitmap;
            _previewBitmap?.Dispose();
            _previewBitmap = bitmap;
        }
        catch (Exception exception)
        {
            _preview.Source = null;
            _status.Text = "预览生成失败，请点击“复制图片”重试";
            Console.Error.WriteLine($"[banner] preview failed: {exception.Message}");
        }
    }

    private async Task CopyAsync()
    {
        if (_copying || _closed)
            return;
        _copying = true;
        _copy.IsEnabled = false;
        _status.Text = "正在生成高清图片…";
        try
        {
            await _coverTask;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (_closed)
                return;
            var clipboard = Clipboard ?? throw new InvalidOperationException("剪贴板不可用");
            var png = Render(2);
            // Keep encoded bytes alive for X11's deferred paste requests.
            await clipboard.SetValueAsync(DataFormat.CreateBytesPlatformFormat("image/png"), png);
            _status.Text = "图片已复制，可直接粘贴";
            try { await clipboard.FlushAsync(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[banner] clipboard persistence failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            _status.Text = "复制失败，请重试";
            Console.Error.WriteLine($"[banner] copy failed: {exception.Message}");
        }
        finally
        {
            _copying = false;
            _copy.IsEnabled = true;
        }
    }
}
