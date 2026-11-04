using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace LyricifyIsland;

internal sealed class BannerPage : PanelPage
{
    private static Bitmap? _clipboardBitmap;
    private readonly PlaybackStore _store;
    private readonly Func<IslandSettings> _settings;
    private TrackInfo? _track;
    private bool _translation;
    private string? _spotifyUrl;
    private bool _portrait;
    private readonly Segmented<bool> _orientation = new("图片方向", fill: true);
    private readonly ToggleSwitch _lyrics = Ui.Toggle("显示歌词");
    private readonly ToggleSwitch _brand = Ui.Toggle("显示软件名");
    private readonly ToggleSwitch _qr = Ui.Toggle("显示 Spotify 二维码", true);
    private readonly ComboBox _line = Ui.Combo("选择歌词", 0);
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _title = Ui.Text("暂无正在播放的歌曲", 22, FontWeight.SemiBold);
    private readonly TextBlock _detail = Ui.Text("播放歌曲后，再进入此页面生成分享图。", 13, muted: true);
    private readonly TextBlock _dimensions = Ui.Text("", 12, muted: true);
    private readonly TextBlock _status = Ui.Text("", 12, muted: true);
    private readonly Button _copy = Ui.Button("复制图片", accent: true);
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly IReadOnlyList<Control> _entrance;
    private readonly Control _empty;
    private Bitmap? _previewBitmap;
    private bool _copying;
    private bool _closed;
    private CancellationTokenSource? _coverCancellation;
    private Task _coverTask = Task.CompletedTask;
    private string _coverStatus = "";

    public override IReadOnlyList<Control> EntranceTargets => _entrance;

    public BannerPage(PlaybackStore store, Func<IslandSettings> settings)
    {
        _store = store;
        _settings = settings;
        _orientation.Set([(false, "横版"), (true, "竖版")], value => value == _portrait);
        _line.HorizontalAlignment = HorizontalAlignment.Stretch;
        _line.MaxDropDownHeight = 300;
        _line.ItemTemplate = new FuncDataTemplate<LyricLine>((line, _) =>
        {
            var text = Ui.Text(line is null ? "" : $"{TimeSpan.FromMilliseconds(Math.Max(0, line.StartMs)):mm\\:ss}  {line.Text}", 13);
            text.MaxWidth = 280;
            return text;
        });
        AutomationProperties.SetName(_preview, "歌曲分享图预览");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _copy.HorizontalAlignment = HorizontalAlignment.Stretch;
        _copy.HorizontalContentAlignment = HorizontalAlignment.Center;

        var heading = new StackPanel
        {
            Spacing = 5,
            Children = { Ui.Text("歌曲分享图", 13, muted: true), _title, _detail }
        };
        var empty = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text("分享此刻的音乐", 19, FontWeight.SemiBold),
                Ui.Text("播放歌曲后即可预览分享图", 13, muted: true)
            }
        };
        var previewContent = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,16,*,12,Auto"),
            Children = { Ui.Text("图片预览", 14, FontWeight.SemiBold), new Grid { Children = { empty, _preview } }, _dimensions }
        };
        Grid.SetRow(previewContent.Children[1], 2);
        Grid.SetRow(_dimensions, 4);
        var previewCard = Ui.Card(previewContent);
        previewCard.Height = 430;
        previewCard.VerticalAlignment = VerticalAlignment.Top;
        var appearance = Ui.Group(
            Ui.Block(new StackPanel
            {
                Spacing = 10,
                Children = { Ui.Text("图片方向", 14, FontWeight.SemiBold), _orientation }
            }),
            Ui.Row("歌词", "在图片中显示选中的一句", _lyrics),
            Ui.Block(new StackPanel
            {
                Spacing = 8,
                Children = { Ui.Text("选择歌词", 12, muted: true), _line }
            }),
            Ui.Row("软件名", "在图片底部显示软件名称", _brand),
            Ui.Row("Spotify 二维码", "扫码打开这首歌曲", _qr));
        var controls = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                appearance,
                _copy,
                Ui.Text("复制高清图片后，可直接粘贴到聊天或文档中。", 12, muted: true),
                _status
            }
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,24,340"),
            Children = { previewCard, controls }
        };
        Grid.SetColumn(controls, 2);
        var content = new StackPanel { Spacing = 22, Children = { heading, body } };
        Content = new ScrollViewer
        {
            Margin = new Thickness(28, 12, 28, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        };
        _entrance = [heading, previewCard, controls];
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < 760;
            body.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "*,24,340");
            body.RowDefinitions = new RowDefinitions(compact ? "Auto,20,Auto" : "*");
            Grid.SetColumn(controls, compact ? 0 : 2);
            Grid.SetRow(controls, compact ? 2 : 0);
            previewCard.Height = compact ? 300 : 430;
        };

        _orientation.Changed += portrait =>
        {
            _portrait = portrait;
            _orientation.Set([(false, "横版"), (true, "竖版")], value => value == _portrait);
            QueuePreview();
        };
        _line.SelectionChanged += (_, _) => QueuePreview();
        _lyrics.IsCheckedChanged += (_, _) =>
        {
            _line.IsEnabled = _lyrics.IsChecked == true && _lyrics.IsEnabled;
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
        DetachedFromVisualTree += (_, _) =>
        {
            _closed = true;
            _coverCancellation?.Cancel();
            _coverCancellation?.Dispose();
            _previewTimer.Stop();
            _preview.Source = null;
            _previewBitmap?.Dispose();
        };
        _empty = empty;
    }

    public override void OnShown()
    {
        _coverCancellation?.Cancel();
        _coverCancellation?.Dispose();
        _coverCancellation = new CancellationTokenSource();
        var snapshot = _store.Snapshot;
        var settings = _settings();
        _track = snapshot.Track;
        _translation = settings.ShowTranslation;
        _spotifyUrl = _track is null ? null : SongBanner.SpotifyUrl(_track.Id, settings.PlaybackSource);
        _qr.IsEnabled = _spotifyUrl is not null;
        _coverStatus = "";
        _title.Text = _track?.Title ?? "暂无正在播放的歌曲";
        _detail.Text = _track is null ? "播放歌曲后，再进入此页面生成分享图。" : string.Join(" / ", _track.Artists);
        _empty.IsVisible = _track is null;
        _preview.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        var lines = _track?.Lyrics.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray() ?? [];
        var position = snapshot.PositionMs + settings.LyricsOffsetMs
            + (settings.EnableTrackOffsets ? settings.TrackOffsetFor(_track?.Id) : 0);
        var index = IslandControl.FindLine(_track?.Lyrics, position);
        var currentLine = index >= 0 ? _track!.Lyrics[index] : null;
        _line.ItemsSource = lines;
        _line.SelectedItem = lines.Contains(currentLine) ? currentLine : lines.FirstOrDefault();
        _lyrics.IsEnabled = lines.Length > 0;
        _lyrics.IsChecked = currentLine is not null && lines.Length > 0;
        _line.IsEnabled = _lyrics.IsChecked == true;
        _orientation.IsEnabled = _brand.IsEnabled = _track is not null;
        _copy.IsEnabled = !_copying && _track is not null;
        _coverTask = _track is { } track ? LoadCoverAsync(track, _coverCancellation.Token) : Task.CompletedTask;
        QueuePreview();
        if (_track is null)
        {
            _dimensions.Text = "";
            _status.Text = "暂无可分享的歌曲";
        }
    }

    private byte[] Render(double scale) => SongBanner.Render(_track!,
        _lyrics.IsChecked == true ? _line.SelectedItem as LyricLine : null,
        _portrait, _translation, _qr.IsChecked == true ? _spotifyUrl : null,
        _brand.IsChecked == true, scale);

    private async Task LoadCoverAsync(TrackInfo track, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(track.AlbumArtUrl))
            return;
        _coverStatus = "正在获取高清封面…";
        try
        {
            var bytes = await SpotifyService.LoadImageSafeAsync(track.AlbumArtUrl, token);
            // Re-entering the page starts a new song snapshot and cancels the previous cover request.
            if (token.IsCancellationRequested)
                return;
            var loaded = TrackCache.PreferLargerImage([], bytes);
            var image = TrackCache.PreferLargerImage(track.AlbumArtBytes, loaded);
            _track = track with { AlbumArtBytes = image };
            _coverStatus = loaded.IsDefaultOrEmpty ? "封面加载失败，将使用缓存封面" : "";
            QueuePreview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                _coverStatus = "封面加载失败，将使用缓存封面";
                _status.Text = _coverStatus;
            }
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
        if (_closed || _track is null)
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
        if (_copying || _closed || _track is null)
            return;
        var token = _coverCancellation!.Token;
        _copying = true;
        _copy.IsEnabled = false;
        _status.Text = "正在生成高清图片…";
        try
        {
            await _coverTask;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (token.IsCancellationRequested)
                return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用");
            var png = Render(2);
            using var stream = new MemoryStream(png);
            Bitmap bitmap = OperatingSystem.IsLinux() ? new PngClipboardBitmap(png) : new Bitmap(stream);
            try
            {
                await clipboard.SetValueAsync(DataFormat.Bitmap, bitmap);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            var previous = _clipboardBitmap;
            _clipboardBitmap = bitmap;
            previous?.Dispose();
            if (!token.IsCancellationRequested)
                _status.Text = "图片已复制，可直接粘贴";
            try { await clipboard.FlushAsync(); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[banner] clipboard persistence failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                _status.Text = "复制失败，请重试";
            Console.Error.WriteLine($"[banner] copy failed: {exception.Message}");
        }
        finally
        {
            _copying = false;
            _copy.IsEnabled = !_closed && _track is not null;
        }
    }
}
