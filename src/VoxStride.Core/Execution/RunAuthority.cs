using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VoxStride.Contracts;

namespace VoxStride.Core.Execution;

/// <summary>
/// Authorization for one committed run. Minted only by <see cref="RunAuthority"/> after a committed user
/// instruction. Holds an unguessable token that is never passed into model context.
/// </summary>
public sealed class RunAuthorization
{
    internal RunAuthorization(Guid runId, Guid sessionId, string transcriptHash, IReadOnlySet<string> allowedTools,
        DateTimeOffset expiresAt, long generation, string token)
    {
        RunId = runId; SessionId = sessionId; TranscriptHash = transcriptHash; AllowedTools = allowedTools;
        ExpiresAt = expiresAt; Generation = generation; Token = token;
    }
    public Guid RunId { get; }
    public Guid SessionId { get; }
    public string TranscriptHash { get; }
    public IReadOnlySet<string> AllowedTools { get; }
    public DateTimeOffset ExpiresAt { get; }
    public long Generation { get; }
    internal string Token { get; }
    public string PolicyVersion => RunAuthority.PolicyVersion;
}

public enum AuthorizationStatus { Valid, Unknown, Revoked, Expired, StaleGeneration }

/// <summary>Single source of run authorizations. Cancelling or committing a new command revokes older runs.</summary>
public sealed class RunAuthority(TimeProvider time)
{
    public const string PolicyVersion = "2026.11.1";
    private readonly ConcurrentDictionary<Guid, RunAuthorization> _active = new();
    private long _generation;

    public long CurrentGeneration => Interlocked.Read(ref _generation);

    public RunAuthorization Mint(CommandEnvelope command, IEnumerable<string> allowedTools, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FinalizedTranscript);
        if (command.TranscriptHash != HashTranscript(command.FinalizedTranscript))
            throw new InvalidOperationException("Transcript hash mismatch: command was modified after commit.");
        var gen = Interlocked.Increment(ref _generation);
        foreach (var k in _active.Keys) _active.TryRemove(k, out _); // one side-effecting run per desktop in v1
        var auth = new RunAuthorization(Guid.NewGuid(), command.SessionId, command.TranscriptHash,
            new HashSet<string>(allowedTools, StringComparer.Ordinal), time.GetUtcNow() + lifetime, gen,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        _active[auth.RunId] = auth;
        return auth;
    }

    public AuthorizationStatus Check(RunAuthorization? auth)
    {
        if (auth is null) return AuthorizationStatus.Unknown;
        if (!_active.TryGetValue(auth.RunId, out var known)) return AuthorizationStatus.Revoked;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(known.Token), Encoding.ASCII.GetBytes(auth.Token)))
            return AuthorizationStatus.Unknown;
        if (auth.Generation != CurrentGeneration) return AuthorizationStatus.StaleGeneration;
        if (time.GetUtcNow() > auth.ExpiresAt) return AuthorizationStatus.Expired;
        return AuthorizationStatus.Valid;
    }

    public void Revoke(Guid runId) => _active.TryRemove(runId, out _);

    public void RevokeAll()
    {
        Interlocked.Increment(ref _generation);
        _active.Clear();
    }

    public static string HashTranscript(string transcript) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(transcript.Normalize(NormalizationForm.FormC))));
}
