using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: prove the deletion lifecycle writes the TRUTH the policy block on IdentityAccountService
///          promises — request → pending + audit row; cancel → active + audit row; a second request
///          while pending is 409 deletion_pending; the grace window refuses early self-execution;
///          and execution (driven through the service against an isolated temp SQLite file, the way
///          Phase 2's scheduler will call it) destroys credentials and connector truth, anonymises
///          the users row in place, clears sync payloads while keeping the revision ledger, and
///          RETAINS audit rows + tombstone hashes exactly as documented.
/// OWNER: Agent 03 (identity lane); written by repair lane R2.
/// CONSUMES: the HTTP verbs for the lifecycle rows + a hand-wired service for the execute row (the
///           HTTP execute route is staff-gated beyond grace; the scheduler seam is the tested path).
/// INVARIANTS: every claim of the policy block is asserted from the DATABASE, not from the response;
///           a deleted account's email is re-registrable ONLY if the policy says so (it is not — the
///           anonymised row keeps the unique index honest, and this test pins what the index holds).
/// </summary>
public sealed class IdentityDeletionLifecycleTests : IdentityApiHarness
{
    public IdentityDeletionLifecycleTests(LivoraWebFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Request_then_cancel_writes_the_state_and_both_audit_rows()
    {
        var reg = await RegisterAsync(deviceLabel: "lifecycle");
        await LoginAsync(reg.Email, reg.Password, deviceLabel: "second-device"); // a live session to keep

        // --- request
        var (status, text) = await PostRawAsync("/api/v1/account/delete-requests", "{}",
            bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status);
        var body = Parse(text);
        var requested = body.GetProperty("deletionRequestedAtUtc").GetDateTimeOffset();
        var scheduled = body.GetProperty("scheduledForUtc").GetDateTimeOffset();
        var reversible = body.GetProperty("reversibleUntilUtc").GetDateTimeOffset();
        Assert.Equal(30, (scheduled - requested).TotalDays);       // Identity:Deletion:GraceDays default
        Assert.Equal(scheduled, reversible);

        using (var db = Db())
        {
            var user = await db.Users.SingleAsync(u => u.Id == reg.Tokens.UserId);
            Assert.Equal(AccountStatus.DeletionPending, user.Status);
            Assert.NotNull(user.DeletionRequestedAtUtc);
            // sessions KEEP working during a pending request — the user must be able to cancel
            var liveSessions = await db.Sessions.CountAsync(s => s.UserId == user.Id && s.RevokedAtUtc == null);
            Assert.Equal(2, liveSessions);
            Assert.NotNull(await db.AuditEvents.FirstOrDefaultAsync(a =>
                a.Type == "AccountDeletionRequested" && a.UserId == reg.Tokens.UserId));
        }

        // a second request while pending is the contract's 409
        var (dupStatus, dupText) = await PostRawAsync("/api/v1/account/delete-requests", "{}",
            bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, dupStatus);
        Assert.Equal(ProblemCodes.DeletionPending, ProblemOf(dupText).Code);

        // the grace window blocks early self-execution
        var (earlyStatus, earlyText) = await PostRawAsync("/api/v1/account/delete-requests/execute",
            JsonSerializer.Serialize(new { }), bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, earlyStatus);
        Assert.Equal(ProblemCodes.Conflict, ProblemOf(earlyText).Code);

        // --- cancel
        var (cancelStatus, _) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/account/delete-requests", bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, cancelStatus);
        using (var db = Db())
        {
            var user = await db.Users.SingleAsync(u => u.Id == reg.Tokens.UserId);
            Assert.Equal(AccountStatus.Active, user.Status);
            Assert.Null(user.DeletionRequestedAtUtc);
            Assert.NotNull(await db.AuditEvents.FirstOrDefaultAsync(a =>
                a.Type == "AccountDeletionCancelled" && a.UserId == reg.Tokens.UserId));
        }

        // cancelling twice is honest-idempotent: nothing pending answers 204, not 409
        var (againStatus, _) = await SendRawAsync(HttpMethod.Delete,
            "/api/v1/account/delete-requests", bearer: reg.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, againStatus);
    }

    [Fact]
    public async Task Executing_a_deletion_destroys_and_anonymises_exactly_per_the_stated_policy()
    {
        // Isolated temp DB + hand-wired services: the same call Phase 2's scheduler will make.
        var dir = Path.Combine(Path.GetTempPath(), "livora-del-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            IdentityModelContribution.EnsureRegistered();
            Livora.Server.Infrastructure.Sync.SyncModelContribution.EnsureRegistered();
            var options = new DbContextOptionsBuilder<LivoraDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dir, "t.db")}").Options;
            using var db = new LivoraDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var now = DateTimeOffset.UtcNow;
            var hasher = new PasswordHasher();
            var user = new UserAccount
            {
                Email = "del-target@identity.test", NormalizedEmail = "del-target@identity.test",
                PasswordHash = hasher.Hash(Password), DisplayName = "Del Me",
                Status = AccountStatus.DeletionPending, DeletionRequestedAtUtc = now.AddMonths(-2),
                CreatedAtUtc = now.AddMonths(-6),
            };
            db.Users.Add(user);
            db.Sessions.Add(new AuthSession
            {
                UserId = user.Id, RefreshTokenHash = RefreshTokens.Hash("live-refresh-material"),
                CreatedAtUtc = now.AddDays(-2), ExpiresAtUtc = now.AddDays(28),
            });
            db.Connectors.Add(new ConnectorState
            {
                UserId = user.Id, Provider = "healthconnect", State = "ok", Detail = "connected upstream",
            });
            db.SyncOperations.Add(new SyncOperation
            {
                UserId = user.Id, OperationId = "op-secret", EntityType = "manual_entry",
                EntityId = "entry-1", Kind = "create", Outcome = "applied", ResultRevision = 1,
                PayloadJson = "{\"note\":\"clinical detail\"}", ReceivedAtUtc = now,
            });
            db.Set<IdentitySecurityProfile>().Add(new IdentitySecurityProfile
            {
                NormalizedEmail = user.NormalizedEmail, UserId = user.Id, FailedAttempts = 3,
            });
            db.AuditEvents.Add(new AuditEvent { UserId = user.Id, Type = "Login", Subject = user.Id });
            await db.SaveChangesAsync();

            var accountService = new Livora.Server.Modules.Identity.IdentityAccountService(
                db, IdentityOptions.FromConfiguration(new ConfigurationBuilder().Build()),
                SystemClock.Instance,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Livora.Server.Modules.Identity.IdentityAccountService>.Instance);

            await accountService.ExecuteAsync(user, now, default);
            await db.SaveChangesAsync();

            // ANONYMISED in place — the row remains as the FK/audit anchor, keyed by a pseudonym
            var after = await db.Users.SingleAsync(u => u.Id == user.Id);
            Assert.Equal(AccountStatus.Deleted, after.Status);
            Assert.Equal($"deleted+{user.Id}@invalid.local", after.Email);
            Assert.Equal(after.Email, after.NormalizedEmail);
            Assert.Null(after.PasswordHash);                       // credential destroyed
            Assert.Null(after.GoogleSubject);
            Assert.Null(after.LastLoginAtUtc);
            Assert.Equal("", after.DisplayName);
            Assert.NotNull(after.DeletedAtUtc);                    // the wipe is timestamped
            Assert.Null(after.DeletionRequestedAtUtc);             // no longer "pending"

            // sessions + connector truth DESTROYED (not flagged)
            Assert.Equal(0, await db.Sessions.CountAsync(s => s.UserId == user.Id));
            Assert.Equal(0, await db.Connectors.CountAsync(c => c.UserId == user.Id));

            // the session's live hash was tombstoned before the row died → replay still detectable
            var tombstone = await db.Set<RevokedRefreshToken>()
                .SingleOrDefaultAsync(t => t.UserId == user.Id && t.Reason == "account_deleted");
            Assert.NotNull(tombstone);

            // sync PAYLOADS cleared, revision ledger kept
            var op = await db.SyncOperations.SingleAsync(o => o.UserId == user.Id);
            Assert.Equal("{}", op.PayloadJson);
            Assert.Equal("erased", op.EntityId);
            Assert.Equal(1, op.ResultRevision);                    // the ledger number survives

            // lockout profile destroyed — a dead mailbox keeps no state
            Assert.Equal(0, await db.Set<IdentitySecurityProfile>()
                .CountAsync(p => p.NormalizedEmail == "del-target@identity.test"));

            // audit trail RETAINED (security evidence; unlinkable once the row is anonymised)
            Assert.True(await db.AuditEvents.CountAsync(a => a.UserId == user.Id) >= 1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Deleted_accounts_cannot_sign_in_refresh_or_read_anything()
    {
        var (status, text) = await PostRawAsync("/api/v1/auth/register",
            JsonSerializer.Serialize(new { email = NewEmail("ghostcheck"), password = Password }));
        Assert.Equal(HttpStatusCode.Created, status);
        var tokens = ReadTokens(text);

        // The HTTP-visible proof of the execute-path policy (covered row-wise in the isolated test
        // above): once the users row carries Deleted, every door closes. Flip the status the same way
        // ExecuteAsync does — this test pins the GATE, not the wipe.
        using (var db = Db())
        {
            var user = await db.Users.SingleAsync(u => u.Id == tokens.UserId);
            user.Status = AccountStatus.Deleted;
            await db.SaveChangesAsync();
        }

        var email = await ReadEmailAsync(tokens.UserId);
        var (loginStatus, loginText) = await PostRawAsync("/api/v1/auth/login",
            JsonSerializer.Serialize(new { email, password = Password }));
        Assert.Equal(HttpStatusCode.Unauthorized, loginStatus);
        Assert.Equal(ProblemCodes.InvalidCredentials, ProblemOf(loginText).Code); // indistinguishable probe

        var (refreshStatus, _) = await RefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshStatus);
        var (accountStatus, _) = await SendRawAsync(HttpMethod.Get, "/api/v1/account",
            bearer: tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, accountStatus);
    }

    private async Task<string> ReadEmailAsync(string userId)
        => (await ReadWhenPresentAsync<string?>(async db =>
            (await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId))?.NormalizedEmail))!;
}
