namespace LIVORA.Application.Cloud;

/// <summary>
/// WAVE 4 P1-D — THE typed API port. Every client→cloud call in the product goes through this
/// interface; ViewModels and Pages are forbidden from touching <c>HttpClient</c> directly (§6).
///
/// The surface is exactly §5c of docs/architecture/wave4/CONTRACT-P1.md — one method per endpoint,
/// no invented extras. Results are values (<see cref="LivoraApiResult{T}"/>), not exceptions:
/// offline, 401, 409 and 429 are all expected states the UI renders, and the safe-degradation law
/// (§0.3) means a call site must never need a try/catch to stay alive.
///
/// MAUI-free by construction (System.Net.Http + Microsoft.Extensions.* only) so the plain-net10
/// test head compiles and drives the REAL implementation with scripted handlers.
/// </summary>
public interface ILivoraApiPort
{
    /// <summary>False when no base URL is configured: callers render `unconfigured`, and every
    /// method still answers honestly (a NotConfigured result) instead of throwing.</summary>
    bool IsConfigured { get; }

    Task<LivoraApiResult<AuthTokensDto>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<LivoraApiResult<AuthTokensDto>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<LivoraApiResult<AuthTokensDto>> LoginWithGoogleAsync(GoogleLoginRequest request, CancellationToken ct = default);
    Task<LivoraApiResult<AuthTokensDto>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);
    Task<LivoraApiResult<bool>> LogoutAsync(CancellationToken ct = default);

    Task<LivoraApiResult<IReadOnlyList<SessionInfoDto>>> GetSessionsAsync(CancellationToken ct = default);
    Task<LivoraApiResult<bool>> RevokeSessionAsync(string sessionId, CancellationToken ct = default);

    Task<LivoraApiResult<AccountInfoDto>> GetAccountAsync(CancellationToken ct = default);
    Task<LivoraApiResult<DeleteRequestDto>> RequestAccountDeletionAsync(CancellationToken ct = default);
    Task<LivoraApiResult<bool>> CancelAccountDeletionAsync(CancellationToken ct = default);
    Task<LivoraApiResult<AccountExportDto>> GetAccountExportAsync(CancellationToken ct = default);

    /// <summary>
    /// POST /sync/batch. <paramref name="idempotencyKey"/> is the caller-chosen UUID (§5c): a retry
    /// of the SAME operations must pass the SAME key so a replay returns the original result, and a
    /// different body under the same key is a server 409 the client surfaces, never retries.
    /// </summary>
    Task<LivoraApiResult<SyncBatchResponseDto>> SyncBatchAsync(
        SyncBatchRequest request, string idempotencyKey, CancellationToken ct = default);

    Task<LivoraApiResult<SyncChangesResponseDto>> GetSyncChangesAsync(
        long? since, int? limit, CancellationToken ct = default);

    /// <summary>GET /platform/capabilities — the honest server-truth probe (no auth required).</summary>
    Task<LivoraApiResult<CapabilitySnapshotDto>> GetCapabilitiesAsync(CancellationToken ct = default);
}

/// <summary>
/// One row of the capability snapshot (§5c notes + PlatformModule.CapabilityEntry):
/// <c>state</c> ∈ ok | degraded | unconfigured | not_implemented, straight from the server's own
/// <c>DependencyState</c>. The client never promotes a state: a module the server calls
/// <c>unconfigured</c> renders as unconfigured even when the local token store holds a session.
/// </summary>
public sealed record CapabilityEntryDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("key")] string Key,
    [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State,
    [property: System.Text.Json.Serialization.JsonPropertyName("detail")] string? Detail,
    [property: System.Text.Json.Serialization.JsonPropertyName("capabilities")]
    IReadOnlyDictionary<string, string>? Capabilities);

/// <summary>The <c>/api/v1/platform/capabilities</c> body (frozen server-side record).</summary>
public sealed record CapabilitySnapshotDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc,
    [property: System.Text.Json.Serialization.JsonPropertyName("apiVersion")] string? ApiVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("modules")] IReadOnlyList<CapabilityEntryDto>? Modules,
    [property: System.Text.Json.Serialization.JsonPropertyName("database")] CapabilityEntryDto? Database,
    [property: System.Text.Json.Serialization.JsonPropertyName("endpointMappingFailures")] IReadOnlyList<string>? EndpointMappingFailures)
{
    /// <summary>Server states, exactly as <c>DependencyState.ToString().ToLowerInvariant()</c> emits them.</summary>
    public const string StateOk = "ok";
    public const string StateDegraded = "degraded";
    public const string StateUnconfigured = "unconfigured";
    public const string StateNotImplemented = "not_implemented";

    public CapabilityEntryDto? Module(string key) =>
        Modules?.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
}
