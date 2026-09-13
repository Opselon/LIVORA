# lane01 — APPEND blocks (orchestrator pastes these; I never edit shared files)

All blocks are additive and anchored on existing markers. Nothing here assumes another lane's type
names except where explicitly noted (lane 07 consumes these services through the frozen interfaces).

## 1) MauiProgram.cs — inside `// WAVE3B-DI:` … `// WAVE3B-DI-END`

```csharp
        // ---- WAVE3C LANE 01: security + data portability (local-only, MAUI-free classes) ----
        // The app-data root every wave-3c store shares. LocalJsonStore is the platform-free twin of
        // JsonFileStore: SAME directory (FileSystem.AppDataDirectory/LIVORA), so there is no second
        // data layout — only an entry point the plain-net10 test head can compile.
        var livoraDataDir = Path.Combine(FileSystem.AppDataDirectory, "LIVORA");
        builder.Services.AddSingleton(sp =>
            new LIVORA.Infrastructure.Persistence.LocalJsonStore(livoraDataDir));
        builder.Services.AddSingleton(sp =>
            new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Security.SecureStorageService.StoreDirName)));

        // At-rest box: DPAPI CurrentUser on Windows (the real encryption path for USER keys);
        // elsewhere the value lives in the private app-data dir and IsPlatformHardwareBacked stays
        // false, so the UI can never claim a cipher this head does not have.
        builder.Services.AddSingleton<LIVORA.Infrastructure.Security.IPlatformSecureBox>(
#if WINDOWS
            _ => new LIVORA.Infrastructure.Security.WindowsPlatformSecureBox());
#else
            _ => new LIVORA.Infrastructure.Security.PrivateFileSecureBox());
#endif
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ISecureStorageService>(sp =>
            new LIVORA.Infrastructure.Security.SecureStorageService(
                sp.GetRequiredService<LIVORA.Infrastructure.Persistence.LocalJsonStore>(),
                // the SECOND LocalJsonStore registration above (the secure/ subdirectory) is the one
                // this service wants; resolve it explicitly so the root store cannot be picked by luck:
                new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                    System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Security.SecureStorageService.StoreDirName)),
                sp.GetRequiredService<LIVORA.Infrastructure.Security.IPlatformSecureBox>()));

        // Consent register (default Untouched / Denied), the AI-gateway seam (user key beats the
        // obfuscated embedded fallback; ships DISABLED), and the honest no-backend placeholders.
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IConsentService>(sp =>
            new LIVORA.Infrastructure.Security.ConsentStore(
                sp.GetRequiredService<LIVORA.Infrastructure.Persistence.LocalJsonStore>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IGatewayConfigService>(sp =>
            new LIVORA.Infrastructure.Security.Gateway.GatewayConfigService(
                new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                    System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Security.Gateway.GatewayConfigService.GatewayDirName)),
                sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ILocalDataCatalogService>(sp =>
            new LIVORA.Infrastructure.Persistence.LocalDataCatalog(
                sp.GetRequiredService<LIVORA.Infrastructure.Persistence.LocalJsonStore>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ILocalPasscodeService>(sp =>
            new LIVORA.Infrastructure.Security.LocalPasscodeService(
                sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>(),
                () => sp.GetRequiredService<LIVORA.Application.Context.SessionState>().CurrentProfile?.Id));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ICloudAuthService,
            LIVORA.Infrastructure.Security.CloudAuthService>();
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ISyncTransport,
            LIVORA.Infrastructure.Security.NoopSyncTransport>();
        // ---- /WAVE3C LANE 01 ----
```

DI note: lane 01 registers `LocalJsonStore` twice on purpose (root + `secure/` subdir) because the
single-service-per-type MS.DI rule would otherwise force a factory per consumer; if the integrator
prefers one registration, use the explicit `new LocalJsonStore(...)` factory arguments above (they
already do that) and drop the `secure/` singleton line.

`using` additions if not already present: `using Microsoft.Maui.Storage;` (for `FileSystem`) —
MauiProgram already has `LIVORA.Infrastructure.Persistence` and `LIVORA.Infrastructure.Security`.

## 2) Resources/Localization/AppResources.resx (+ .fa.resx)

Append the `<data>` entries from `wave3c-keys/lane01.en.keys.xml` /
`wave3c-keys/lane01.fa.keys.xml` before `</root>`, in manifest order. `Norm.Reject.NotFinite` is
repeated VERBATIM from lane03's manifest (same value both lanes) — if the merge driver dedupes
identical keys, that is the expected outcome; a value conflict is a merge-stop.

## 3) Privacy inventory (residual wipe coverage)

`Presentation/ViewModels/Profile/LocalDataFiles.cs` lists the Wave 3 stores the core wipe misses.
Lane 01 adds three files to that residual set (the consent register, the secure store, the gateway
state file) — none carries personal values, but the wipe must not leave them behind either:

```csharp
    public const string ConsentsFile = LIVORA.Infrastructure.Security.ConsentStore.FileName;      // "livora_consents.json"
    public const string SecureStoreFile = LIVORA.Infrastructure.Security.SecureStorageService.StoreFileName; // "secure/secure-store.json"
    public const string GatewayStateFile = LIVORA.Infrastructure.Security.Gateway.GatewayConfigService.ConfigFileName; // "gateway/gateway-config.json"
```

`gateway-config.json` and `secure-store.json` contain NO key material in plaintext (the secure store
holds DPAPI ciphertext on Windows, and the gateway file has no key field at all — asserted by
`OverrideIsStoredThroughTheSecureStore_NotInThisServicesJson`). Deleting them on wipe removes the
user's own gateway key override + passcode verifier, which is the point of a wipe.

## 4) Probe wiring (lane 02's transport → lane 01's state)

`GatewayConfigService.RecordProbeResult(bool ok, string? failureReasonKey)` is the ONLY writer of
`LastVerifiedUtc`/`LastProbeOk`, and lane 01 never calls it: the UI must not be able to say
"Connected" without a real round-trip. Whoever registers the probe (lane 02) calls it after each
live check. Until then `LastVerifiedUtc` stays null and lane 07 renders "Not verified".
