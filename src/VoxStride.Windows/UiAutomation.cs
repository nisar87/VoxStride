using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using VoxStride.Contracts;
using VoxStride.Core.Tools;

namespace VoxStride.Windows;

/// <summary>
/// Windows UI Automation for the ui_* / apps_*window tools (spec P4).
/// <list type="bullet">
/// <item>All UIA calls run on a dedicated MTA worker thread with a timeout. If an app hangs a call, that worker is
/// abandoned and a fresh one takes over, so VoxStride's UI never freezes.</item>
/// <item>Windows are identified by handle + process id + process start time; a closed or reused handle is detected.</item>
/// <item>Snapshots are bounded (count, depth, time). Element references expire with the snapshot.</item>
/// <item>Typing and clicking verify the target window is in front first and stop if the user switches windows or
/// moves the mouse; clicks also require the window to be where the screenshot saw it and the point not covered.</item>
/// </list>
/// </summary>
public sealed class WindowsUiAutomation : IUiAutomation, IScreenCapture, IDisposable
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);
    private readonly ConcurrentDictionary<string, DateTime> _startTimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, AutomationElement>> _elements = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _nextElementNumber = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (UiNative.RECT Rect, double Scale)> _captures = new(StringComparer.Ordinal);
    private readonly int _ownPid = Environment.ProcessId;
    private Worker _worker = new();
    private readonly Lock _workerGate = new();

    // ---------------- worker ----------------
    private sealed class Worker : IDisposable
    {
        public readonly BlockingCollection<Action> Queue = new();
        public Worker()
        {
            var t = new Thread(() => { foreach (var a in Queue.GetConsumingEnumerable()) a(); }) { IsBackground = true, Name = "VoxStride UIA worker" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }
        public void Dispose() => Queue.CompleteAdding();
    }

    private async Task<T> Run<T>(string what, Func<T> work, CancellationToken ct, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Worker w;
        lock (_workerGate) w = _worker;
        w.Queue.Add(() => { try { tcs.TrySetResult(work()); } catch (Exception ex) { tcs.TrySetException(ex); } }, ct);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? CallTimeout, ct)).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            ct.ThrowIfCancellationRequested();
            Log.Warn("ui", $"UI Automation call '{what}' timed out; replacing the worker thread");
            lock (_workerGate) { if (ReferenceEquals(_worker, w)) { _worker = new Worker(); w.Dispose(); } }
            throw new TimeoutException("The app isn't responding to accessibility requests right now.");
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    // ---------------- windows ----------------
    private static string IdOf(nint h, uint pid) => $"w{(long)h:x}-{pid}";

    private static bool TryParse(string id, out nint hwnd, out uint pid)
    {
        hwnd = 0; pid = 0;
        var dash = id.IndexOf('-', StringComparison.Ordinal);
        return id.StartsWith('w') && dash > 1 && long.TryParse(id[1..dash], System.Globalization.NumberStyles.HexNumber, null, out var h) && uint.TryParse(id[(dash + 1)..], out pid) && (hwnd = (nint)h) != 0;
    }

    private static DateTime StartOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.StartTime.ToUniversalTime(); }
        catch (Exception) { return DateTime.MinValue; } // elevated/protected process: identity by handle+pid only
    }

    private static string ProcessNameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch (Exception) { return "unknown"; }
    }

    private UiWindow Describe(nint h, uint pid)
    {
        UiNative.GetWindowRect(h, out var r);
        var id = IdOf(h, pid);
        var start = _startTimes.GetOrAdd(id, _ => StartOf(pid));
        return new UiWindow(id, h, (int)pid, start, Native.Title(h), ProcessNameOf(pid), Native.GetForegroundWindow() == h, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public Task<IReadOnlyList<UiWindow>> ListWindowsAsync(CancellationToken ct)
    {
        var list = new List<UiWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (!WindowsAppController.IsAppWindow(h) || UiNative.IsCloaked(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid != 0 && pid != _ownPid) list.Add(Describe(h, pid));
            return true;
        }, 0);
        Log.Debug("ui", $"list windows: {list.Count}");
        return Task.FromResult<IReadOnlyList<UiWindow>>(list);
    }

    public Task<UiWindow?> GetWindowAsync(string windowId, CancellationToken ct) => Task.FromResult(Resolve(windowId));

    private UiWindow? Resolve(string windowId)
    {
        if (!TryParse(windowId, out var h, out var pid) || !Native.IsWindow(h)) return null;
        Native.GetWindowThreadProcessId(h, out var now);
        if (now != pid || pid == _ownPid) return null;
        var w = Describe(h, pid);
        if (_startTimes.TryGetValue(windowId, out var seen) && seen != DateTime.MinValue && StartOf(pid) != seen) return null; // handle reused by a new process
        return w;
    }

    public async Task<UiActionResult> FocusAsync(string windowId, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return new UiActionResult(false, "window_gone", "That window has closed.");
        var h = (nint)w.Hwnd;
        if (UiNative.IsIconic(h)) Native.ShowWindow(h, UiNative.SW_RESTORE);
        if (!Native.SetForegroundWindow(h))
        {
            // Windows only lets the foreground app hand over focus; a synthetic Alt press is the documented way to qualify.
            UiNative.TapAlt();
            Native.SetForegroundWindow(h);
        }
        for (var i = 0; i < 10 && Native.GetForegroundWindow() != h; i++) await Task.Delay(50, ct).ConfigureAwait(false);
        var ok = Native.GetForegroundWindow() == h;
        Log.Info("ui", $"focus {w.ProcessName} \"{w.Title}\" -> {(ok ? "in front" : "Windows refused")}");
        return ok ? new UiActionResult(true, Message: $"{w.ProcessName} is in front.", Verified: true)
                  : new UiActionResult(false, "focus_refused", "Windows didn't let me bring that window to the front. Click it once, then try again.");
    }

    // ---------------- observe ----------------
    private static readonly AutomationProperty[] Props =
    [
        AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.AutomationIdProperty, AutomationElement.IsEnabledProperty,
        AutomationElement.IsPasswordProperty, AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty, AutomationElement.IsSelectionItemPatternAvailableProperty,
        AutomationElement.IsExpandCollapsePatternAvailableProperty, AutomationElement.IsValuePatternAvailableProperty,
    ];

    public async Task<UiSnapshot?> ObserveAsync(string windowId, string? nameFilter, int maxElements, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return null;
        try
        {
            return await Run("observe", () => ObserveCore(w, nameFilter, Math.Clamp(maxElements, 10, 400)), ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or ElementNotAvailableException or COMException)
        {
            Log.Warn("ui", $"observe {w.ProcessName} failed: {ex.Message}");
            return null;
        }
    }

    private UiSnapshot ObserveCore(UiWindow w, string? filter, int max)
    {
        var sw = Stopwatch.StartNew();
        var cr = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var p in Props) cr.Add(p);
        var map = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var list = new List<UiElement>();
        var truncated = false;
        // Element ids are never reused for a window: an id from an older observation can't silently point at a different control.
        var number = _nextElementNumber.GetOrAdd(w.Id, 1);
        using (cr.Activate())
        {
            var root = AutomationElement.FromHandle((nint)w.Hwnd);
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement El, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.Count > 0)
            {
                if (list.Count >= max || visited > 3000 || sw.Elapsed > TimeSpan.FromSeconds(6)) { truncated = true; break; }
                var (el, depth) = queue.Dequeue();
                visited++;
                if (depth > 0) TryAdd(el, depth, filter, map, list, ref number);
                if (depth >= 14) continue;
                AutomationElement? child;
                try { child = walker.GetFirstChild(el, cr); } catch (ElementNotAvailableException) { continue; }
                while (child is not null)
                {
                    queue.Enqueue((child, depth + 1));
                    try { child = walker.GetNextSibling(child, cr); } catch (ElementNotAvailableException) { break; }
                }
            }
        }
        _elements[w.Id] = map;
        _nextElementNumber[w.Id] = number > 900_000 ? 1 : number;
        Log.Info("ui", $"observe {w.ProcessName} elements={list.Count} truncated={truncated} ms={sw.ElapsedMilliseconds}");
        return new UiSnapshot(w.Id, w.Title, list, truncated, DateTimeOffset.UtcNow);
    }

    private static void TryAdd(AutomationElement el, int depth, string? filter, Dictionary<string, AutomationElement> map, List<UiElement> list, ref int number)
    {
        try
        {
            var c = el.Cached;
            if (c.IsOffscreen && string.IsNullOrEmpty(c.Name)) return;
            var name = c.Name ?? "";
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            var role = c.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal).ToLowerInvariant();
            var actions = new List<string>();
            if ((bool)el.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)) actions.Add("invoke");
            if ((bool)el.GetCachedPropertyValue(AutomationElement.IsTogglePatternAvailableProperty)) actions.Add("toggle");
            if ((bool)el.GetCachedPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty)) actions.Add("select");
            if ((bool)el.GetCachedPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty)) { actions.Add("expand"); actions.Add("collapse"); }
            string? value = null;
            if ((bool)el.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty))
            {
                try
                {
                    var vp = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
                    if (!vp.Current.IsReadOnly) actions.Add("setvalue");
                    if (!c.IsPassword) { value = vp.Current.Value; if (value.Length > 200) value = value[..200] + "…"; }
                }
                catch (InvalidOperationException) { }
            }
            if (c.IsKeyboardFocusable) actions.Add("focus");
            if (name.Length == 0 && actions.Count == 0 && value is null) return; // structural containers add noise
            var r = c.BoundingRectangle;
            var id = "e" + (number++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            map[id] = el;
            list.Add(new UiElement(id, role, name.Length > 120 ? name[..120] + "…" : name, string.IsNullOrEmpty(c.AutomationId) ? null : c.AutomationId, value, c.IsEnabled, c.IsPassword, actions,
                double.IsInfinity(r.X) ? 0 : (int)r.X, double.IsInfinity(r.Y) ? 0 : (int)r.Y, (int)r.Width, (int)r.Height, depth));
        }
        catch (ElementNotAvailableException) { }
    }

    private AutomationElement? ElementOf(UiWindow w, string elementId, out string? error)
    {
        error = null;
        if (!_elements.TryGetValue(w.Id, out var map) || !map.TryGetValue(elementId, out var el)) { error = "Observe the window again first."; return null; }
        try
        {
            if (el.Current.ProcessId != w.ProcessId) { error = "That control no longer belongs to the window. Observe again."; return null; }
            return el;
        }
        catch (ElementNotAvailableException) { error = "That control is gone (the window changed). Observe again."; return null; }
    }

    // ---------------- actions ----------------
    public async Task<UiActionResult> InvokeAsync(string windowId, string elementId, string action, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return new UiActionResult(false, "window_gone", "That window has closed.");
        try
        {
            return await Run("invoke", () =>
            {
                var el = ElementOf(w, elementId, out var err);
                if (el is null) return new UiActionResult(false, "element_stale", err);
                var name = el.Current.Name;
                switch (action)
                {
                    case "invoke": ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return new UiActionResult(true, Message: $"Pressed {name}.");
                    case "toggle":
                    {
                        var tp = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
                        var before = tp.Current.ToggleState;
                        tp.Toggle();
                        var after = tp.Current.ToggleState;
                        return new UiActionResult(after != before, after != before ? null : "unchanged", $"{name} is now {after.ToString().ToLowerInvariant()}.", after.ToString(), after != before);
                    }
                    case "select":
                    {
                        var sp = (SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern);
                        sp.Select();
                        return new UiActionResult(sp.Current.IsSelected, sp.Current.IsSelected ? null : "unchanged", $"Selected {name}.", null, sp.Current.IsSelected);
                    }
                    case "expand" or "collapse":
                    {
                        var ep = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                        if (action == "expand") ep.Expand(); else ep.Collapse();
                        var state = ep.Current.ExpandCollapseState;
                        var ok = action == "expand" ? state != ExpandCollapseState.Collapsed : state == ExpandCollapseState.Collapsed;
                        return new UiActionResult(ok, ok ? null : "unchanged", $"{name} {action}ed.", state.ToString(), ok);
                    }
                    case "focus": el.SetFocus(); return new UiActionResult(true, Message: $"{name} has focus.", Verified: true);
                    default: return new UiActionResult(false, "action_unsupported", "Unknown action.");
                }
            }, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Some apps block the call while a dialog opens: the press probably happened. Never retry automatically.
            return new UiActionResult(false, "app_busy_after_action", "The app stopped responding right after that (maybe a dialog opened). I won't repeat it — look at the screen, or observe the window again.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return new UiActionResult(false, "action_failed", "That control didn't accept it: " + ex.Message);
        }
    }

    public async Task<UiActionResult> SetValueAsync(string windowId, string elementId, string text, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return new UiActionResult(false, "window_gone", "That window has closed.");
        try
        {
            return await Run("set value", () =>
            {
                var el = ElementOf(w, elementId, out var err);
                if (el is null) return new UiActionResult(false, "element_stale", err);
                if (el.Current.IsPassword) return new UiActionResult(false, "password_field", "I never fill in password fields.");
                var vp = (ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern);
                if (vp.Current.IsReadOnly) return new UiActionResult(false, "read_only", "That field is read-only.");
                vp.SetValue(text);
                return new UiActionResult(true, NewValue: vp.Current.Value);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ElementNotAvailableException or COMException or UnauthorizedAccessException)
        {
            return new UiActionResult(false, "set_failed", ex is UnauthorizedAccessException ? "That app runs as administrator, so Windows doesn't let me type into it." : "I couldn't set that text: " + ex.Message);
        }
    }

    public async Task<UiActionResult> TypeTextAsync(string windowId, string elementId, string text, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return new UiActionResult(false, "window_gone", "That window has closed.");
        var h = (nint)w.Hwnd;
        try
        {
            var focused = await Run("focus for typing", () =>
            {
                var el = ElementOf(w, elementId, out var err);
                if (el is null) return err;
                if (el.Current.IsPassword) return "I never type into password fields.";
                el.SetFocus();
                return null;
            }, ct).ConfigureAwait(false);
            if (focused is not null) return new UiActionResult(false, "element_stale", focused);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return new UiActionResult(false, "focus_failed", "I couldn't put the cursor in that field: " + ex.Message);
        }
        if (Native.GetForegroundWindow() != h && !(await FocusAsync(windowId, ct).ConfigureAwait(false)).Ok)
            return new UiActionResult(false, "not_foreground", "That window isn't in front, so I didn't type anything.");
        UiNative.GetCursorPos(out var startCursor);
        var typed = 0;
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (typed % 8 == 0)
            {
                // Focus guard: stop the moment the user takes over.
                UiNative.GetCursorPos(out var now);
                if (Native.GetForegroundWindow() != h) return new UiActionResult(false, "focus_changed", $"You switched windows, so I stopped after {typed} of {text.Length} characters.");
                if (Math.Abs(now.X - startCursor.X) > 3 || Math.Abs(now.Y - startCursor.Y) > 3) return new UiActionResult(false, "user_took_over", $"You moved the mouse, so I stopped after {typed} of {text.Length} characters.");
            }
            UiNative.TypeChar(ch);
            typed++;
            if (typed % 40 == 0) await Task.Delay(5, ct).ConfigureAwait(false);
        }
        // Verify where the control supports reading its value. SendInput is asynchronous: the app may still be
        // processing the keystrokes, so poll briefly instead of reading once.
        var verified = false;
        var expected = text.ReplaceLineEndings("\n").Split('\n')[^1];
        for (var attempt = 0; attempt < 15 && !verified; attempt++)
        {
            if (attempt > 0) await Task.Delay(100, ct).ConfigureAwait(false);
            try
            {
                verified = await Run("verify typing", () =>
                {
                    var el = ElementOf(w, elementId, out _);
                    return el is not null && el.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && ((ValuePattern)p).Current.Value.Contains(expected, StringComparison.Ordinal);
                }, ct).ConfigureAwait(false);
            }
            catch (Exception) { break; } // control can't be read back: report unverified honestly
        }
        Log.Info("ui", $"typed {typed} chars into {w.ProcessName} verified={verified}");
        return new UiActionResult(true, Message: verified ? "Typed and checked." : "Typed (this control doesn't let me read it back to check).", Verified: verified);
    }

    // ---------------- vision fallback ----------------
    public Task<UiCapture?> CaptureAsync(string windowId, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return Task.FromResult<UiCapture?>(null);
        var h = (nint)w.Hwnd;
        if (UiNative.IsIconic(h)) return Task.FromResult<UiCapture?>(null);
        UiNative.GetWindowRect(h, out var r);
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) return Task.FromResult<UiCapture?>(null);
        var pixels = UiNative.CaptureWindow(h, width, height);
        if (pixels is null) return Task.FromResult<UiCapture?>(null);
        var scale = Math.Min(1.0, 1280.0 / width);
        var (png, pw, ph) = Png.Encode(pixels, width, height, scale);
        var id = "cap-" + Guid.NewGuid().ToString("N")[..12];
        _captures[id] = (r, scale);
        Log.Info("ui", $"captured {w.ProcessName} {width}x{height} -> {pw}x{ph} png={png.Length / 1024}KB");
        return Task.FromResult<UiCapture?>(new UiCapture(id, w.Id, png, pw, ph, DateTimeOffset.UtcNow));
    }

    public async Task<UiActionResult> ClickAsync(string windowId, UiCapture capture, int x, int y, CancellationToken ct)
    {
        var w = Resolve(windowId);
        if (w is null) return new UiActionResult(false, "window_gone", "That window has closed.");
        if (!_captures.TryGetValue(capture.CaptureId, out var cap)) return new UiActionResult(false, "capture_stale", "Capture the window again.");
        var h = (nint)w.Hwnd;
        UiNative.GetWindowRect(h, out var now);
        if (now.Left != cap.Rect.Left || now.Top != cap.Rect.Top || now.Right != cap.Rect.Right || now.Bottom != cap.Rect.Bottom)
            return new UiActionResult(false, "window_moved", "The window moved or resized since the screenshot. Capture it again.");
        var px = cap.Rect.Left + (int)Math.Round(x / cap.Scale);
        var py = cap.Rect.Top + (int)Math.Round(y / cap.Scale);
        if (Native.GetForegroundWindow() != h && !(await FocusAsync(windowId, ct).ConfigureAwait(false)).Ok)
            return new UiActionResult(false, "not_foreground", "That window isn't in front, so I didn't click.");
        var hit = UiNative.GetAncestor(UiNative.WindowFromPoint(new UiNative.POINT { X = px, Y = py }), UiNative.GA_ROOT);
        if (hit != h) return new UiActionResult(false, "point_covered", "Something else is covering that spot, so I didn't click.");
        UiNative.GetCursorPos(out var before);
        UiNative.Click(px, py);
        UiNative.SetCursorPos(before.X, before.Y);
        Log.Info("ui", $"clicked {w.ProcessName} at screen {px},{py}");
        return new UiActionResult(true, Message: "Clicked.");
    }

    /// <summary>One picture of the primary screen, scaled to at most 1280 px wide (for the screen_look tool).</summary>
    public Task<ScreenShot?> CaptureScreenAsync(CancellationToken ct)
    {
        var (pixels, width, height) = UiNative.CapturePrimaryScreen();
        if (pixels is null) return Task.FromResult<ScreenShot?>(null);
        var scale = Math.Min(1.0, 1280.0 / width);
        var (png, pw, ph) = Png.Encode(pixels, width, height, scale);
        var fg = Native.GetForegroundWindow();
        var title = fg == 0 ? null : Native.Title(fg);
        Log.Info("ui", $"screen captured {width}x{height} -> {pw}x{ph} png={png.Length / 1024}KB");
        return Task.FromResult<ScreenShot?>(new ScreenShot(png, "image/png", pw, ph, title));
    }

    public void Dispose() { lock (_workerGate) _worker.Dispose(); }
}

/// <summary>Minimal PNG encoder (RGB, zlib) with box down-scaling; no imaging dependencies.</summary>
internal static class Png
{
    public static (byte[] Png, int Width, int Height) Encode(byte[] bgra, int width, int height, double scale)
    {
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var raw = new byte[h * (1 + w * 3)];
        for (var y = 0; y < h; y++)
        {
            var row = y * (1 + w * 3);
            raw[row] = 0; // filter: none
            int sy0 = (int)(y / scale), sy1 = Math.Min(height, Math.Max(sy0 + 1, (int)((y + 1) / scale)));
            for (var x = 0; x < w; x++)
            {
                int sx0 = (int)(x / scale), sx1 = Math.Min(width, Math.Max(sx0 + 1, (int)((x + 1) / scale)));
                int r = 0, g = 0, b = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = (sy * width + sx) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; n++;
                    }
                var o = row + 1 + x * 3;
                raw[o] = (byte)(r / n); raw[o + 1] = (byte)(g / n); raw[o + 2] = (byte)(b / n);
            }
        }
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", []);
        return (ms.ToArray(), w, h);
    }

    private static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        var c = new byte[4]; BE(c, 0, (int)crc);
        s.Write(c);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

internal static class UiNative
{
    public const int SW_RESTORE = 9, GA_ROOT = 2;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h, int flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint h, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint bmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint h, int attr, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4, MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4;

    public static bool IsCloaked(nint h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var v, sizeof(int)) == 0 && v != 0;

    public static void TapAlt() { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, KEYEVENTF_KEYUP, 0); }

    public static void TypeChar(char c)
    {
        if (c == '\r') return;
        if (c == '\n') { Key(0x0D); return; }
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } },
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Key(ushort vk)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } },
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>The primary monitor as BGRA pixels (top-down), exactly what the user sees there.</summary>
    public static (byte[]? Pixels, int Width, int Height) CapturePrimaryScreen()
    {
        int width = GetSystemMetrics(0 /* SM_CXSCREEN */), height = GetSystemMetrics(1 /* SM_CYSCREEN */);
        if (width <= 0 || height <= 0) return (null, 0, 0);
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, width, height, screen, 0, 0, 0x00CC0020 /* SRCCOPY */)) return (null, 0, 0);
            var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[width * height * 4];
            SelectObject(mem, old);
            return (GetDIBits(mem, bmp, 0, (uint)height, bits, ref bmi, 0) == height ? bits : null, width, height);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Window pixels (BGRA, top-down) via PrintWindow, which works even when the window is partly covered.</summary>
    public static byte[]? CaptureWindow(nint h, int width, int height)
    {
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(mem, bmp);
        try
        {
            if (!PrintWindow(h, mem, 2 /* PW_RENDERFULLCONTENT */)) return null;
            var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[width * height * 4];
            SelectObject(mem, old);
            return GetDIBits(mem, bmp, 0, (uint)height, bits, ref bmi, 0) == height ? bits : null;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }
}
