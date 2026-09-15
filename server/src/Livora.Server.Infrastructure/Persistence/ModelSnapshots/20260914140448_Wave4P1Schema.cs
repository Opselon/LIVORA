using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Livora.Server.Infrastructure.Persistence.ModelSnapshots
{
    /// <inheritdoc />
    public partial class Wave4P1Schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auth_revoked_refresh_tokens",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FamilyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RevokedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_revoked_refresh_tokens", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "auth_session_lineage",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FamilyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_session_lineage", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "engine_evidence",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    MetricKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Grade = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    SourceFamily = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    SourceLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsSourceFact = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAggregate = table.Column<bool>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_evidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "engine_feedback",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ActionKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EvidenceGrade = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    SourceLabel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "engine_verifications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Grade = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    TrailJson = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    AssessedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_verifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "identity_security_profiles",
                columns: table => new
                {
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowStartedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailureAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LockoutUntilUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_security_profiles", x => x.NormalizedEmail);
                });

            migrationBuilder.CreateTable(
                name: "pattern_dismissals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PatternId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pattern_dismissals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    GoogleSubject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AppleSubject = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PrimaryLocale = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Tier = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastLoginAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletionRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "verification_claims",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClaimId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    MetricKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimedValue = table.Column<double>(type: "REAL", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    AsOfUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    TrustLevel = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    AttemptedRulesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ContributingRuleKeysJson = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    RefusalReason = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ReviewerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReviewDecision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ReviewedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceLabel = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ClientPlatform = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUsedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_auth_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "connector_states",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LastSyncAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DataAsOfUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_connector_states_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_batch_records",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResponseJson = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: false),
                    ResponseStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReceivedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_batch_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_batch_records_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_change_records",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    BatchKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    BaseRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 60000, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_change_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_change_records_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sync_operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    BaseRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 60000, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ConflictDetail = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_operations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_operations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification_evidence",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimRowId = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    MetricKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_verification_evidence_verification_claims_ClaimRowId",
                        column: x => x.ClaimRowId,
                        principalTable: "verification_claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_Type",
                table: "audit_events",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_UserId_OccurredAtUtc",
                table: "audit_events",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_auth_revoked_refresh_tokens_SessionId",
                table: "auth_revoked_refresh_tokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_revoked_refresh_tokens_UserId",
                table: "auth_revoked_refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_session_lineage_FamilyId",
                table: "auth_session_lineage",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_session_lineage_UserId",
                table: "auth_session_lineage",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_auth_sessions_RefreshTokenHash",
                table: "auth_sessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth_sessions_UserId",
                table: "auth_sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_connector_states_UserId_Provider",
                table: "connector_states",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_engine_evidence_UserId_Id",
                table: "engine_evidence",
                columns: new[] { "UserId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_engine_evidence_UserId_SubjectKey_OccurredAtUtc",
                table: "engine_evidence",
                columns: new[] { "UserId", "SubjectKey", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_engine_feedback_UserId_ActionKey_OccurredAtUtc",
                table: "engine_feedback",
                columns: new[] { "UserId", "ActionKey", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_engine_feedback_UserId_IdempotencyKey",
                table: "engine_feedback",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_engine_verifications_UserId_SubjectKey_AssessedAtUtc",
                table: "engine_verifications",
                columns: new[] { "UserId", "SubjectKey", "AssessedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_security_profiles_UserId",
                table: "identity_security_profiles",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_pattern_dismissals_UserId",
                table: "pattern_dismissals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_pattern_dismissals_UserId_PatternId",
                table: "pattern_dismissals",
                columns: new[] { "UserId", "PatternId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_batch_records_UserId_IdempotencyKey",
                table: "sync_batch_records",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_change_records_UserId_EntityType_EntityId",
                table: "sync_change_records",
                columns: new[] { "UserId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_change_records_UserId_ServerRevision",
                table: "sync_change_records",
                columns: new[] { "UserId", "ServerRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_operations_UserId_EntityType_EntityId",
                table: "sync_operations",
                columns: new[] { "UserId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_operations_UserId_OperationId",
                table: "sync_operations",
                columns: new[] { "UserId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_GoogleSubject",
                table: "users",
                column: "GoogleSubject",
                unique: true,
                filter: "\"GoogleSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_NormalizedEmail",
                table: "users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_claims_UserId_ClaimId",
                table: "verification_claims",
                columns: new[] { "UserId", "ClaimId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_verification_claims_UserId_CreatedAtUtc",
                table: "verification_claims",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_evidence_ClaimRowId_EvidenceId",
                table: "verification_evidence",
                columns: new[] { "ClaimRowId", "EvidenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "auth_revoked_refresh_tokens");

            migrationBuilder.DropTable(
                name: "auth_session_lineage");

            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropTable(
                name: "connector_states");

            migrationBuilder.DropTable(
                name: "engine_evidence");

            migrationBuilder.DropTable(
                name: "engine_feedback");

            migrationBuilder.DropTable(
                name: "engine_verifications");

            migrationBuilder.DropTable(
                name: "identity_security_profiles");

            migrationBuilder.DropTable(
                name: "pattern_dismissals");

            migrationBuilder.DropTable(
                name: "sync_batch_records");

            migrationBuilder.DropTable(
                name: "sync_change_records");

            migrationBuilder.DropTable(
                name: "sync_operations");

            migrationBuilder.DropTable(
                name: "verification_evidence");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "verification_claims");
        }
    }
}
