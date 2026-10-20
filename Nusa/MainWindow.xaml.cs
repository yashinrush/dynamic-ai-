using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Nusa;

public partial class MainWindow : Window
{
    private enum NusaMode { Music, Lyrics, Focus, Water, Shelf, Todo, Headphones, Charging, System }
    private sealed record LyricLine(TimeSpan At, string Text);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly NusaData _data;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _eyeBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(4100) };
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _systemPollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<NusaMode, Button> _navButtons = [];
    private readonly List<LyricLine> _lyricLines = [];
    private readonly Forms.NotifyIcon _trayIcon;
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private GlobalSystemMediaTransportControlsSession? _mediaSession;
    private string _mediaTitle = "Nothing playing";
    private string _mediaArtist = "Start music in a supported Windows player";
    private ImageSource? _albumArt;
    private string? _albumArtTrackTitle;
    private double _mediaPositionSeconds;
    private double _mediaDurationSeconds;
    private bool _mediaIsPlaying;
    private TextBlock? _focusCounterText;
    private NusaMode _mode = NusaMode.Music;
    private DateTime? _waterReminderDue;
    private DateTime? _lyricsStartedAt;
    private TimeSpan _lyricsOffset;
    private bool _expanded;
    private bool _wavePlaying;
    private bool _allowClose;
    private bool _lastCapsLock;
    private string? _lastInputLanguage;
    private int _lastBatteryPercent = -1;
    private bool _hiddenForFullscreen;
    private TimeSpan? _pausedFocusRemaining;
    private int _windowTransitionId;
    private int _panelTransitionId;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scan, uint flags, UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    private const byte VkMediaNext = 0xB0, VkMediaPrevious = 0xB1, VkMediaPlayPause = 0xB3;

    public MainWindow()
    {
        InitializeComponent();
        _data = AppDataStore.Load();
        if (_data.FocusEndsAt is not null && _data.FocusEndsAt <= DateTime.Now) _data.FocusEndsAt = null;
        if (_data.WaterRemindersEnabled) _waterReminderDue = DateTime.Now.AddHours(1);
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Nusa — your Windows notch",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open Nusa", null, (_, _) => Dispatcher.Invoke(() => SetExpanded(true)));
        var startupItem = new Forms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startupItem.CheckedChanged += (_, _) => ToggleStartup(startupItem.Checked);
        _trayIcon.ContextMenuStrip.Items.Add(startupItem);
        _trayIcon.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() => SetExpanded(!_expanded));

        _clockTimer.Tick += (_, _) => UpdateClock();
        _eyeBlinkTimer.Tick += (_, _) => BlinkRestingEyes();
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!IsMouseOver && !IsKeyboardFocusWithin) SetExpanded(false); };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _systemPollTimer.Tick += (_, _) => PollSystemState();
        _lastCapsLock = Keyboard.IsKeyToggled(Key.CapsLock);
        _lastInputLanguage = CultureInfo.CurrentCulture.Name;
        _clockTimer.Start();
        _eyeBlinkTimer.Start();
        _reminderTimer.Start();
        _systemPollTimer.Start();
        UpdateClock();
        RenderNavigation();
        RenderPanel();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        UpdateSystemIndicators();
        _ = InitializeMediaAsync();
    }

    private async Task InitializeMediaAsync()
    {
        try
        {
            _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mediaManager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await AttachCurrentSessionAsync()));
            _mediaManager.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(async () => await AttachCurrentSessionAsync()));
            await AttachCurrentSessionAsync();
        }
        catch
        {
            _mediaTitle = "Media session unavailable";
            _mediaArtist = "Windows could not provide player details";
            UpdateRestMedia();
        }
    }

    private async Task AttachCurrentSessionAsync()
    {
        try
        {
            if (_mediaSession is not null)
            {
                _mediaSession.MediaPropertiesChanged -= MediaSession_MediaPropertiesChanged;
                _mediaSession.PlaybackInfoChanged -= MediaSession_PlaybackInfoChanged;
                _mediaSession.TimelinePropertiesChanged -= MediaSession_TimelinePropertiesChanged;
            }
            _mediaSession = _mediaManager?.GetCurrentSession();
            if (_mediaSession is null)
            {
                _mediaTitle = "Nothing playing";
                _mediaArtist = "Start music in Spotify, Apple Music, a browser or another player";
                _mediaDurationSeconds = _mediaPositionSeconds = 0;
            }
            else
            {
                _mediaSession.MediaPropertiesChanged += MediaSession_MediaPropertiesChanged;
                _mediaSession.PlaybackInfoChanged += MediaSession_PlaybackInfoChanged;
                _mediaSession.TimelinePropertiesChanged += MediaSession_TimelinePropertiesChanged;
                await RefreshMediaAsync();
            }
            UpdateRestMedia();
            if (_expanded && (_mode == NusaMode.Music || _mode == NusaMode.Lyrics)) RenderPanel();
        }
        catch { }
    }

    private void MediaSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Dispatcher.BeginInvoke(new Action(async () => await RefreshMediaAsync()));
    private void MediaSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Dispatcher.BeginInvoke(new Action(async () => await RefreshMediaAsync()));
    private void MediaSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Dispatcher.BeginInvoke(new Action(async () => await RefreshMediaAsync()));

    private async Task RefreshMediaAsync()
    {
        if (_mediaSession is null) return;
        try
        {
            var properties = await _mediaSession.TryGetMediaPropertiesAsync();
            _mediaTitle = string.IsNullOrWhiteSpace(properties.Title) ? "Unknown track" : properties.Title;
            if (_albumArtTrackTitle != _mediaTitle)
            {
                _albumArt = await LoadAlbumArtAsync(properties.Thumbnail);
                _albumArtTrackTitle = _mediaTitle;
            }
            _mediaArtist = string.IsNullOrWhiteSpace(properties.Artist) ? _mediaSession.SourceAppUserModelId : properties.Artist;
            var timeline = _mediaSession.GetTimelineProperties();
            _mediaPositionSeconds = Math.Max(0, timeline.Position.TotalSeconds);
            _mediaDurationSeconds = Math.Max(0, timeline.EndTime.TotalSeconds);
            _mediaIsPlaying = _mediaSession.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!string.IsNullOrWhiteSpace(properties.Title)) _data.LastTrackTitle = properties.Title;
            if (!string.IsNullOrWhiteSpace(properties.Artist)) _data.LastTrackArtist = properties.Artist;
            if (timeline.EndTime.TotalSeconds > 0) _data.LastTrackDurationSeconds = timeline.EndTime.TotalSeconds;
            UpdateRestMedia();
            if (_expanded && (_mode == NusaMode.Music || _mode == NusaMode.Lyrics)) RenderPanel();
        }
        catch { }
    }

    private static async Task<ImageSource?> LoadAlbumArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var randomAccessStream = await thumbnail.OpenReadAsync();
            using var source = randomAccessStream.AsStreamForRead();
            using var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            if (memory.Length == 0 || memory.Length > 12 * 1024 * 1024) return null;
            memory.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private void PositionWindow()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var size = _expanded ? GetExpandedSize(_mode) : (Width: 244d, Height: 48d);
        Width = size.Width;
        Height = size.Height;
        Left = screen.Bounds.Left / scale + (screen.Bounds.Width / scale - Width) / 2;
        Top = screen.Bounds.Top / scale;
    }

    private void SetExpanded(bool expanded)
    {
        _collapseTimer.Stop();
        if (_expanded == expanded)
        {
            if (expanded && !IsVisible) Show();
            return;
        }
        _expanded = expanded;
        var transition = ++_windowTransitionId;
        if (!IsVisible) Show();
        if (expanded)
        {
            RestView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.Opacity = 0;
            RenderPanel();
            UpdateSystemIndicators();
            var size = GetExpandedSize(_mode);
            AnimateWindowBounds(size.Width, size.Height);
            AnimateValue(ExpandedView, UIElement.OpacityProperty, 0, 1, 190, new CubicEase { EasingMode = EasingMode.EaseOut });
            AnimateValue(ShellScale, ScaleTransform.ScaleYProperty, .94, 1, 360, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .12 });
            return;
        }

        var fade = new DoubleAnimation(ExpandedView.Opacity, 0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (transition != _windowTransitionId || _expanded) return;
            ExpandedView.Visibility = Visibility.Collapsed;
            RestView.Visibility = Visibility.Visible;
            AnimateWindowBounds(244, 48);
            AnimateValue(ShellScale, ScaleTransform.ScaleYProperty, ShellScale.ScaleY, 1, 220, new CubicEase { EasingMode = EasingMode.EaseOut });
        };
        ExpandedView.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private (double Width, double Height) GetExpandedSize(NusaMode mode) => mode switch
    {
        NusaMode.Music => (690, 245),
        NusaMode.Lyrics => (790, 260),
        NusaMode.Focus => (680, 240),
        NusaMode.Water => (680, 230),
        NusaMode.Shelf => (680, 245),
        NusaMode.Todo => (680, 245),
        NusaMode.Headphones => (520, 170),
        NusaMode.Charging => (500, 150),
        _ => (680, 260)
    };

    private void AnimateWindowBounds(double targetWidth, double targetHeight)
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        targetWidth = Math.Min(targetWidth, screen.Bounds.Width / scale - 24);
        targetHeight = Math.Min(targetHeight, screen.Bounds.Height / scale - 24);
        var targetLeft = screen.Bounds.Left / scale + (screen.Bounds.Width / scale - targetWidth) / 2;
        var widthDuration = targetWidth > ActualWidth ? 420 : 330;
        var heightDuration = targetHeight > ActualHeight ? 460 : 310;
        AnimateValue(this, WidthProperty, ActualWidth, targetWidth, widthDuration,
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = targetWidth > ActualWidth ? .12 : .07 });
        AnimateValue(this, HeightProperty, ActualHeight, targetHeight, heightDuration,
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = targetHeight > ActualHeight ? .10 : .06 });
        AnimateValue(this, LeftProperty, Left, targetLeft, widthDuration, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    private static void AnimateValue(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds, IEasingFunction easing)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (!_expanded) SetExpanded(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        SetExpanded(false);
    }

    private void Quit()
    {
        _allowClose = true;
        Save();
        _clockTimer.Stop(); _eyeBlinkTimer.Stop(); _reminderTimer.Stop(); _collapseTimer.Stop(); _systemPollTimer.Stop();
        _trayIcon.Visible = false; _trayIcon.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    private void UpdateClock()
    {
        RestClock.Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
        DateClock.Text = DateTime.Now.ToString("ddd dd MMM  HH:mm", CultureInfo.CurrentCulture);
        if (_expanded && _mode == NusaMode.Focus && _focusCounterText is not null) _focusCounterText.Text = FocusRemaining();
        if (_lyricsStartedAt is not null && _expanded && _mode == NusaMode.Lyrics) UpdateLyricHighlight();
    }

    private void BlinkRestingEyes()
    {
        if (RestEyes.Visibility != Visibility.Visible) return;
        foreach (var eye in new[] { LeftEye, RightEye })
        {
            if (eye.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1, 1);
                eye.RenderTransformOrigin = new Point(.5, .5);
                eye.RenderTransform = scale;
            }
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, .08, TimeSpan.FromMilliseconds(95))
            {
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }

    private void UpdateRestMedia()
    {
        var hasTrack = !string.IsNullOrWhiteSpace(_mediaTitle) && _mediaTitle != "Nothing playing" && _mediaTitle != "Media session unavailable";
        RestMediaContent.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        RestEyes.Visibility = hasTrack ? Visibility.Collapsed : Visibility.Visible;
        if (_wavePlaying != (hasTrack && _mediaIsPlaying))
        {
            _wavePlaying = hasTrack && _mediaIsPlaying;
            SetWaveAnimation(RestWave, _wavePlaying);
            SetWaveAnimation(HeaderWave, _wavePlaying);
        }
    }

    private static void SetWaveAnimation(Panel wave, bool playing)
    {
        var index = 0;
        foreach (var bar in wave.Children.OfType<Border>())
        {
            if (bar.RenderTransform is not ScaleTransform scale) continue;
            if (playing)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.25, 1, TimeSpan.FromMilliseconds(230 + index * 45))
                {
                    AutoReverse = true,
                    BeginTime = TimeSpan.FromMilliseconds(index * 75),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = .45;
            }
            index++;
        }
    }

    private void UpdateSystemIndicators()
    {
        try
        {
            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            var percent = (int)Math.Round(power.BatteryLifePercent * 100);
            BatteryGlyph.Text = "◉";
            BatteryGlyph.Foreground = percent < 20 ? Brush("#FFFF7B73") : Brush("#FF49D69B");
            BatteryGlyph.ToolTip = power.PowerLineStatus == Forms.PowerLineStatus.Online ? $"Charging · {percent}%" : $"Battery · {percent}%";
            _lastBatteryPercent = percent;
        }
        catch { }
    }

    private void CheckReminders()
    {
        var now = DateTime.Now;
        foreach (var task in _data.Tasks.Where(t => !t.IsDone && t.DueAt is not null && t.DueAt <= now && (t.RemindedAt is null || now - t.RemindedAt > TimeSpan.FromHours(12))))
        {
            task.RemindedAt = now;
            _trayIcon.ShowBalloonTip(5000, "Nusa task reminder", task.Title, Forms.ToolTipIcon.Info);
            Save();
        }
        if (_data.WaterRemindersEnabled && now.Hour >= _data.ReminderStartHour && now.Hour < _data.ReminderEndHour && (_waterReminderDue is null || _waterReminderDue <= now))
        {
            _trayIcon.ShowBalloonTip(4000, "A little water break", "Take a sip and log it in Nusa.", Forms.ToolTipIcon.Info);
            _waterReminderDue = now.AddHours(1);
        }
        if (_data.FocusEndsAt is not null && _data.FocusEndsAt <= now)
        {
            _data.FocusEndsAt = null;
            _pausedFocusRemaining = null;
            System.Media.SystemSounds.Asterisk.Play();
            _trayIcon.ShowBalloonTip(5000, _data.IsOnBreak ? "Break finished" : "Focus session complete", _data.IsOnBreak ? "Ready for another focus session?" : "Time for a short break.", Forms.ToolTipIcon.Info);
            if (_expanded && _mode == NusaMode.Focus) RenderPanel();
            Save();
        }
        var caps = Keyboard.IsKeyToggled(Key.CapsLock);
        if (caps != _lastCapsLock)
        {
            _lastCapsLock = caps;
            if (caps) { _mode = NusaMode.System; if (_expanded) RenderPanel(); }
        }
        UpdateSystemIndicators();
    }

    private void PollSystemState()
    {
        var caps = Keyboard.IsKeyToggled(Key.CapsLock);
        if (caps != _lastCapsLock)
        {
            _lastCapsLock = caps;
            if (caps)
            {
                _mode = NusaMode.System;
                if (_expanded) RenderPanel();
                _trayIcon.ShowBalloonTip(2200, "Caps Lock is on", "Keyboard status changed.", Forms.ToolTipIcon.Info);
            }
        }
        var language = Forms.InputLanguage.CurrentInputLanguage?.Culture.Name;
        if (language is not null && _lastInputLanguage is not null && language != _lastInputLanguage)
        {
            _lastInputLanguage = language;
            if (_expanded && _mode == NusaMode.System) RenderPanel();
            _trayIcon.ShowBalloonTip(2200, "Keyboard language changed", language, Forms.ToolTipIcon.Info);
        }
        else if (language is not null) _lastInputLanguage = language;

        var foreground = GetForegroundWindow();
        var ownHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (foreground != 0 && ownHandle != foreground && GetWindowRect(foreground, out var rect))
        {
            var screen = Forms.Screen.FromHandle(foreground);
            var fullscreen = rect.Left <= screen.Bounds.Left && rect.Top <= screen.Bounds.Top && rect.Right >= screen.Bounds.Right - 1 && rect.Bottom >= screen.Bounds.Bottom - 1;
            if (fullscreen && IsVisible) { _hiddenForFullscreen = true; Hide(); }
            else if (!fullscreen && _hiddenForFullscreen) { _hiddenForFullscreen = false; Show(); PositionWindow(); }
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false);
            return key?.GetValue("Nusa") is not null;
        }
        catch { return false; }
    }

    private void ToggleStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true)
                ?? Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            if (enabled) key?.SetValue("Nusa", $"\"{Environment.ProcessPath}\"");
            else key?.DeleteValue("Nusa", false);
        }
        catch (Exception ex) { _trayIcon.ShowBalloonTip(3500, "Startup setting", ex.Message, Forms.ToolTipIcon.Warning); }
    }

    private void Save()
    {
        try { AppDataStore.Save(_data); }
        catch (Exception ex) { _trayIcon.ShowBalloonTip(3500, "Nusa", "Could not save your local data: " + ex.Message, Forms.ToolTipIcon.Warning); }
    }

    private void RenderNavigation()
    {
        Navigation.Children.Clear(); _navButtons.Clear();
        var nav = new (NusaMode Mode, string Glyph, string Tip)[]
        {
            (NusaMode.Music,"♫","Music"),(NusaMode.Lyrics,"▤","Lyrics"),(NusaMode.Focus,"◷","Focus"),
            (NusaMode.Water,"◉","Water"),(NusaMode.Shelf,"⇩","Shelf"),(NusaMode.Todo,"✓","To-do"),
            (NusaMode.Headphones,"♫","Headphones"),(NusaMode.Charging,"ϟ","Charging"),(NusaMode.System,"▥","System")
        };
        foreach (var (mode, glyph, tip) in nav)
        {
            var selected = mode == _mode;
            var button = new Button
            {
                Content = null, ToolTip = tip, Style = (Style)FindResource("PillButton"),
                Width = selected ? 19 : 6, Height = 6, MinWidth = selected ? 19 : 6,
                Padding = new Thickness(0), Margin = new Thickness(3, 0, 3, 0),
                FontSize = 1, BorderThickness = new Thickness(0),
                Background = selected ? Brushes.White : Brush("#FF72757E"),
                BorderBrush = Brushes.Transparent,
                Focusable = false
            };
            button.Click += (_, _) => SelectMode(mode);
            Navigation.Children.Add(button); _navButtons[mode] = button;
        }
    }

    private void SelectMode(NusaMode mode)
    {
        _mode = mode;
        foreach (var pair in _navButtons)
        {
            var selected = pair.Key == mode;
            pair.Value.Width = pair.Value.MinWidth = selected ? 19 : 6;
            pair.Value.Background = selected ? Brushes.White : Brush("#FF72757E");
            pair.Value.BorderBrush = Brushes.Transparent;
        }
        RenderPanel(transition: true);
        if (_expanded)
        {
            var size = GetExpandedSize(mode);
            AnimateWindowBounds(size.Width, size.Height);
        }
    }

    private void RenderPanel(bool transition = false)
    {
        if (!transition) _panelTransitionId++;
        HeaderTitle.Text = _mode switch
        {
            NusaMode.Music => "Now playing", NusaMode.Lyrics => "Synced lyrics", NusaMode.Focus => "Pomodoro",
            NusaMode.Water => "Water reminder", NusaMode.Shelf => "File shelf", NusaMode.Todo => "TodoList",
            NusaMode.Headphones => "Headphones", NusaMode.Charging => "Power", _ => "System status"
        };
        UIElement CreateContent() => _mode switch
        {
            NusaMode.Music => BuildMusic(), NusaMode.Lyrics => BuildLyrics(), NusaMode.Focus => BuildFocus(),
            NusaMode.Water => BuildWater(), NusaMode.Shelf => BuildShelf(), NusaMode.Todo => BuildTodo(),
            NusaMode.Headphones => BuildHeadphones(), NusaMode.Charging => BuildCharging(), _ => BuildSystem()
        };

        var nextContent = CreateContent();
        if (!transition || PanelContent.Content is not UIElement oldContent)
        {
            PanelContent.Content = nextContent;
            nextContent.Opacity = 1;
            return;
        }

        var panelTransition = ++_panelTransitionId;
        var oldShift = oldContent.RenderTransform as TranslateTransform ?? new TranslateTransform();
        oldContent.RenderTransform = oldShift;
        var fadeOut = new DoubleAnimation(oldContent.Opacity, 0, TimeSpan.FromMilliseconds(85))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        AnimateValue(oldShift, TranslateTransform.YProperty, oldShift.Y, -4, 110, new CubicEase { EasingMode = EasingMode.EaseIn });
        fadeOut.Completed += (_, _) =>
        {
            if (panelTransition != _panelTransitionId) return;
            PanelContent.Content = nextContent;
            nextContent.Opacity = 0;
            var nextShift = new TranslateTransform(0, 8);
            nextContent.RenderTransform = nextShift;
            nextContent.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            AnimateValue(nextShift, TranslateTransform.YProperty, 8, 0, 260,
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .12 });
        };
        oldContent.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private UIElement BuildMusic()
    {
        var root = new Grid { Margin = new Thickness(8, 1, 8, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var artContent = _albumArt is not null
            ? (UIElement)new Image { Source = _albumArt, Stretch = Stretch.UniformToFill }
            : new TextBlock { Text = "♫", FontSize = 36, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var art = new Border { Width = 88, Height = 88, CornerRadius = new CornerRadius(19), ClipToBounds = true, Background = new LinearGradientBrush(Brush("#FF3475D8").Color, Brush("#FF7756C8").Color, 45), Child = artContent, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(art);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(content, 1); root.Children.Add(content);
        content.Children.Add(new TextBlock { Text = _mediaTitle, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(new TextBlock { Text = _mediaArtist, Foreground = Brush("#FF9A9A9D"), FontSize = 12, Margin = new Thickness(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis });
        var position = new Slider { Minimum = 0, Maximum = Math.Max(1, _mediaDurationSeconds), Value = Math.Clamp(_mediaPositionSeconds, 0, Math.Max(1, _mediaDurationSeconds)), IsEnabled = _mediaSession is not null, Height = 17, Margin = new Thickness(0, 0, 0, 1), Foreground = Brush("#FFF0F0F2") };
        position.PreviewMouseLeftButtonUp += async (_, _) => await SeekMediaAsync(position.Value);
        position.KeyUp += async (_, _) => await SeekMediaAsync(position.Value);
        content.Children.Add(position);
        var times = new Grid { Margin = new Thickness(0, -3, 0, 3) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.Children.Add(new TextBlock { Text = FormatSeconds(_mediaPositionSeconds), Foreground = Brush("#FFE5E5E8"), FontSize = 10 });
        var remaining = new TextBlock { Text = "-" + FormatSeconds(Math.Max(0, _mediaDurationSeconds - _mediaPositionSeconds)), Foreground = Brush("#FFE5E5E8"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(remaining, 1); times.Children.Add(remaining); content.Children.Add(times);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(MediaButton("▤", "Open synced lyrics", () => SelectMode(NusaMode.Lyrics)));
        controls.Children.Add(MediaButton("◀◀", "Previous track", async () => { if (_mediaSession is null) SendMediaKey(VkMediaPrevious); else await _mediaSession.TrySkipPreviousAsync(); }));
        controls.Children.Add(MediaButton(_mediaIsPlaying ? "Ⅱ" : "▶", "Play or pause", async () => { if (_mediaSession is null) SendMediaKey(VkMediaPlayPause); else await _mediaSession.TryTogglePlayPauseAsync(); }, true));
        controls.Children.Add(MediaButton("▶▶", "Next track", async () => { if (_mediaSession is null) SendMediaKey(VkMediaNext); else await _mediaSession.TrySkipNextAsync(); }));
        content.Children.Add(controls);
        return root;
    }

    private static string FormatSeconds(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private async Task SeekMediaAsync(double seconds)
    {
        if (_mediaSession is null) return;
        try { await _mediaSession.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks); } catch { }
    }

    private UIElement BuildLyrics()
    {
        var root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(235) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var form = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(form, 0); root.Children.Add(form);
        var track = new TextBox { Style = (Style)FindResource("TextBoxStyle"), Text = _mediaSession is not null ? _mediaTitle : _data.LastTrackTitle ?? "", ToolTip = "Track title", Margin = new Thickness(0, 3, 0, 6), IsReadOnly = _mediaSession is not null };
        var artist = new TextBox { Style = (Style)FindResource("TextBoxStyle"), Text = _mediaSession is not null ? _mediaArtist : _data.LastTrackArtist ?? "", ToolTip = "Artist", Margin = new Thickness(0, 0, 0, 6), IsReadOnly = _mediaSession is not null };
        var durationValue = _mediaDurationSeconds > 0 ? _mediaDurationSeconds : _data.LastTrackDurationSeconds;
        var duration = new TextBox { Style = (Style)FindResource("TextBoxStyle"), Text = durationValue > 0 ? durationValue.ToString(CultureInfo.InvariantCulture) : "", ToolTip = "Track duration in seconds (optional)", Margin = new Thickness(0, 0, 0, 6), IsReadOnly = _mediaSession is not null };
        if (_mediaSession is not null)
        {
            var cover = new Border { Width = 70, Height = 70, CornerRadius = new CornerRadius(15), ClipToBounds = true, Background = new LinearGradientBrush(Brush("#FF3475D8").Color, Brush("#FF7756C8").Color, 45), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
            cover.Child = _albumArt is not null ? new Image { Source = _albumArt, Stretch = Stretch.UniformToFill } : new TextBlock { Text = "♫", FontSize = 31, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            form.Children.Add(cover);
            form.Children.Add(new TextBlock { Text = _mediaTitle, Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            form.Children.Add(new TextBlock { Text = _mediaArtist, Foreground = Brush("#FF92959F"), FontSize = 11, Margin = new Thickness(0, 2, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(MediaButton("◀◀", "Previous track", async () => { if (_mediaSession is null) SendMediaKey(VkMediaPrevious); else await _mediaSession.TrySkipPreviousAsync(); }));
            controls.Children.Add(MediaButton(_mediaIsPlaying ? "Ⅱ" : "▶", "Play or pause", async () => { if (_mediaSession is null) SendMediaKey(VkMediaPlayPause); else await _mediaSession.TryTogglePlayPauseAsync(); }, true));
            controls.Children.Add(MediaButton("▶▶", "Next track", async () => { if (_mediaSession is null) SendMediaKey(VkMediaNext); else await _mediaSession.TrySkipNextAsync(); }));
            form.Children.Add(controls);
        }
        else
        {
            form.Children.Add(new TextBlock { Text = "Find a song", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            form.Children.Add(track); form.Children.Add(artist);
            form.Children.Add(new TextBlock { Text = "Duration in seconds · optional", Foreground = Brush("#FF858892"), FontSize = 9, Margin = new Thickness(1, 0, 0, 3) });
            form.Children.Add(duration);
        }
        var fetch = ActionButton(_lyricLines.Count > 0 ? "↻  Refresh lyrics" : "Find synced lyrics", "Search LRCLIB for synced lyrics", async () =>
        {
            if (_mediaSession is null) { _data.LastTrackTitle = track.Text; _data.LastTrackArtist = artist.Text; }
            _data.LastTrackDurationSeconds = double.TryParse(duration.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) ? secs : _mediaDurationSeconds;
            Save(); await FetchLyrics(track.Text.Trim(), artist.Text.Trim(), _data.LastTrackDurationSeconds);
        }, true);
        fetch.HorizontalAlignment = HorizontalAlignment.Stretch;
        fetch.Margin = new Thickness(0, 8, 0, 0); form.Children.Add(fetch);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = Brush("#FF08090D"), Padding = new Thickness(18, 8, 12, 8), BorderBrush = Brush("#FF202126"), BorderThickness = new Thickness(1) };
        var list = new StackPanel();
        list.Name = "LyricRows";
        scroll.Content = list; Grid.SetColumn(scroll, 1); root.Children.Add(scroll);
        if (_lyricLines.Count == 0) list.Children.Add(new TextBlock { Text = "Lyrics will appear here\nafter you search for a track.", Foreground = Brush("#FF777A83"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 38, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        else
        {
            for (var i = 0; i < _lyricLines.Count; i++)
            {
                var lineIndex = i;
                var tb = new TextBlock { Text = _lyricLines[i].Text, Tag = _lyricLines[i].At, Foreground = Brush("#FF777A83"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand };
                tb.MouseLeftButtonUp += async (_, _) => { _lyricsOffset = (TimeSpan)tb.Tag; _lyricsStartedAt = DateTime.Now; await SeekMediaAsync(_lyricsOffset.TotalSeconds); UpdateLyricHighlight(); };
                list.Children.Add(tb);
            }
        }
        _lyricsList = list;
        return root;
    }

    private StackPanel? _lyricsList;

    private async Task FetchLyrics(string title, string artist, double duration)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) { _lyricLines.Clear(); RenderPanel(); return; }
        try
        {
            var query = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (duration > 0) query += $"&duration={duration.ToString(CultureInfo.InvariantCulture)}";
            using var response = await Http.GetAsync(query);
            if (!response.IsSuccessStatusCode) { _lyricLines.Clear(); RenderPanel(); return; }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var lrc = document.RootElement.TryGetProperty("syncedLyrics", out var synced) ? synced.GetString() : null;
            var plain = document.RootElement.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;
            _lyricLines.Clear();
            if (!string.IsNullOrWhiteSpace(lrc))
            {
                foreach (var raw in lrc.Split('\n'))
                {
                    var match = Regex.Match(raw, @"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)");
                    if (!match.Success) continue;
                    var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    var fraction = match.Groups[3].Value;
                    var millis = fraction.Length switch { 0 => 0, 1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100, 2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10, _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture) };
                    _lyricLines.Add(new LyricLine(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(millis), match.Groups[4].Value.Trim()));
                }
            }
            else if (!string.IsNullOrWhiteSpace(plain))
                _lyricLines.AddRange(plain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select((line, i) => new LyricLine(TimeSpan.FromSeconds(i * 4), line.Trim())));
            _lyricsOffset = TimeSpan.Zero; _lyricsStartedAt = DateTime.Now;
            RenderPanel();
            if (_lyricLines.Count == 0) _trayIcon.ShowBalloonTip(3500, "No lyrics found", "LRCLIB has no match for this track yet.", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _lyricLines.Clear(); RenderPanel();
            _trayIcon.ShowBalloonTip(4500, "Lyrics unavailable", "Could not reach LRCLIB. Check your internet connection.", Forms.ToolTipIcon.Warning);
        }
    }

    private void UpdateLyricHighlight()
    {
        if (_lyricsList is null || _lyricsStartedAt is null) return;
        var now = _mediaSession is null ? _lyricsOffset + (DateTime.Now - _lyricsStartedAt.Value) : _mediaSession.GetTimelineProperties().Position;
        var current = -1;
        for (var i = 0; i < _lyricLines.Count; i++) if (_lyricLines[i].At <= now) current = i;
        for (var i = 0; i < _lyricsList.Children.Count && i < _lyricLines.Count; i++)
        {
            if (_lyricsList.Children[i] is not TextBlock tb) continue;
            var distance = Math.Abs(i - current);
            tb.Foreground = Brush(i == current ? "#FFFFFFFF" : distance == 1 ? "#FFB9BBC0" : "#FF777A83");
            tb.FontSize = i == current ? 17 : 13;
            tb.Opacity = distance > 3 ? .55 : 1;
        }
    }

    private UIElement BuildFocus()
    {
        var root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _focusCounterText = new TextBlock { Text = FocusRemaining(), Foreground = Brushes.White, FontSize = 34, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var timer = new Border { Width = 138, Height = 138, CornerRadius = new CornerRadius(69), BorderThickness = new Thickness(8), BorderBrush = Brush(_data.IsOnBreak ? "#FF4CA8FF" : "#FFFF4655"), Background = Brush("#FF090A0D"), Child = _focusCounterText };
        timer.HorizontalAlignment = HorizontalAlignment.Left; timer.VerticalAlignment = VerticalAlignment.Center; root.Children.Add(timer);
        var controls = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(controls, 1); root.Children.Add(controls);
        controls.Children.Add(new TextBlock { Text = _data.IsOnBreak ? "Short break" : "Focus time", Foreground = Brush("#FFA4A6B2"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ActionButton(_data.FocusEndsAt is null ? (_pausedFocusRemaining is null ? "Start" : "Resume") : "Pause", "Start, pause or resume timer", () => ToggleFocus(), true));
        row.Children.Add(ActionButton("Reset", "Reset timer", () => { _data.FocusEndsAt = null; _pausedFocusRemaining = null; Save(); RenderPanel(); }));
        row.Children.Add(ActionButton(_data.IsOnBreak ? "Focus" : "Short break", "Switch focus or break mode", () => { _data.IsOnBreak = !_data.IsOnBreak; _data.FocusEndsAt = null; _pausedFocusRemaining = null; Save(); RenderPanel(); }));
        controls.Children.Add(row);
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        settings.Children.Add(new TextBlock { Text = "Focus min", Foreground = Brush("#FF9295A2"), VerticalAlignment = VerticalAlignment.Center, FontSize = 10 });
        var focus = new TextBox { Text = _data.FocusMinutes.ToString(), Width = 42, Style = (Style)FindResource("TextBoxStyle"), Margin = new Thickness(7, 0, 11, 0) };
        focus.LostFocus += (_, _) => { if (int.TryParse(focus.Text, out var v)) _data.FocusMinutes = Math.Clamp(v, 1, 180); Save(); };
        settings.Children.Add(focus); settings.Children.Add(new TextBlock { Text = "Break min", Foreground = Brush("#FF9295A2"), VerticalAlignment = VerticalAlignment.Center, FontSize = 10 });
        var brk = new TextBox { Text = _data.BreakMinutes.ToString(), Width = 42, Style = (Style)FindResource("TextBoxStyle"), Margin = new Thickness(7, 0, 0, 0) };
        brk.LostFocus += (_, _) => { if (int.TryParse(brk.Text, out var v)) _data.BreakMinutes = Math.Clamp(v, 1, 60); Save(); };
        settings.Children.Add(brk); controls.Children.Add(settings);
        return root;
    }

    private string FocusRemaining()
    {
        var remaining = _data.FocusEndsAt is null ? TimeSpan.FromMinutes(_data.IsOnBreak ? _data.BreakMinutes : _data.FocusMinutes) : _data.FocusEndsAt.Value - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
    }

    private void ToggleFocus()
    {
        if (_data.FocusEndsAt is null)
        {
            var duration = _pausedFocusRemaining ?? TimeSpan.FromMinutes(_data.IsOnBreak ? _data.BreakMinutes : _data.FocusMinutes);
            _data.FocusEndsAt = DateTime.Now.Add(duration);
            _pausedFocusRemaining = null;
        }
        else
        {
            _pausedFocusRemaining = _data.FocusEndsAt.Value - DateTime.Now;
            if (_pausedFocusRemaining < TimeSpan.Zero) _pausedFocusRemaining = TimeSpan.Zero;
            _data.FocusEndsAt = null;
        }
        Save(); RenderPanel();
    }

    private UIElement BuildWater()
    {
        var root = new Grid { Margin = new Thickness(8, 1, 8, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        var total = TodayWater(); var progress = Math.Min(1, total / (double)Math.Max(1, _data.WaterGoalMl));
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; root.Children.Add(left);
        left.Children.Add(new TextBlock { Text = "WATER TODAY", Foreground = Brush("#FF9CA1AE"), FontSize = 10, FontWeight = FontWeights.Bold });
        left.Children.Add(new TextBlock { Text = $"{total / 1000.0:0.0} / {_data.WaterGoalMl / 1000.0:0.0} L", Foreground = Brushes.White, FontSize = 27, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 1, 0, 0) });
        left.Children.Add(new TextBlock { Text = progress >= 1 ? "Goal reached — nice work" : $"{progress:P0} of your daily goal", Foreground = Brush("#FF9295A2"), FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(ActionButton($"💧 Drink {_data.BottleMl} ml", "Log one drink", () => { _data.WaterEntries.Add(new WaterEntry { At = DateTime.Now, Millilitres = _data.BottleMl }); Save(); RenderPanel(); }, true));
        controls.Children.Add(ActionButton("Settings", "Set bottle and daily goal", () => SelectMode(NusaMode.Water)));
        left.Children.Add(controls);
        var chart = new StackPanel { Margin = new Thickness(14, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(chart, 1); root.Children.Add(chart);
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 82, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var h = 8; h < 20; h++)
        {
            var ml = _data.WaterEntries.Where(w => w.At.Date == DateTime.Today && w.At.Hour == h).Sum(w => w.Millilitres);
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0), Width = 12 };
            column.Children.Add(new Border { Height = Math.Max(3, 58 * ml / Math.Max(300, _data.WaterGoalMl / 3.0)), CornerRadius = new CornerRadius(3, 3, 0, 0), Background = Brush(ml > 0 ? "#FF66A8FF" : "#FF272A34") });
            bars.Children.Add(column);
        }
        chart.Children.Add(bars); chart.Children.Add(new TextBlock { Text = "08     12     16     20", Foreground = Brush("#FF858996"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var settings = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var reminder = new CheckBox { Content = "Hourly reminders", IsChecked = _data.WaterRemindersEnabled, Style = (Style)FindResource("CheckBoxStyle"), FontSize = 10 };
        reminder.Checked += (_, _) => { _data.WaterRemindersEnabled = true; _waterReminderDue = DateTime.Now.AddHours(1); Save(); };
        reminder.Unchecked += (_, _) => { _data.WaterRemindersEnabled = false; _waterReminderDue = null; Save(); };
        settings.Children.Add(reminder);
        settings.Children.Add(new TextBlock { Text = "Bottle ml", Foreground = Brush("#FF9295A2"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        var bottle = new TextBox { Text = _data.BottleMl.ToString(), Width = 44, Style = (Style)FindResource("TextBoxStyle") };
        bottle.LostFocus += (_, _) => { if (int.TryParse(bottle.Text, out var v)) _data.BottleMl = Math.Clamp(v, 50, 2000); Save(); };
        settings.Children.Add(bottle);
        settings.Children.Add(new TextBlock { Text = "Goal ml", Foreground = Brush("#FF9295A2"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        var goal = new TextBox { Text = _data.WaterGoalMl.ToString(), Width = 48, Style = (Style)FindResource("TextBoxStyle") };
        goal.LostFocus += (_, _) => { if (int.TryParse(goal.Text, out var v)) _data.WaterGoalMl = Math.Clamp(v, 250, 10000); Save(); };
        settings.Children.Add(goal);
        left.Children.Add(settings);
        return root;
    }

    private int TodayWater() => _data.WaterEntries.Where(w => w.At.Date == DateTime.Today).Sum(w => w.Millilitres);

    private UIElement BuildShelf()
    {
        var root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        header.Children.Add(new TextBlock { Text = "Drag files here to keep them close. Click a card to open it.", Foreground = Brush("#FF9A9DAA"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var choose = ActionButton("＋ Add files", "Choose files for the shelf", () => AddFiles(), true); DockPanel.SetDock(choose, Dock.Right); header.Children.Insert(0, choose); root.Children.Add(header);
        var drop = new Border { Background = Brush("#FF12141B"), BorderBrush = Brush("#FF343743"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), AllowDrop = true, Padding = new Thickness(8) };
        drop.DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        drop.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) AddShelfPath(f); Save(); RenderPanel(); } };
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var items = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_data.ShelfItems.Count == 0) items.Children.Add(new TextBlock { Text = "Drop files here  ·  drag out anytime  ·  click to open", Foreground = Brush("#FF898C98"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 18, 20, 18) });
        foreach (var file in _data.ShelfItems.ToArray())
        {
            var card = new Border { Width = 120, Height = 94, Background = Brush("#FF20222C"), BorderBrush = Brush("#FF373945"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(4), Padding = new Thickness(8), Cursor = Cursors.Hand, Tag = file };
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = "▧", Foreground = Brush("#FFA998FF"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = file.Name, Foreground = Brushes.White, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            card.Child = content;
            card.MouseLeftButtonUp += (_, _) => OpenFile(file.Path);
            card.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragDrop.DoDragDrop(card, new DataObject(DataFormats.FileDrop, new[] { file.Path }), DragDropEffects.Copy | DragDropEffects.Move); };
            card.MouseRightButtonUp += (_, _) => { _data.ShelfItems.Remove(file); Save(); RenderPanel(); };
            items.Children.Add(card);
        }
        scroll.Content = items; drop.Child = scroll; Grid.SetRow(drop, 1); root.Children.Add(drop);
        return root;
    }

    private void AddFiles()
    {
        using var dialog = new Forms.OpenFileDialog { Multiselect = true, Title = "Add files to Nusa shelf" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) { foreach (var file in dialog.FileNames) AddShelfPath(file); Save(); RenderPanel(); }
    }

    private void AddShelfPath(string path)
    {
        if (!File.Exists(path) || _data.ShelfItems.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        _data.ShelfItems.Add(new ShelfItem { Path = path, Name = System.IO.Path.GetFileName(path) });
    }

    private static void OpenFile(string path)
    {
        try { if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    private UIElement BuildTodo()
    {
        var root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) }); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; root.Children.Add(left);
        var done = _data.Tasks.Count(t => t.IsDone);
        left.Children.Add(new TextBlock { Text = $"{done}/{_data.Tasks.Count}", Foreground = Brushes.White, FontSize = 28, FontWeight = FontWeights.Bold });
        left.Children.Add(new TextBlock { Text = "Tasks done", Foreground = Brush("#FF9295A2"), FontSize = 11, Margin = new Thickness(0, 0, 0, 9) });
        var title = new TextBox { Style = (Style)FindResource("TextBoxStyle"), FontSize = 11, ToolTip = "New task" };
        var due = new TextBox { Style = (Style)FindResource("TextBoxStyle"), FontSize = 10, ToolTip = "Optional reminder in minutes", Text = "Due in min (optional)", Margin = new Thickness(0, 5, 0, 0) };
        due.GotKeyboardFocus += (_, _) => { if (due.Text == "Due in min (optional)") due.Clear(); };
        left.Children.Add(title); left.Children.Add(due);
        left.Children.Add(ActionButton("＋ Add task", "Add a task and optional reminder", () => AddTask(title, due), true));
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 0, 0) }; Grid.SetColumn(listScroll, 1); root.Children.Add(listScroll);
        var list = new StackPanel(); listScroll.Content = list;
        foreach (var task in _data.Tasks.OrderBy(t => t.IsDone).ThenBy(t => t.DueAt ?? DateTime.MaxValue))
        {
            var row = new DockPanel { Background = Brush(task.IsDone ? "#FF153224" : "#FF1B1D26"), Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
            var remove = new Button { Content = "×", Style = (Style)FindResource("PillButton"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4) };
            remove.Click += (_, _) => { _data.Tasks.Remove(task); Save(); RenderPanel(); }; DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var check = new CheckBox { IsChecked = task.IsDone, Style = (Style)FindResource("CheckBoxStyle"), Content = task.Title, Margin = new Thickness(8, 5, 8, 5), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            check.Checked += (_, _) => { task.IsDone = true; Save(); RenderPanel(); }; check.Unchecked += (_, _) => { task.IsDone = false; Save(); RenderPanel(); };
            var content = new StackPanel(); content.Children.Add(check);
            if (task.DueAt is not null) content.Children.Add(new TextBlock { Text = "Due " + task.DueAt.Value.ToString("ddd HH:mm"), Foreground = Brush("#FF999CA8"), FontSize = 9, Margin = new Thickness(28, -4, 0, 4) });
            row.Children.Add(content); list.Children.Add(row);
        }
        return root;
    }

    private void AddTask(TextBox input, TextBox dueInput)
    {
        var text = input.Text.Trim(); if (text.Length == 0) return;
        DateTime? dueAt = int.TryParse(dueInput.Text, out var mins) && mins > 0 ? DateTime.Now.AddMinutes(Math.Min(mins, 60 * 24 * 365)) : null;
        _data.Tasks.Add(new TodoItem { Title = text, DueAt = dueAt }); input.Clear(); dueInput.Text = "Due in min (optional)"; Save(); RenderPanel();
    }

    private UIElement BuildHeadphones()
    {
        var root = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 10, 18, 5), VerticalAlignment = VerticalAlignment.Center };
        var icon = new Border { Width = 82, Height = 82, CornerRadius = new CornerRadius(41), Background = Brush("#FF171A22"), BorderBrush = Brush("#FF353844"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "◖♪◗", FontSize = 24, Foreground = Brush("#FF91A9FF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        root.Children.Add(icon);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = "Bluetooth headphones", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock { Text = "Pair your earbuds in Windows Bluetooth settings. Battery level is available only when the device reports it to Windows.", Foreground = Brush("#FF9A9DAA"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 390, Margin = new Thickness(0, 6, 0, 10) });
        info.Children.Add(ActionButton("Open Bluetooth settings", "Open Windows Bluetooth settings", () => Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }), true));
        root.Children.Add(info); return root;
    }

    private UIElement BuildCharging()
    {
        var percent = (int)Math.Round(System.Windows.Forms.SystemInformation.PowerStatus.BatteryLifePercent * 100);
        var plugged = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Online;
        var root = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 12), VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(new TextBlock { Text = plugged ? "ϟ" : "▰", Foreground = Brush(plugged ? "#FF49D69B" : "#FFA2A5B1"), FontSize = 32, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = plugged ? "Charging" : "Battery", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock { Text = plugged ? "Charger connected" : "On battery power", Foreground = Brush("#FF9A9DAA"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) }); root.Children.Add(info);
        root.Children.Add(new TextBlock { Text = percent >= 0 ? $"{percent}%" : "—", Foreground = Brushes.White, FontSize = 27, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) });
        return root;
    }

    private UIElement BuildSystem()
    {
        var root = new UniformGrid { Columns = 3, Margin = new Thickness(8, 3, 8, 0) };
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        AddStatusCard(root, "Battery", power.BatteryLifePercent >= 0 ? $"{(int)(power.BatteryLifePercent * 100)}%" : "Unavailable", power.PowerLineStatus == Forms.PowerLineStatus.Online ? "Charging" : "On battery");
        AddStatusCard(root, "Wi-Fi / network", System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? "Connected" : "Offline", "Windows network status");
        AddStatusCard(root, "Caps Lock", Keyboard.IsKeyToggled(Key.CapsLock) ? "On" : "Off", "Keyboard indicator");
        AddStatusCard(root, "Keyboard", Forms.InputLanguage.CurrentInputLanguage?.Culture.Name ?? CultureInfo.CurrentCulture.Name, "Current input language");
        root.Children.Add(BuildVolumeCard());
        root.Children.Add(BuildBrightnessCard());
        return root;
    }

    private UIElement BuildVolumeCard()
    {
        var card = new Border { Background = Brush("#FF171922"), BorderBrush = Brush("#FF30323D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(3), Padding = new Thickness(10) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Volume", Foreground = Brush("#FF999CA8"), FontSize = 10 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        row.Children.Add(ActionButton("−", "Volume down", () => SendMediaKey(0xAE)));
        row.Children.Add(ActionButton("Mute", "Toggle mute", () => SendMediaKey(0xAD)));
        row.Children.Add(ActionButton("+", "Volume up", () => SendMediaKey(0xAF)));
        stack.Children.Add(row); card.Child = stack; return card;
    }

    private UIElement BuildBrightnessCard()
    {
        var card = new Border { Background = Brush("#FF171922"), BorderBrush = Brush("#FF30323D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(3), Padding = new Thickness(10) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Brightness", Foreground = Brush("#FF999CA8"), FontSize = 10 });
        stack.Children.Add(ActionButton("Open Windows settings", "Open display brightness settings", () => Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })));
        card.Child = stack; return card;
    }

    private UIElement AddStatusCard(Panel parent, string label, string value, string subtitle)
    {
        var card = new Border { Background = Brush("#FF171922"), BorderBrush = Brush("#FF30323D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(3), Padding = new Thickness(10) };
        var stack = new StackPanel(); stack.Children.Add(new TextBlock { Text = label, Foreground = Brush("#FF999CA8"), FontSize = 10 }); stack.Children.Add(new TextBlock { Text = value, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 1) }); stack.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("#FF858895"), FontSize = 9 }); card.Child = stack; parent.Children.Add(card); return card;
    }

    private Button ActionButton(string text, string tip, Action action, bool accent = false)
    {
        var button = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("PillButton"), Margin = new Thickness(0, 0, 6, 0), Background = accent ? Brush("#FF1C2A40") : Brush("#FF202126"), BorderBrush = accent ? Brush("#FF5078B6") : Brush("#FF34363D") };
        button.Click += (_, _) => action(); return button;
    }

    private Button MediaButton(string text, string tip, Action action, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tip,
            Style = (Style)FindResource("PillButton"),
            Width = accent ? 42 : 44,
            Height = 34,
            MinWidth = accent ? 42 : 44,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 3, 0),
            FontSize = 13,
            Background = accent ? Brush("#FF24262B") : Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
