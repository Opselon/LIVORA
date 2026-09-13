APPEND BLOCKS — lane 07 (wave 3c). Merge drivers apply these verbatim at the anchors named.
Nothing here writes into shared files from the lane copy; the lane build gate already passed
WITHOUT these (pages are standalone-safe; anchors make the surfaces reachable after merge).

========================================================================================
1) MauiProgram.cs — anchor: `// WAVE3B-DI:` (inside the WAVE3B-DI..WAVE3B-DI-END region)
========================================================================================
        // Wave 3c (lane 07): AI settings, Data Studio, lock screen — pages + VMs + routes.
        // Services (IGatewayConfigService, IConsentService, ILocalPasscodeService,
        // ICloudAuthService, ILocalDataCatalogService) are lane 02/05's registrations: every UI
        // service resolves through ServiceHelper.TryGet, so this block merges cleanly even when
        // a backing lane is not in yet — the pages then render their honest "not available"
        // states (product law), never a crash and never a dead button.
        builder.Services.AddTransient<LIVORA.Presentation.AiSettingsViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.DataStudioViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.LockViewModel>();
        Routing.RegisterRoute("ai-settings", typeof(LIVORA.Presentation.Views.AiSettingsPage));
        Routing.RegisterRoute("data-studio", typeof(LIVORA.Presentation.Views.DataStudioPage));
        Routing.RegisterRoute("lock", typeof(LIVORA.Presentation.Views.LockPage));
        // AI test-connection seam (lane 07 VM docs): wire ONLY to lane 02's real provider probe
        // when it merges. Left unset, the Test row honestly says no tester exists in this build.
        // Example (integrator replaces with the real probe — must not log the key):
        //   LIVORA.Presentation.AiSettingsViewModel.ConnectionTester =
        //       ct => sp.GetRequiredService<LIVORA.Application.Abstractions.IAiProbeService>()
        //             .ProbeAsync(ct);

========================================================================================
2) AppShell — NO route lines needed here: routes use the existing Routing.RegisterRoute
   pattern in MauiProgram (same as settings/reminders/updates). AppShell.xaml/.xaml.cs are
   untouched by this lane.
========================================================================================

========================================================================================
3) SettingsPage.xaml — anchor: `<!-- WAVE3B-SETTINGS ... -->` comment block (replace that
   comment with: comment kept + these rows + WAVE3B-SETTINGS-END style comment)
========================================================================================
            <!-- WAVE3B-SETTINGS: lane cards merge here (AI status, privacy/consent, connections).
                 NOTE: must stay an XML comment — XAML has no C-style slash-star comment
                 syntax; a brace-delimited comment here becomes literal text content of the
                 stack and fails the build with MAUIX2002. -->

            <!-- WAVE3C-LANE07: entries into the wave-3c surfaces (AI settings / Data Studio /
                 app lock). Same row vocabulary as the reminders shortcut above; navigation goes
                 through the VM's NavigateRequested, whose NavigateSafelyAsync reports an honest
                 alert if a route has not merged yet — these are entries, never dead buttons. -->
            <Border Style="{StaticResource Card}" Background="{StaticResource SurfaceAltBrush}"
                    StrokeThickness="0" Padding="14,10" MinimumHeightRequest="56"
                    comp:TappableFeedback.Tappable="True"
                    SemanticProperties.Description="{localize:Tr Settings.AiEntry}">
                <Border.GestureRecognizers>
                    <TapGestureRecognizer Command="{Binding OpenAiSettingsCommand}"/>
                </Border.GestureRecognizers>
                <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
                    <VerticalStackLayout Grid.Column="0" Spacing="2">
                        <Label Text="{localize:Tr Settings.AiEntry}" Style="{StaticResource LSubheading}"
                               FontSize="15" LineBreakMode="WordWrap" MaxLines="2"
                               SemanticProperties.HeadingLevel="Level3"/>
                        <Label Text="{localize:Tr Settings.AiEntryNote}" Style="{StaticResource HintLabel}"
                               MaxLines="2"/>
                    </VerticalStackLayout>
                    <Label Grid.Column="1" Text="›" Style="{StaticResource LMetric}"
                           VerticalOptions="Center" HorizontalOptions="End"
                           TextColor="{AppThemeBinding Light={StaticResource TextTertiary}, Dark={StaticResource TextTertiaryDark}}"
                           AutomationProperties.IsInAccessibleTree="False"/>
                </Grid>
            </Border>
            <Border Style="{StaticResource Card}" Background="{StaticResource SurfaceAltBrush}"
                    StrokeThickness="0" Padding="14,10" MinimumHeightRequest="56"
                    comp:TappableFeedback.Tappable="True"
                    SemanticProperties.Description="{localize:Tr Settings.DataStudioEntry}">
                <Border.GestureRecognizers>
                    <TapGestureRecognizer Command="{Binding OpenDataStudioCommand}"/>
                </Border.GestureRecognizers>
                <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
                    <VerticalStackLayout Grid.Column="0" Spacing="2">
                        <Label Text="{localize:Tr Settings.DataStudioEntry}"
                               Style="{StaticResource LSubheading}" FontSize="15"
                               LineBreakMode="WordWrap" MaxLines="2"
                               SemanticProperties.HeadingLevel="Level3"/>
                        <Label Text="{localize:Tr Settings.DataStudioEntryNote}"
                               Style="{StaticResource HintLabel}" MaxLines="2"/>
                    </VerticalStackLayout>
                    <Label Grid.Column="1" Text="›" Style="{StaticResource LMetric}"
                           VerticalOptions="Center" HorizontalOptions="End"
                           TextColor="{AppThemeBinding Light={StaticResource TextTertiary}, Dark={StaticResource TextTertiaryDark}}"
                           AutomationProperties.IsInAccessibleTree="False"/>
                </Grid>
            </Border>
            <Border Style="{StaticResource Card}" Background="{StaticResource SurfaceAltBrush}"
                    StrokeThickness="0" Padding="14,10" MinimumHeightRequest="56"
                    comp:TappableFeedback.Tappable="True"
                    SemanticProperties.Description="{localize:Tr Settings.LockEntry}">
                <Border.GestureRecognizers>
                    <TapGestureRecognizer Command="{Binding OpenLockCommand}"/>
                </Border.GestureRecognizers>
                <Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">
                    <VerticalStackLayout Grid.Column="0" Spacing="2">
                        <Label Text="{localize:Tr Settings.LockEntry}" Style="{StaticResource LSubheading}"
                               FontSize="15" LineBreakMode="WordWrap" MaxLines="2"
                               SemanticProperties.HeadingLevel="Level3"/>
                        <Label Text="{localize:Tr Settings.LockEntryNote}" Style="{StaticResource HintLabel}"
                               MaxLines="2"/>
                    </VerticalStackLayout>
                    <Label Grid.Column="1" Text="›" Style="{StaticResource LMetric}"
                           VerticalOptions="Center" HorizontalOptions="End"
                           TextColor="{AppThemeBinding Light={StaticResource TextTertiary}, Dark={StaticResource TextTertiaryDark}}"
                           AutomationProperties.IsInAccessibleTree="False"/>
                </Grid>
            </Border>
            <!-- WAVE3C-LANE07-END -->

========================================================================================
4) SettingsViewModel.cs — anchor: `// WAVE3B-SETTINGS-VM:` (keep both markers around it)
========================================================================================
        // WAVE3C-LANE07: second-level entries added by the wave-3c UI lane (routed the same way
        // as reminders/updates — the page's NavigateSafelyAsync reports honestly if a route
        // from this block has not merged).
        OpenAiSettingsCommand = new Command(() => NavigateRequested?.Invoke("ai-settings"));
        OpenDataStudioCommand = new Command(() => NavigateRequested?.Invoke("data-studio"));
        OpenLockCommand = new Command(() => NavigateRequested?.Invoke("lock"));
        // WAVE3C-LANE07-END

   // ...plus, next to the existing `public Command OpenRemindersCommand { get; }` declarations:

    public Command OpenAiSettingsCommand { get; }
    public Command OpenDataStudioCommand { get; }
    public Command OpenLockCommand { get; }

   // ...plus, appended to LocalizedProperties[] (the labels are rendered through localize:Tr in
   // XAML, so no VM properties are needed — nothing else to add; language re-raise is automatic).

========================================================================================
5) App.xaml.cs startup gate — PROPOSAL (integrator applies; lane never edited App.xaml.cs).
   Anchor: after `var window = new Window(page);` inside CreateWindow, before the WAVE3-APP
   block. Behavior: locked session => LockPage presented modally over the normal root; the
   page's UnlockedByPasscode event pops it. When no ILocalPasscodeService is registered or the
   session is not locked, this is a measured no-op — the app behaves exactly as before.
========================================================================================

        // WAVE3C-LOCK-GATE (lane 07 proposal): the passcode gate. IsLocked is per-session state
        // on the service — first launch after Set has IsLocked=true until a verify succeeds.
        // NOT a lockout timer and NOT a fake wall: with no service registered nothing happens.
        var passcode = ServiceHelper.TryGet<LIVORA.Application.Abstractions.ILocalPasscodeService>();
        if (passcode is { IsSet: true, IsLocked: true })
        {
            window.Page.Appearing += async (_, _) =>
            {
                if (window.Page.Navigation.ModalStack.Count > 0) return; // gate already up
                try
                {
                    var gate = ServiceHelper.Get<LIVORA.Presentation.Views.LockPage>();
                    gate.UnlockedByPasscode += async () =>
                    {
                        try { await window.Page.Navigation.PopModalAsync(); } catch { }
                    };
                    await window.Page.Navigation.PushModalAsync(gate);
                }
                catch { /* an unresolvable gate page must not brick the window: Settings -> App
                           lock remains the manual route, and the log carries no PIN material. */ }
            };
        }
        // WAVE3C-LOCK-GATE-END

   Note: this needs `builder.Services.AddTransient<LIVORA.Presentation.Views.LockPage>();` in
   the MauiProgram block above (§1) so ServiceHelper.Get can build the page with its VM.

========================================================================================
6) resx (EN) — append to Resources/Localization/AppResources.resx before </root>
   Full <data> blocks: see wave3c-keys/lane07.en.keys.xml (90 entries, same order as FA).
   resx (FA) — append to Resources/Localization/AppResources.fa.resx before </root>:
   see wave3c-keys/lane07.fa.keys.xml. Reused existing keys (already in both resx, NOT re-added):
   Common.Back / Common.Save / Common.Delete / Common.Done / Common.Cancel / Common.Continue /
   Common.NotAvailable.
========================================================================================
