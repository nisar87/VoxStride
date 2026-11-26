using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VoxStride.Contracts;
using VoxStride.Core.Execution;

namespace VoxStride.Desktop;

/// <summary>
/// Trusted local approval UI. Only a real click on Approve produces an <see cref="Approval"/>, bound to the exact
/// manifest digest and run. Timeouts, closing the window, cancellation and keyboard Enter all decline.
/// </summary>
public partial class ApprovalWindow : Window
{
    private readonly ApprovalRequest _request;
    private readonly DispatcherTimer _timer;
    private DateTime _deadline;
    public Approval? Result { get; private set; }
    public bool RememberRule => RememberBox.IsChecked == true;

    public ApprovalWindow(ApprovalRequest request, TimeSpan timeout)
    {
        _request = request;
        InitializeComponent();
        TitleText.Text = request.Title;
        RiskText.Text = request.Risk switch
        {
            RiskTier.High => "High impact — please read carefully.",
            RiskTier.Moderate => "This changes files on your PC. Review the exact list below.",
            _ => "Please confirm.",
        };
        LinesList.ItemsSource = request.Lines;
        if (request.Operation == StandingRuleOperations.ReversibleSameRootMove && request.Scope is not null) RememberBox.Visibility = Visibility.Visible;
        _deadline = DateTime.UtcNow + timeout;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var left = _deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) { _timer.Stop(); DialogResultSafe(false); return; }
            CountdownText.Text = $"Expires in {left.TotalSeconds:0}s";
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        // Guard against an accidental click carried over from a previous window: enable Approve after a short delay.
        ApproveButton.IsEnabled = false;
        var enable = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        enable.Tick += (_, _) => { enable.Stop(); ApproveButton.IsEnabled = true; };
        enable.Start();
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        Result = new Approval(Guid.NewGuid(), _request.RunId, _request.ManifestDigest, _request.Operation, DateTimeOffset.UtcNow.AddMinutes(2), "ui-click");
        DialogResultSafe(true);
    }

    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResultSafe(false);

    private void DialogResultSafe(bool ok)
    {
        if (!ok) Result = null;
        try { DialogResult = ok; } catch (InvalidOperationException) { Close(); }
    }
}

/// <summary>IUserApproval implementation that shows <see cref="ApprovalWindow"/> on the UI thread.</summary>
public sealed class WpfApproval(Dispatcher dispatcher, Func<string, string, Task> addRule) : IUserApproval
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public async Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var result = await dispatcher.InvokeAsync(() =>
        {
            var window = new ApprovalWindow(request, Timeout);
            using var reg = ct.Register(() => dispatcher.BeginInvoke(() => { try { window.Close(); } catch (InvalidOperationException) { } }));
            window.ShowDialog();
            return (window.Result, window.RememberRule);
        }).Task.ConfigureAwait(false);

        if (ct.IsCancellationRequested) return null;
        if (result.Result is not null && result.RememberRule && request.Scope is not null)
            await addRule(StandingRuleOperations.ReversibleSameRootMove, request.Scope).ConfigureAwait(false);
        return result.Result;
    }
}
