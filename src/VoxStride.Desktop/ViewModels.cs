using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using VoxStride.Contracts;

namespace VoxStride.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ActivityItem
{
    public required string Time { get; init; }
    public required string Text { get; init; }
    public required string Icon { get; init; }
}

/// <summary>Shared state for the overlay and the main window. Updated only on the UI dispatcher.</summary>
public sealed class AssistantViewModel : Observable
{
    private AgentState _state = AgentState.Standby;
    private string _final = "";
    private string _interim = "";
    private string _status = "Say \"Hey VoxStride\" or press Ctrl+Alt+V";
    private string _provider = "No reasoning provider";
    private string _progressText = "";
    private double _progress;
    private bool _progressVisible;
    private bool _canCancel;
    private DateTime _runStarted;

    public ObservableCollection<ActivityItem> Activity { get; } = [];

    public AgentState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            if (value == AgentState.Committed) _runStarted = DateTime.Now;
            Raise(nameof(StateLabel)); Raise(nameof(StateBrush)); Raise(nameof(IsListening)); Raise(nameof(ProceedVisible)); Raise(nameof(IsBusy)); Raise(nameof(PauseVisible));
        }
    }

    public string StateLabel => _state switch
    {
        AgentState.Standby => "Ready",
        AgentState.Listening => "Listening — pauses are fine",
        AgentState.Finalizing => "Finalizing your instruction…",
        AgentState.Clarifying => "Waiting for you",
        AgentState.Committed => "Got it",
        AgentState.Planning => "Planning",
        AgentState.AwaitingApproval => "Needs your approval",
        AgentState.Executing => "Working",
        AgentState.Verifying => "Verifying",
        AgentState.Speaking => "Done",
        AgentState.Muted => "Microphone muted",
        AgentState.Paused => "Paused (screen locked)",
        AgentState.Cancelled => "Cancelled",
        AgentState.Failed => "Couldn't finish",
        _ => _state.ToString(),
    };

    public Brush StateBrush => new SolidColorBrush(_state switch
    {
        AgentState.Listening or AgentState.Finalizing => Color.FromRgb(0x10, 0xB9, 0x81),
        AgentState.Clarifying or AgentState.AwaitingApproval => Color.FromRgb(0xF5, 0x9E, 0x0B),
        AgentState.Planning or AgentState.Executing or AgentState.Verifying or AgentState.Committed => Color.FromRgb(0x63, 0x66, 0xF1),
        AgentState.Failed => Color.FromRgb(0xEF, 0x44, 0x44),
        AgentState.Muted or AgentState.Paused => Color.FromRgb(0x9C, 0xA3, 0xAF),
        _ => Color.FromRgb(0x14, 0xB8, 0xA6),
    });

    public bool IsListening => _state is AgentState.Listening or AgentState.Clarifying;
    public Visibility ProceedVisible => IsListening ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PauseVisible => IsBusy || _state == AgentState.Paused ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy => _state is AgentState.Committed or AgentState.Planning or AgentState.Executing or AgentState.Verifying or AgentState.AwaitingApproval;

    public string FinalTranscript { get => _final; set { if (Set(ref _final, value)) Raise(nameof(HasTranscript)); } }
    public string InterimTranscript { get => _interim; set { if (Set(ref _interim, value)) Raise(nameof(HasTranscript)); } }
    public bool HasTranscript => _final.Length + _interim.Length > 0;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ProviderLabel { get => _provider; set => Set(ref _provider, value); }
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool ProgressVisible { get => _progressVisible; set => Set(ref _progressVisible, value); }
    public bool CanCancel { get => _canCancel; set => Set(ref _canCancel, value); }
    public string Elapsed => IsBusy ? $"{(DateTime.Now - _runStarted).TotalSeconds:0}s" : "";

    public void TickElapsed() => Raise(nameof(Elapsed));

    public void AddActivity(string text, string icon = "•")
    {
        Activity.Insert(0, new ActivityItem { Time = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), Text = text, Icon = icon });
        while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
    }

    public void Apply(TaskEvent e)
    {
        if (e.Phase is "draft") { Status = "Draft: " + e.Message; return; }
        if (e.State != AgentState.Standby || e.Phase is "done" or "recovery" or "wake-unavailable") Status = e.Message;
        CanCancel = e.CanCancel && e.State is not (AgentState.Standby or AgentState.Speaking or AgentState.Failed or AgentState.Cancelled);
        if (e.Total is > 0 && e.Completed is { } c)
        {
            ProgressVisible = true;
            Progress = 100.0 * c / e.Total.Value;
            ProgressText = $"{c} of {e.Total}";
        }
        else if (e.Phase is "done" or "result") { ProgressVisible = false; ProgressText = ""; }
        if (e.Phase is "ack" or "step-result" or "result" or "approval" or "clarify" or "no-provider" or "cancelled" or "recovery" or "wake-unavailable" or "speech-hint" or "preview")
            AddActivity(e.Message, e.Phase switch { "result" => "✔", "cancelled" => "■", "approval" => "?", "recovery" => "!", "speech-hint" => "🎙", _ => "•" });
    }
}
