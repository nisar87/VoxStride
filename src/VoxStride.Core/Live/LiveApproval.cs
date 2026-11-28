using System.Collections.Concurrent;
using VoxStride.Contracts;
using VoxStride.Core.Tools;

namespace VoxStride.Core.Live;

/// <summary>
/// Approvals during a live conversation (owner decision 2026-11-28: "no proceed, just do it like a person").
/// <list type="bullet">
/// <item>Reversible changes the user asked for in their own words (moving or renaming files, which "undo" can put
/// back) are approved automatically for live runs. The evidence records that the user's spoken request authorized them.</item>
/// <item>Anything that can't easily be undone (recycling or deleting, permanent cleanup, pressing Send/Pay/Submit,
/// submitting a web form, clicking at a screen position) is still asked once. The user answers with a plain "yes" or
/// "no" out loud, or clicks the on-screen card, whichever comes first.</item>
/// <item>Runs that are not live (typed or classic voice commands) go to the normal on-screen approval unchanged.</item>
/// </list>
/// The model never answers for the user: only the user's transcribed voice or a click resolves a question.
/// </summary>
public sealed class LiveApproval(IUserApproval inner, TimeProvider time) : IUserApproval
{
    /// <summary>Operations that are reversible and auto-approved when the user asked for them in a live conversation.</summary>
    public static readonly IReadOnlySet<string> AutoInLive = new HashSet<string>(StringComparer.Ordinal)
    {
        ManifestMoveToolBase.Operation, // files.move_batch (move / rename / organize; undoable)
    };

    private readonly ConcurrentDictionary<Guid, byte> _liveRuns = new();
    private TaskCompletionSource<bool>? _voice;
    private readonly Lock _gate = new();

    /// <summary>Raised when a live run needs the user's yes/no (the companion says the question aloud).</summary>
    public event Action<ApprovalRequest>? Asking;
    /// <summary>Raised when the question is answered or abandoned.</summary>
    public event Action<bool>? Answered;

    public bool IsWaitingForVoice { get { lock (_gate) return _voice is not null; } }

    public void MarkLive(Guid runId) => _liveRuns[runId] = 0;
    public void Forget(Guid runId) => _liveRuns.TryRemove(runId, out _);

    /// <summary>The user's spoken answer, from the live transcription of their voice.</summary>
    public bool AnswerByVoice(bool yes)
    {
        TaskCompletionSource<bool>? t;
        lock (_gate) { t = _voice; _voice = null; }
        return t?.TrySetResult(yes) == true;
    }

    public async Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (!_liveRuns.ContainsKey(request.RunId)) return await inner.RequestAsync(request, ct).ConfigureAwait(false);

        if (AutoInLive.Contains(request.Operation))
        {
            Log.Info("approval", $"live auto-approved {request.Operation} for run {request.RunId.ToString("N")[..8]} (reversible; asked for by the user)");
            return new Approval(Guid.NewGuid(), request.RunId, request.ManifestDigest, request.Operation, time.GetUtcNow().AddMinutes(5), "live-request:" + request.Operation);
        }

        var voice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asking?.Invoke(request); // show and say the question first, so the companion is visibly asking before we listen for the answer
        lock (_gate) { _voice?.TrySetResult(false); _voice = voice; }
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Log.Info("approval", $"live question for {request.Operation}: {request.Title}");
        var click = inner.RequestAsync(request, both.Token);
        var first = await Task.WhenAny(click, voice.Task, Task.Delay(TimeSpan.FromMinutes(2), ct)).ConfigureAwait(false);
        lock (_gate) { if (_voice == voice) _voice = null; }
        Approval? result = null;
        if (first == voice.Task)
        {
            both.Cancel(); // closes the on-screen card
            if (voice.Task.Result) result = new Approval(Guid.NewGuid(), request.RunId, request.ManifestDigest, request.Operation, time.GetUtcNow().AddMinutes(5), "spoken-yes");
            Log.Info("approval", $"live question answered by voice: {(result is null ? "no" : "yes")}");
        }
        else if (first == click)
        {
            voice.TrySetResult(false);
            result = await click.ConfigureAwait(false);
            Log.Info("approval", $"live question answered by click: {(result is null ? "no" : "yes")}");
        }
        else { both.Cancel(); Log.Info("approval", "live question timed out: not done"); }
        Answered?.Invoke(result is not null);
        return result;
    }

    private static readonly HashSet<string> Nos = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nope", "no thanks", "no thank you", "don't", "dont", "do not", "stop", "cancel", "wait", "never mind", "nevermind", "not now",
        "nahi", "nahin", "na", "mat karo", "ruko", "rehne do",
    };

    /// <summary>Classifies a short standalone answer: true = yes, false = no, null = not an answer.</summary>
    public static bool? ClassifyAnswer(string text)
    {
        if (Voice.ConversationCoordinator.IsConfirmation(text)) return true;
        var words = new string(text.Select(ch => char.IsLetter(ch) || char.IsWhiteSpace(ch) || ch == '\'' ? char.ToLowerInvariant(ch) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("voxstride" or "please")).ToArray();
        if (words.Length is 0 or > 4) return null;
        return Nos.Contains(string.Join(' ', words)) || Nos.Contains(words[0]) && words.All(w => Nos.Contains(w) || w is "thanks" or "thank" or "you" or "it" or "that") ? false : null;
    }
}
