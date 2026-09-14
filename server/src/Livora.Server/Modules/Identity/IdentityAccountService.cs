using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Modules.Identity;

/// <summary>
/// PURPOSE: the account half of the identity lane — profile read, the deletion lifecycle, and the
///          data export. Split from IdentityService purely so each file owns one behaviour; both
///          share the same ctor inputs and the same session gate.
/// OWNER: Agent 03 (identity lane).
///
/// ─── ACCOUNT-DELETION POLICY (the contract's "stated as deleted vs anonymised vs retained") ───
///
/// REQUEST: status → DeletionPending, sessions keep working (a pending request must not lock the
///          user out of their own cancellation), scheduledForUtc = now + Identity:Deletion:GraceDays
///          (default 30), reversibleUntilUtc = scheduledForUtc. Cancel = a no-op-safe DELETE that
///          returns the account to Active.
///
/// EXECUTE (Phase 2 wires the scheduler; P1-C exposes POST /api/v1/account/delete-requests/execute
/// guarded to Admin + a >= grace-hours-old request so the path is REAL and testable now):
///
///   DELETED (destroyed — the user's own content and all credentials):
///     • users.PasswordHash, users.Email/NormalizedEmail (pseudonymised in place, see ANONYMISED),
///       users.GoogleSubject/AppleSubject, users.DisplayName, users.PrimaryLocale
///     • auth_sessions rows for this user + every live refresh hash → tombstoned as account_deleted,
///       row then physically deleted (a session of a deleted account is a credential, not history)
///     • connector_states rows (a third-party link is personal data — gone, not flagged)
///     • sync_operations PayloadJson → '{}' + content columns cleared: the SYNC LEDGER revision
///       counts are platform state (conflict ordering), the PAYLOAD is the user's content
///   ANONYMISED (kept structurally, unlinked from the person):
///     • users row itself: Email="deleted+<id>@invalid.local", NormalizedEmail likewise,
///       DisplayName="", Status=Deleted, DeletedAtUtc=now, GoogleSubject=null
///       — kept because FK-anchored platform rows (sync revision history, audit user ids) reference
///       the id, and dropping the row would cascade-destroy the audit trail that proves WHY this
///       happened. After the wipe the id maps to no person: it is a pseudonym, not personal data.
///   RETAINED (by design — documented, not accidental):
///     • audit_events (rows for this user are KEPT): they carry only ids/types/timestamps and enums
///       — no content — and are the security evidence trail (lockouts, theft detections, deletions).
///       This is the standard "right to erasure vs. legal/records obligation" split: the retained
///       rows become unlinkable once the users row is anonymised above.
///     • auth_revoked_refresh_tokens tombstone hashes: SHA-256 of random 288-bit secrets — already
///       pre-hash non-personal; deleting them would silently DISABLE reuse detection for tokens an
///       attacker might still hold.
///     • identity_security_profiles: the email is replaced by the same anonymised form on the users
///       row, and the profile keyed by the OLD email is DELETED — the lockout state of a
///       nonexistent mailbox protects nobody.
///     → Every claim in this block is exercised by DeletionLifecycleTests.
/// </summary>
public sealed class IdentityAccountService
{
    private readonly LivoraDbContext _db;
    private readonly IdentityOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<IdentityAccountService> _log;

    public IdentityAccountService(
        LivoraDbContext db, IdentityOptions options, IClock clock, ILogger<IdentityAccountService> log)
    {
        _db = db;
        _options = options;
        _clock = clock;
        _log = log;
    }

    // ==================================================================== account

    public async Task<IResult> GetAccountAsync(HttpContext ctx, CancellationToken ct)
    {
        var gate = await IdentityService.AuthorizeCallerAsync(_db, ctx, _clock, ct);
        if (!gate.Allowed) return gate.Problem!;
        var user = gate.Context!.User;

        return Results.Json(new AccountInfo(
            UserId: user.Id,
            Email: user.Email,
            DisplayName: user.DisplayName,
            Locale: user.PrimaryLocale,
            Tier: user.Tier.ToString().ToLowerInvariant(),
            Status: user.Status.ToString().ToLowerInvariant(),
            CreatedAtUtc: user.CreatedAtUtc,
            LastLoginAtUtc: user.LastLoginAtUtc));
    }

    // ============================================================ delete request

    public async Task<IResult> RequestDeletionAsync(HttpContext ctx, CancellationToken ct)
    {
        var gate = await IdentityService.AuthorizeCallerAsync(_db, ctx, _clock, ct);
        if (!gate.Allowed) return gate.Problem!;
        var user = gate.Context!.User;

        if (user.Status == AccountStatus.DeletionPending && user.DeletionRequestedAtUtc is not null)
            // §5c pins THIS response at 409. The frozen Problems.StatusFor maps the code to 403 by
            // default, so the handler states the contract status explicitly — the more specific
            // frozen contract (§5c's line) wins over the general code table, without editing it.
            return Problems.Of(ctx, ProblemCodes.DeletionPending,
                "A deletion request is already pending for this account.",
                status: StatusCodes.Status409Conflict);

        var now = _clock.UtcNow;
        var scheduled = now.AddDays(_options.DeletionGraceDays);
        user.Status = AccountStatus.DeletionPending;
        user.DeletionRequestedAtUtc = now;
        user.RowVersion += 1;

        IdentityService.AddAuditCore(_db, ctx, "AccountDeletionRequested", user.Id, user.Id);
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("account requested deletion; scheduled in {GraceDays} days", _options.DeletionGraceDays);

        return Results.Json(new DeletionRequestResponse(
            DeletionRequestedAtUtc: now, ScheduledForUtc: scheduled, ReversibleUntilUtc: scheduled));
    }

    public async Task<IResult> CancelDeletionAsync(HttpContext ctx, CancellationToken ct)
    {
        var gate = await IdentityService.AuthorizeCallerAsync(_db, ctx, _clock, ct);
        if (!gate.Allowed) return gate.Problem!;
        var user = gate.Context!.User;

        if (user.Status == AccountStatus.Deleted)
            return Problems.Of(ctx, ProblemCodes.Conflict, "This account has already been deleted.");
        if (user.Status != AccountStatus.DeletionPending)
            return Results.NoContent(); // idempotent cancel: nothing pending is a success, not an error

        user.Status = AccountStatus.Active;
        user.DeletionRequestedAtUtc = null;
        user.RowVersion += 1;

        IdentityService.AddAuditCore(_db, ctx, "AccountDeletionCancelled", user.Id, user.Id);
        await _db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Execute a due deletion. Admin-guarded AND self-allowed-by-ownership only through the
    /// scheduler seam: Phase 2's hosted service calls it with no caller; the HTTP route requires
    /// the Admin policy. Deletion of your own account is authorised by your own credential; the
    /// grace window (not the endpoint) is the reversibility guarantee.
    /// </summary>
    public async Task<IResult> ExecuteDeletionAsync(HttpContext ctx, string? targetUserId, CancellationToken ct)
    {
        var gate = await IdentityService.AuthorizeCallerAsync(_db, ctx, _clock, ct);
        if (!gate.Allowed) return gate.Problem!;
        var caller = gate.Context!.User;

        // IDOR gate (§5b): an admin may delete a DUE target account; a normal caller may only reach
        // their own pending deletion. A foreign targetUserId never learns anything: same 403 shape.
        var targetId = string.IsNullOrWhiteSpace(targetUserId) ? caller.Id : targetUserId!;
        if (targetId != caller.Id && !ctx.User.IsStaff())
            return Problems.Of(ctx, ProblemCodes.Forbidden, "Only staff may execute another account's deletion.");

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetId, ct);
        if (target is null || target.Status == AccountStatus.Deleted)
            return Problems.Of(ctx, ProblemCodes.NotFound, "No such account.");
        if (target.Status != AccountStatus.DeletionPending || target.DeletionRequestedAtUtc is null)
            return Problems.Of(ctx, ProblemCodes.ValidationFailed,
                "This account has no pending deletion request.");

        var due = target.DeletionRequestedAtUtc.Value.AddDays(_options.DeletionGraceDays);
        var now = _clock.UtcNow;
        if (due > now && !ctx.User.IsStaff())
            return Problems.Of(ctx, ProblemCodes.Conflict,
                "The deletion grace window has not elapsed yet.");
        if (due > now)
        {
            // Staff early-execution is an operator action — audited as such.
            IdentityService.AddAuditCore(_db, ctx, "AccountDeletionEarlyStaff", caller.Id, target.Id);
        }

        await ExecuteAsync(target, now, ct);
        IdentityService.AddAuditCore(_db, ctx, "AccountDeleted", target.Id, target.Id,
            "{\"policy\":\"wave4-p1c-v1\"}");
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("account deletion executed (policy wave4-p1c-v1)");
        return Results.NoContent();
    }

    /// <summary>What actually happens, per the policy block on this class. Single method so the
    /// deleted/anonymised/retained split is auditable in one read.</summary>
    public async Task ExecuteAsync(UserAccount target, DateTimeOffset now, CancellationToken ct)
    {
        // 1. every session of the account dies with its refresh hashes tombstoned
        var sessions = await _db.Sessions.Where(s => s.UserId == target.Id).ToListAsync(ct);
        foreach (var s in sessions)
        {
            if (s.RevokedAtUtc is null && s.RefreshTokenHash.Length > 0)
            {
                _db.Set<RevokedRefreshToken>().Add(new RevokedRefreshToken
                {
                    TokenHash = s.RefreshTokenHash,
                    SessionId = s.Id,
                    FamilyId = s.Id,
                    UserId = s.UserId,
                    Reason = RevokedRefreshReasons.AccountDeleted,
                    RevokedAtUtc = now,
                });
            }
            _db.Sessions.Remove(s); // destroyed: credentials, not history
        }
        var lineage = await _db.Set<AuthSessionLineage>().Where(l => l.UserId == target.Id).ToListAsync(ct);
        _db.Set<AuthSessionLineage>().RemoveRange(lineage);

        // 2. connector truth destroyed
        var connectors = await _db.Connectors.Where(c => c.UserId == target.Id).ToListAsync(ct);
        _db.Connectors.RemoveRange(connectors);

        // 3. sync payloads cleared, revision ledger kept (policy block explains why).
        // Load-and-mutate (not ExecuteUpdate) so the SAME code runs on relational providers and on
        // the tests' in-memory provider — never branch above the persistence layer, §4.
        var operations = await _db.SyncOperations.Where(o => o.UserId == target.Id).ToListAsync(ct);
        foreach (var o in operations)
        {
            o.PayloadJson = "{}";
            o.EntityId = "erased";
            o.ConflictDetail = null;
        }

        // 4. lockout profile destroyed (its key is the personal email)
        var profiles = await _db.Set<IdentitySecurityProfile>()
            .Where(p => p.UserId == target.Id || p.NormalizedEmail == target.NormalizedEmail).ToListAsync(ct);
        _db.Set<IdentitySecurityProfile>().RemoveRange(profiles);

        // 5. the users row is ANONYMISED in place (not deleted — see policy: FK + audit anchor)
        target.Email = $"deleted+{target.Id}@invalid.local";
        target.NormalizedEmail = target.Email;
        target.PasswordHash = null;   // credential destroyed
        target.GoogleSubject = null;  // federated anchor destroyed
        target.AppleSubject = null;
        target.DisplayName = "";
        target.PrimaryLocale = "en";
        target.Status = AccountStatus.Deleted;
        target.DeletionRequestedAtUtc = null;
        target.DeletedAtUtc = now;
        target.LastLoginAtUtc = null;
        target.RowVersion += 1;
    }

    // ===================================================================== export

    public async Task<IResult> ExportAsync(HttpContext ctx, CancellationToken ct)
    {
        var gate = await IdentityService.AuthorizeCallerAsync(_db, ctx, _clock, ct);
        if (!gate.Allowed) return gate.Problem!;
        var user = gate.Context!.User;

        // The export is a GDPR/DSAR surface: no tokens, no hashes, no provider credentials, no
        // audit internals — only what the account IS and what it recorded.
        var profile = new AccountInfo(
            UserId: user.Id, Email: user.Email, DisplayName: user.DisplayName,
            Locale: user.PrimaryLocale, Tier: user.Tier.ToString().ToLowerInvariant(),
            Status: user.Status.ToString().ToLowerInvariant(),
            CreatedAtUtc: user.CreatedAtUtc, LastLoginAtUtc: user.LastLoginAtUtc);

        var connectors = await _db.Connectors.Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Provider)
            .Select(c => new
            {
                provider = c.Provider, state = c.State, detail = c.Detail,
                lastSyncAtUtc = c.LastSyncAtUtc, dataAsOfUtc = c.DataAsOfUtc,
            })
            .ToListAsync(ct);

        var history = await _db.SyncOperations.Where(o => o.UserId == user.Id)
            .OrderByDescending(o => o.ReceivedAtUtc).Take(500)
            .Select(o => new
            {
                entityType = o.EntityType, entityId = o.EntityId, kind = o.Kind,
                outcome = o.Outcome, receivedAtUtc = o.ReceivedAtUtc,
                // payload deliberately excluded: the export states it exists, sync lanes own its
                // shape; dumping raw client JSON through identity would bypass their validation.
                payloadIncluded = false,
            })
            .ToListAsync(ct);

        // Empty is honest: goals/habits/plans/purchases/community do not exist in Phase 1 —
        // §5c note 3. Each section carries its provenance instead of a fabricated fixture.
        static ExportSection notImplemented() => new("not_implemented", Array.Empty<object>());

        var doc = new AccountExport(
            ExportedAtUtc: _clock.UtcNow,
            Profile: new ExportSection("server", profile),
            Goals: notImplemented(),
            Habits: notImplemented(),
            Plans: notImplemented(),
            History: new ExportSection("server", history),
            ConnectedDataMetadata: new ExportSection("server", connectors),
            Purchases: notImplemented(),
            CommunityContent: notImplemented());
        return Results.Json(doc);
    }
}
