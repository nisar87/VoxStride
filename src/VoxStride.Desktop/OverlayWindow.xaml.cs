using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using VoxStride.Contracts;

namespace VoxStride.Desktop;

/// <summary>
/// Compact floating assistant. Never steals focus (WS_EX_NOACTIVATE) so acknowledging a command can't disturb
/// the window the user is working in; it is also excluded from VoxStride's own window targeting.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly App _app;

    public OverlayWindow(App app, AssistantViewModel vm)
    {
        _app = app;
        Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => PositionTopRight();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE);
            SetWindowLongPtr(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void PositionTopRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Top + 16;
    }

    private void Proceed_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "overlay Proceed"); _app.Host?.Conversation.PressProceed(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "overlay Cancel"); _app.Host?.Conversation.PressCancel(); }
    private void Pause_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "overlay Pause"); _app.TogglePause(); PauseButton.Content = _app.Host?.Runs.IsPaused == true ? "Resume" : "Pause"; }
    private void Mute_Click(object sender, RoutedEventArgs e) { Log.Info("ui", "overlay Mute"); _app.ToggleMute(); }

    public void SetMuted(bool muted) => MuteButton.Content = muted ? "Unmute" : "Mute";

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int i, nint v);
}
