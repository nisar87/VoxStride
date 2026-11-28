using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoxStride.Contracts;
using VoxStride.Core.Live;

namespace VoxStride.Desktop;

/// <summary>
/// The on-screen companion: an original animated character with a talk bar, always on top, draggable and
/// resizable (Ctrl + mouse wheel). It shows clearly whether VoxStride is listening, hearing you, thinking, working or
/// talking: the headset lights, the face (leaning in, looking up, moving its mouth with its own voice) and the status
/// line all follow the live conversation.
/// </summary>
public partial class CompanionWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private readonly App _app;
    private readonly Random _rng = new();
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly DispatcherTimer _bubbleTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private readonly DispatcherTimer _frame;
    private LiveCompanion? _companion;
    private CompanionState _state = CompanionState.Off;
    private double _nextBlink = 2, _blinkUntil = -1, _mouth, _micLevel;

    public CompanionWindow(App app)
    {
        _app = app;
        InitializeComponent();
        var s = app.Settings;
        ApplyScale(s.CompanionScale);
        Loaded += (_, _) => PlaceWindow();
        // Animation on a timer rather than every display frame: smooth while talking or listening closely, slow and cheap
        // while idle (CompositionTarget.Rendering kept the CPU busy even when nothing was happening).
        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _frame.Tick += Animate;
        IsVisibleChanged += (_, _) => { if (IsVisible) _frame.Start(); else _frame.Stop(); };
        _bubbleTimer.Tick += (_, _) => { _bubbleTimer.Stop(); if (AskButtons.Visibility != Visibility.Visible) Bubble.Visibility = Visibility.Collapsed; };
        Input.TextChanged += (_, _) => Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewMouseWheel += OnWheel;
        LocationChanged += (_, _) => { if (IsLoaded) { _app.Settings.CompanionLeft = Left; _app.Settings.CompanionTop = Top; } };
        ShowUnavailable();
    }

    // ---------------- wiring ----------------

    public void Attach(LiveCompanion? companion)
    {
        if (_companion is not null)
        {
            _companion.StateChanged -= OnState;
            _companion.Caption -= OnCaption;
            _companion.Activity -= OnActivity;
        }
        _companion = companion;
        if (companion is null) { ShowUnavailable(); return; }
        companion.StateChanged += OnState;
        companion.Caption += OnCaption;
        companion.Activity += OnActivity;
        Dispatcher.BeginInvoke(() => ApplyState(companion.State));
    }

    public void ShowQuestion(string question)
    {
        BubbleText.Text = question;
        AskButtons.Visibility = Visibility.Visible;
        Bubble.Visibility = Visibility.Visible;
        _bubbleTimer.Stop();
    }

    public void ClearQuestion()
    {
        AskButtons.Visibility = Visibility.Collapsed;
        _bubbleTimer.Stop(); _bubbleTimer.Start();
    }

    private void OnState(CompanionState s) => Dispatcher.BeginInvoke(() => ApplyState(s));
    private void OnActivity(string what, bool started) => Dispatcher.BeginInvoke(() => { if (started) StatusText.Text = what + "…"; });

    private void OnCaption(CompanionCaption c) => Dispatcher.BeginInvoke(() =>
    {
        switch (c.Speaker)
        {
            case CaptionSpeaker.User:
                if (Input.Text.Length == 0) Placeholder.Text = "You: " + c.Text;
                break;
            case CaptionSpeaker.Assistant or CaptionSpeaker.System:
                if (AskButtons.Visibility == Visibility.Visible) break;
                BubbleText.Text = c.Text;
                Bubble.Visibility = Visibility.Visible;
                _bubbleTimer.Stop();
                if (c.TurnEnded) _bubbleTimer.Start();
                break;
        }
    });

    private void ShowUnavailable()
    {
        _state = CompanionState.Off;
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = _app.Host?.CanTalkLive == true ? "Click the mic to talk" : "Add a Gemini key to talk (click the mic)";
        SetLeds(Color.FromRgb(0x80, 0x80, 0x90));
    }

    private void ApplyState(CompanionState s)
    {
        _state = s;
        var muted = _companion?.MicMuted == true;
        var (text, color) = s switch
        {
            CompanionState.Connecting => ("Connecting…", Color.FromRgb(0xF5, 0xB7, 0x3B)),
            CompanionState.Reconnecting => ("Reconnecting…", Color.FromRgb(0xF5, 0xB7, 0x3B)),
            CompanionState.Listening => (muted ? "Mic muted — click the mic to talk" : "Listening…", muted ? Color.FromRgb(0xE5, 0x48, 0x4D) : Color.FromRgb(0x3C, 0xE0, 0x8C)),
            CompanionState.Hearing => ("I'm listening", Color.FromRgb(0x3C, 0xE0, 0x8C)),
            CompanionState.Thinking => ("Thinking…", Color.FromRgb(0xF5, 0xB7, 0x3B)),
            CompanionState.Speaking => ("Talking — just speak to interrupt", Color.FromRgb(0x4F, 0x9B, 0xFF)),
            CompanionState.Working => ("Working on it…", Color.FromRgb(0xA0, 0x7C, 0xFF)),
            CompanionState.AwaitingYes => ("Say \"yes\" or \"no\"", Color.FromRgb(0xF5, 0xB7, 0x3B)),
            CompanionState.Error => ("Couldn't connect — click the mic to retry", Color.FromRgb(0xE5, 0x48, 0x4D)),
            _ => (_app.Host?.CanTalkLive == true ? "Click the mic to talk" : "Add a Gemini key to talk (click the mic)", Color.FromRgb(0x80, 0x80, 0x90)),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);
        SetLeds(color);
        MicGlyph.Text = muted || s is CompanionState.Off or CompanionState.Error ? "" : ""; // mic off / mic
        MicButton.Background = new SolidColorBrush(muted ? Color.FromRgb(0xE5, 0x48, 0x4D) : s is CompanionState.Off or CompanionState.Error ? Color.FromRgb(0x55, 0x55, 0x66) : Color.FromRgb(0x7C, 0x5C, 0xFF));
        MicButton.ToolTip = s is CompanionState.Off or CompanionState.Error ? "Start talking (Ctrl+Alt+V)" : muted ? "Unmute (Ctrl+Alt+M)" : "Mute the mic (Ctrl+Alt+M)";
        ThinkDots.Visibility = s is CompanionState.Thinking or CompanionState.Working or CompanionState.Connecting ? Visibility.Visible : Visibility.Collapsed;
        if (s != CompanionState.AwaitingYes && AskButtons.Visibility == Visibility.Visible) ClearQuestion();
    }

    private void SetLeds(Color c)
    {
        var b = new SolidColorBrush(c);
        LedL.Fill = b; LedR.Fill = b;
    }

    /// <summary>Self-test: show a state with a given mouth opening and bubble, then render the window to a bitmap.</summary>
    internal System.Windows.Media.Imaging.BitmapSource Snapshot(CompanionState state, double mouth, string? bubble, bool ask, bool muted = false)
    {
        _forcedMouth = mouth;
        ApplyState(state);
        if (muted) { MicButton.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)); MicGlyph.Text = "\uEC54"; StatusText.Text = "Mic muted — click the mic to talk"; StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)); }
        if (ask && bubble is not null) ShowQuestion(bubble);
        else if (bubble is not null) { AskButtons.Visibility = Visibility.Collapsed; BubbleText.Text = bubble; Bubble.Visibility = Visibility.Visible; }
        else Bubble.Visibility = Visibility.Collapsed;
        for (var i = 0; i < 40; i++) Animate(null, EventArgs.Empty); // settle the eased animations
        UpdateLayout();
        var w = (int)Math.Ceiling(Root.ActualWidth); var h = (int)Math.Ceiling(Root.ActualHeight);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Root);
        _forcedMouth = null;
        return bmp;
    }

    private double? _forcedMouth;

    // ---------------- animation ----------------

    private void Animate(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var busy = _state is CompanionState.Hearing or CompanionState.Speaking or CompanionState.Thinking or CompanionState.Working or CompanionState.AwaitingYes or CompanionState.Connecting or CompanionState.Reconnecting;
        var wanted = TimeSpan.FromMilliseconds(busy ? 40 : 100); // 25 fps when something is happening, 10 fps at rest
        if (_frame.Interval != wanted) _frame.Interval = wanted;
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        if (_forcedMouth is not null) { _blinkUntil = -1; _nextBlink = Math.Max(_nextBlink, t + 5); } // eyes open in snapshots

        // breathing and a gentle sway
        Breath.Y = Math.Sin(t * 2 * Math.PI / 3.6) * 2.2;
        var tiltTarget = _state switch
        {
            CompanionState.Hearing => -5.0,                     // leaning in to listen
            CompanionState.Thinking or CompanionState.Working => 4.0,
            CompanionState.Speaking => Math.Sin(t * 2.3) * 2.5,
            _ => Math.Sin(t * 0.7) * 1.2,
        };
        HeadTilt.Angle += (tiltTarget - HeadTilt.Angle) * (busy ? 0.15 : 0.3);

        // blinking
        if (t >= _nextBlink) { _blinkUntil = t + 0.13; _nextBlink = t + 2.5 + _rng.NextDouble() * 3.5; if (_rng.NextDouble() < 0.15) _nextBlink = t + 0.3; }
        var lid = t < _blinkUntil ? 0.12 : 1.0;
        BlinkL.ScaleY = lid; BlinkR.ScaleY = lid;

        // eyes: look up while thinking, otherwise follow the mouse a little
        double lx = 0, ly = 0;
        if (_state is CompanionState.Thinking or CompanionState.Working) { lx = 2; ly = -3.5; }
        else if (GetCursorPos(out var p))
        {
            try
            {
                var centre = Character.PointToScreen(new Point(120, 118));
                var dx = p.X - centre.X; var dy = p.Y - centre.Y;
                var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                lx = dx / d * Math.Min(3.5, d / 60); ly = dy / d * Math.Min(3, d / 60);
            }
            catch (InvalidOperationException) { }
        }
        LookL.X += (lx - LookL.X) * 0.2; LookL.Y += (ly - LookL.Y) * 0.2;
        LookR.X = LookL.X; LookR.Y = LookL.Y;

        // eyebrows: raised when listening closely, one raised while thinking
        var browTarget = _state switch { CompanionState.Hearing => -3.0, CompanionState.Listening => -1.0, _ => 0.0 };
        BrowLUp.Y += (browTarget - BrowLUp.Y) * 0.15;
        BrowRUp.Y += ((_state is CompanionState.Thinking ? -4.0 : browTarget) - BrowRUp.Y) * 0.15;

        // lip sync with VoxStride's own voice
        var level = _forcedMouth ?? (_state == CompanionState.Speaking || _companion?.OutputLevel > 0.02 ? _companion?.OutputLevel ?? 0 : 0);
        _mouth = _forcedMouth ?? _mouth + (level - _mouth) * (level > _mouth ? 0.6 : 0.25);
        if (_mouth > 0.04)
        {
            Smile.Visibility = Visibility.Collapsed;
            MouthOpen.Visibility = Visibility.Visible;
            MouthOpen.Height = 3 + _mouth * 17;
            MouthOpen.Width = 15 + _mouth * 7;
            Canvas.SetLeft(MouthOpen, 120 - MouthOpen.Width / 2);
            Canvas.SetTop(MouthOpen, 149 - _mouth * 3);
        }
        else { Smile.Visibility = Visibility.Visible; MouthOpen.Visibility = Visibility.Collapsed; }

        // the mic ring and headset lights pulse with the user's voice
        var input = _companion is { IsOn: true, MicMuted: false } ? _companion.InputLevel : 0;
        _micLevel += (input - _micLevel) * 0.3;
        MicRing.Opacity = 0.25 + Math.Min(0.75, _micLevel * 3);
        MicRing.Stroke = StatusDot.Fill;
        var ledPulse = _state switch
        {
            CompanionState.Hearing => 0.6 + Math.Min(0.4, _micLevel * 4),
            CompanionState.Thinking or CompanionState.Working or CompanionState.Connecting or CompanionState.Reconnecting or CompanionState.AwaitingYes => 0.55 + 0.45 * Math.Abs(Math.Sin(t * 3)),
            CompanionState.Speaking => 0.6 + _mouth * 0.4,
            _ => 0.9,
        };
        LedL.Opacity = ledPulse; LedR.Opacity = ledPulse;

        if (ThinkDots.Visibility == Visibility.Visible)
        {
            Dot1.Opacity = 0.3 + 0.7 * Math.Max(0, Math.Sin(t * 5));
            Dot2.Opacity = 0.3 + 0.7 * Math.Max(0, Math.Sin(t * 5 - 1));
            Dot3.Opacity = 0.3 + 0.7 * Math.Max(0, Math.Sin(t * 5 - 2));
        }
    }

    // ---------------- interaction ----------------

    private void Character_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { _ = _app.ToggleCompanionAsync(); return; }
        try { DragMove(); } catch (InvalidOperationException) { }
        _app.Settings.CompanionLeft = Left; _app.Settings.CompanionTop = Top;
        _app.Settings.Save();
    }

    private void Character_RightClick(object sender, MouseButtonEventArgs e) => ShowMenu();
    private void Menu_Click(object sender, RoutedEventArgs e) => ShowMenu();

    private void ShowMenu()
    {
        var on = _companion?.IsOn == true;
        var menu = new ContextMenu();
        void Add(string header, Action a) { var i = new MenuItem { Header = header }; i.Click += (_, _) => a(); menu.Items.Add(i); }
        Add(on ? "End conversation" : "Start talking", () => _ = _app.ToggleCompanionAsync());
        if (on) Add(_companion!.MicMuted ? "Unmute microphone" : "Mute microphone", () => _app.ToggleCompanionMute());
        Add(_app.Settings.LiveUsingSpeakers ? "✓ I'm using speakers (no headset)" : "I'm using speakers (no headset)", () =>
        {
            _app.Settings.LiveUsingSpeakers = !_app.Settings.LiveUsingSpeakers; _app.Settings.Save();
            if (on) _ = _app.RestartCompanionAsync();
        });
        menu.Items.Add(new Separator());
        Add("Bigger", () => ApplyScale(_app.Settings.CompanionScale + 0.15, save: true));
        Add("Smaller", () => ApplyScale(_app.Settings.CompanionScale - 0.15, save: true));
        Add("Settings", () => _app.ShowMain(2));
        Add("Hide character (open from the tray)", () => { _app.Settings.CompanionVisible = false; _app.Settings.Save(); Hide(); });
        menu.IsOpen = true;
    }

    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Host?.CanTalkLive != true) { _app.ShowMain(3); return; }
        if (_companion?.IsOn == true) _app.ToggleCompanionMute();
        else _ = _app.StartCompanionAsync();
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendTyped();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendTyped(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Input.Text = ""; e.Handled = true; }
    }

    private async void SendTyped()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Input.Text = "";
        if (_companion?.IsOn != true) await _app.StartCompanionAsync();
        if (_companion is { IsOn: true } c) await c.SendTextAsync(text);
    }

    private void Yes_Click(object sender, RoutedEventArgs e) { _app.Host?.LiveApprovals.AnswerByVoice(true); ClearQuestion(); }
    private void No_Click(object sender, RoutedEventArgs e) { _app.Host?.LiveApprovals.AnswerByVoice(false); ClearQuestion(); }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ApplyScale(_app.Settings.CompanionScale + (e.Delta > 0 ? 0.08 : -0.08), save: true);
        e.Handled = true;
    }

    private void ApplyScale(double scale, bool save = false)
    {
        scale = Math.Clamp(scale, 0.55, 1.8);
        RootScale.ScaleX = scale; RootScale.ScaleY = scale;
        Width = 360 * scale; Height = 560 * scale;
        _app.Settings.CompanionScale = scale;
        if (save) _app.Settings.Save();
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        var s = _app.Settings;
        if (s.CompanionLeft is { } l && s.CompanionTop is { } t && double.IsFinite(l) && double.IsFinite(t)
            && l > SystemParameters.VirtualScreenLeft - 50 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
            && t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        { Left = l; Top = t; return; }
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height;
    }

    protected override void OnClosed(EventArgs e)
    {
        _frame.Stop();
        base.OnClosed(e);
    }
}
