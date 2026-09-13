# Lane 07 NOTES — wave 3c UI surfaces (self-audit + state proofs)

## Timer / loop state proof
`grep -rn "System.Threading.Timer|DispatcherTimer|new Timer|PeriodicTimer" Presentation/Views/{AiSettings,DataStudio,Security} Presentation/ViewModels/{AiSettings,DataStudio,Security} Presentation/Views/Wave3cMotion.cs`
=> matches ONLY inside doc-comments that *assert* their absence (zero code usages). Animation is a
finite FadeToAsync burst per page instance (Wave3cMotion: 140 ms, 20 ms stagger, once); nothing
runs after it completes, so no Disappearing teardown is needed and none exists.

## ReduceMotion
grep for ReduceMotion/ReducedMotion across the repo (bin/obj excluded): ZERO hits — the token
does not exist in this wave. Follow-up for lane 05 (shared style owner): surface the OS setting
and set `Wave3cMotion.ReduceMotion` from it; the seam is documented in Wave3cMotion.cs. This lane
does not edit shared files, so nothing was wired.

## i18n self-audit table (key | EN | FA | overflow risk)
Key counts: EN 90 / FA 90 — identical order, identical arg counts (script-verified: no drift),
zero collisions with existing AppResources.resx. Reused existing keys (not re-emitted):
Common.Back/Save/Delete/Done/Cancel/Continue/NotAvailable. Technical names stay Latin: model tag
(`coding`), entity kinds (`goals`…), file paths, the raw error-key echo row under each localized
rejection. All dates/numbers flow through IFormatService (LongDate/Time with locale digits) —
zero string-concatenated numbers in new UI code.

| key | EN | FA | risk |
|---|---|---|---|
| AiSettings.Title | AI assistant | دستیار هوش مصنوعی | LOW |
| AiSettings.Status.Header | Gateway status | وضعیت درگاه | LOW |
| AiSettings.AdvancedNote | This screen talks to the real gateway settings. Changes take effect immediately and only after the service confirms them. | این صفحه با تنظیماتِ واقعیِ درگاه کار می‌کند. تغییرها بی‌درنگ مؤثرند، فقط وقتی سرویس تأییدشان کند. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Status.Configured | Configured | پیکربندی‌شده | LOW |
| AiSettings.Status.NotConfigured | Not configured | پیکربندی‌نشده | LOW |
| AiSettings.Status.VerifiedAt | Verified reachable at {0}, {1} | آخرین بررسیِ موفق: {0}، ساعت {1} | LOW |
| AiSettings.Status.NotVerified | Not verified — this install has never seen a successful check | تأییدنشده — این نصب هرگز پاسخِ موفقی از درگاه نگرفته است | LOW |
| AiSettings.KeySource.BuiltIn | Built-in (obfuscated) | کلیدِ داخلی (مبهم‌شده) | MED chip text long — wraps MaxLines=2 |
| AiSettings.KeySource.User | Your own key | کلیدِ خودتان | LOW |
| AiSettings.KeySource.None | No key | بدون کلید | LOW |
| AiSettings.Enable | Use AI answers | پاسخ‌های هوش مصنوعی | LOW |
| AiSettings.EnableNote | When off, every insight stays rule-based. AI also needs your processing consent below. | اگر خاموش باشد، همهٔ تحلیل‌ها فقط با قواعدِ داخلی ساخته می‌شوند. هوش مصنوعی به رضایتِ پردازشِ پایینِ این صفحه هم نیاز دارد. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.TestConnection | Test connection | آزمونِ اتصال | LOW |
| AiSettings.TestUnavailableNote | No connection tester is registered in this build — the live probe has not been merged yet. | در این نسخه هیچ آزمونگرِ اتصالی ثبت نشده — بررسیِ زنده هنوز به این نسخه نرسیده است. | LOW |
| AiSettings.Test.Ok | The gateway answered this check. Verification is now recorded. | درگاه به این آزمون پاسخ داد و تأیید ثبت شد. | LOW |
| AiSettings.Test.Fail | The gateway did not answer this check. Nothing was verified. | درگاه به این آزمون پاسخ نداد. چیزی تأیید نشد. | LOW |
| AiSettings.Key.Header | Your own API key | کلیدِ APIِ خودتان | LOW |
| AiSettings.Key.Note | Optional. A key you enter here replaces the built-in one and is stored encrypted on this device only. Save an empty box? Use “Back to built-in” instead. | اختیاری. کلیدی که اینجا وارد کنید جای کلیدِ داخلی را می‌گیرد و فقط روی همین دستگاه به‌صورت رمزنگاری‌شده ذخیره می‌شود. برای پاک‌کردن، «بازگشت به کلیدِ داخلی» را بزنید. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Key.Placeholder | Paste your key | کلید را اینجا بچسبانید | LOW |
| AiSettings.Key.Clear | Back to built-in | بازگشت به کلیدِ داخلی | LOW |
| AiSettings.Key.Saved | Your key was saved for this device. | کلیدِ شما روی این دستگاه ذخیره شد. | MED button label long — wraps MaxLines=2 |
| AiSettings.Key.Cleared | Back to the built-in key. | کلیدِ داخلی دوباره فعال شد. | LOW |
| AiSettings.Key.Empty | Type a key first — or use “Back to built-in” to clear your override. | اول کلید را بنویسید — یا برای پاک‌کردن، «بازگشت به کلیدِ داخلی» را بزنید. | LOW |
| AiSettings.Status.InsecureChip | Insecure | ناامن | LOW |
| AiSettings.Insecure.Title | This connection is not encrypted | این اتصال رمزنگاری نمی‌شود | LOW |
| AiSettings.Insecure.Body | The gateway is reachable over plain HTTP: anything sent could be read on the network. Keep AI off unless you accept that risk, and never send data you would not want exposed. | درگاه با HTTPِ ساده در دسترس است: هرچه بفرستید ممکن است در شبکه خوانده شود. تا این خطر را نپذیره‌اید، هوش مصنوعی را خاموش بگذارید و داده‌ای که نمی‌خواهید فاش شود نفرستید. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Consent.Header | Consent | رضایت‌ها | LOW |
| AiSettings.Consent.Note | Every category starts as never-asked, which behaves like denied. Flipping a switch records your explicit answer; a “never asked” label disappears once you do. | هر مورد در ابتدا «پرسیده‌نشده» است و مثل «ردشده» رفتار می‌شود. با کلیدِ کنارش پاسخِ صریحِ شما ثبت می‌شود و برچسبِ «پرسیده‌نشده» می‌رود. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Consent.NeverAsked | Never asked — treated as denied | هرگز پرسیده نشده — ردشده حساب می‌شود | LOW hint line, wraps |
| AiSettings.Consent.AiProcessing | AI processing | پردازش با هوش مصنوعی | LOW |
| AiSettings.Consent.AiProcessingNote | Send a minimal summary of your state to the AI gateway for interpretation. The raw key never travels from this device beyond the gateway call itself. | اجازه دهید خلاصهٔ حداقلی از وضعیت شما برای تفسیر به درگاهِ هوش مصنوعی فرستاده شود. کلیدِ API هیچ‌وقت فراتر از خودِ همین فراخوانی از دستگاه بیرون نمی‌رود. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Consent.HealthData | Health data | دادهٔ سلامت | LOW |
| AiSettings.Consent.HealthDataNote | Let features read your sleep and recovery history. Stays on this device; AI only ever receives the minimal derived summary above, and only with that consent too. | اجازه دهید ویژگی‌ها تاریخچهٔ خواب و ریکاوری شما را بخوانند. همه روی همین دستگاه می‌ماند؛ هوش مصنوعی هم فقط همان خلاصهٔ حداقلی را می‌گیرد، و آن هم با همین رضایت. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Consent.ActivityData | Activity data | دادهٔ فعالیت | LOW |
| AiSettings.Consent.ActivityDataNote | Let features read steps and workouts. Stays on this device. | اجازه دهید ویژگی‌ها قدم‌ها و تمرین‌های شما را بخوانند. همه روی همین دستگاه می‌ماند. | LOW |
| AiSettings.Consent.Notifications | Notifications | اعلان‌ها | LOW |
| AiSettings.Consent.NotificationsNote | Allow LIVORA to remind you locally. The system permission prompt is separate and is only asked for on the Reminders screen. | اجازه دهید لایورا یادآوریِ محلی بفرستد. اجازهٔ خودِ سیستم جدا است و فقط در صفحهٔ یادآوری‌ها خواسته می‌شود. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Cloud.Header | Cloud account | حسابِ ابری | LOW |
| AiSettings.Cloud.Configured | Backend configured | بک‌اند آماده است | LOW |
| AiSettings.Cloud.NotConfigured | Not configured | پیکربندی‌نشده | LOW |
| AiSettings.Cloud.Note | This card reports the account backend's real state. There is no sign-in form because there is nothing to sign in to yet — your data lives on this device. | این کارت وضعیتِ واقعیِ سرویسِ حساب را گزارش می‌دهد. فرمِ ورودی نیست، چون فعلاً سرویسی برای ورود وجود ندارد — داده‌های شما روی همین دستگاه است. | LOW prose — WordWrap, generous MaxLines |
| AiSettings.Failed | That did not go through. Nothing was changed. | این عمل انجام نشد. چیزی تغییر نکرد. | LOW |
| DataStudio.Title | Data Studio | استودیوی داده | LOW |
| DataStudio.AdvancedBanner | Advanced: this screen edits the raw JSON your app actually stores. Wrong shapes can break trends and plans. If you were not told to be here, go back. | پیشرفته: این صفحه JSONِ خامِ ذخیره‌شدهٔ برنامه را ویرایش می‌کند. شکلِ غلط می‌تواند روندها و برنامه‌ها را خراب کند. اگر نگفته‌اند اینجا بیایید، برگردید. | LOW prose — WordWrap, generous MaxLines |
| DataStudio.Warning.Title | You are editing stored data directly | در حال ویرایشِ مستقیمِ دادهٔ ذخیره‌شده هستید | LOW |
| DataStudio.Warning.Body | Changes here are not validated by the normal UI — the store's own rules are the last line of defense. Export a backup first if the data matters to you. | تغییرهای اینجا را رابطِ عادی اعتبارسنجی نمی‌کند — قواعدِ خودِ حافظه آخرین خطِ دفاع‌اند. اگر این داده برایتان مهم است، اول پشتیبان بگیرید. | LOW prose — WordWrap, generous MaxLines |
| DataStudio.Entity.Header | Single entity | یک موجودیت | LOW |
| DataStudio.Kind | Entity kind | نوعِ موجودیت | LOW |
| DataStudio.EntityId | Entity ID | شناسهٔ موجودیت | LOW |
| DataStudio.EntityId.Placeholder | the entity's id | شناسهٔ موجودیت | LOW |
| DataStudio.Load | Load | بارگذاری | LOW |
| DataStudio.Apply | Apply JSON | اعمالِ JSON | LOW |
| DataStudio.NoKinds | The data catalog returned no kinds yet. | فهرستِ داده هنوز نوعی برنگردانده است. | LOW |
| DataStudio.NothingLoaded | Nothing loaded yet. Pick a kind, type an id, then press Load. | هنوز چیزی بارگذاری نشده. نوع را انتخاب کنید، شناسه را بنویسید و «بارگذاری» را بزنید. | LOW |
| DataStudio.Errors.Header | The store rejected this JSON: | حافظه این JSON را نپذیرفت: | LOW |
| DataStudio.ImportOk | Accepted and stored | پذیرفته و ذخیره شد | MED chip text long — wraps MaxLines=2 |
| DataStudio.Failed | The operation failed. Nothing was changed. | عملیات شکست خورد. چیزی تغییر نکرد. | LOW |
| DataStudio.ConfirmDelete.Title | Delete this entity? | این موجودیت حذف شود؟ | LOW |
| DataStudio.ConfirmDelete.Body | {0} “{1}” will be removed from local storage. This cannot be undone. | «{1}» از نوعِ {0} برای همیشه از حافظهٔ محلی حذف می‌شود و قابل بازگشت نیست. | LOW |
| DataStudio.ExportAll.Header | Export everything | پشتیبانِ کامل | LOW |
| DataStudio.ExportAll.Note | Writes the whole local store as one JSON file into this app's data folder and shows the real path. No cloud, no network. | همهٔ حافظهٔ محلی را در یک فایلِ JSON در پوشهٔ دادهٔ همین برنامه می‌نویسد و مسیرِ واقعی را نشان می‌دهد. بدون ابر، بدون شبکه. | LOW prose — WordWrap, generous MaxLines |
| DataStudio.ExportAll.Action | Export all to file | پشتیبان‌گیری در فایل | LOW |
| DataStudio.ImportAll.Header | Import from file | بازیابی از فایل | LOW |
| DataStudio.ImportAll.Note | Reads a full-export JSON file from a path on this device and replaces or merges your store according to the switch below. | یک فایلِ JSONِ پشتیبانِ کامل را از مسیرِ داده‌شده روی همین دستگاه می‌خواند و بسته به کلیدِ پایین، همه‌چیز را جایگزین یا ادغام می‌کند. | LOW prose — WordWrap, generous MaxLines |
| DataStudio.ImportAll.Action | Import from path | بازیابی از این مسیر | LOW |
| DataStudio.Merge | Merge into existing data (off = replace everything) | ادغام در داده‌های موجود (خاموش = جایگزینیِ همه‌چیز) | MED switch label long (wraps to 2 lines) |
| DataStudio.Path | Backup file path | مسیرِ فایلِ پشتیبان | LOW |
| DataStudio.Path.Placeholder | C:\folder\livora_export.json | C:\folder\livora_export.json | LOW |
| DataStudio.PathMissing | That file could not be read. Check the path — this build takes a full path typed here, not a folder picker. | این فایل خوانده نشد. مسیر را بررسی کنید — این نسخه مسیرِ کاملِ تایپ‌شده را می‌گیرد، نه انتخابگرِ پوشه. | LOW |
| Lock.Title | App lock | قفلِ برنامه | LOW |
| Lock.PinNote | A 4-digit code protects this app on the device. It is stored only as a hash here and never sent anywhere. | یک کدِ ۴ رقمی از برنامه روی همین دستگاه محافظت می‌کند. اینجا فقط هشِ آن ذخیره می‌شود و هیچ‌جا فرستاده نمی‌شود. | LOW |
| Lock.Set.Label | Choose a 4-digit code | کدِ ۴ رقمیِ خود را انتخاب کنید | LOW |
| Lock.Retype.Label | Re-enter the same code | همان کد را دوباره وارد کنید | LOW |
| Lock.Verify.Label | Enter your code | کدِ خود را وارد کنید | LOW |
| Lock.Set | Set code | ثبتِ کد | LOW |
| Lock.Confirm | Confirm code | تأییدِ کد | LOW |
| Lock.Unlock | Unlock | باز کردن | LOW |
| Lock.Wrong | That code didn't work. | این کد کار نکرد. | LOW |
| Lock.Mismatch | The two entries didn't match. Choose your code again. | دو ورودی با هم فرق داشتند. کد را از نو انتخاب کنید. | LOW |
| Lock.SetDone | Code set. The app will ask for it next launch. | کد ثبت شد. بارِ بعدِ اجرا برنامه آن را می‌خواهد. | LOW |
| Lock.SetFailed | The code could not be saved on this device. | کد روی این دستگاه ذخیره نشد. | LOW |
| Lock.Removed | The code was removed from this device. | کد از این دستگاه حذف شد. | LOW |
| Lock.Remove | Remove code | حذفِ کد | LOW |
| Lock.LockNow | Lock now | همین حالا قفل کن | LOW |
| Settings.AiEntry | AI assistant & consent | دستیار هوش مصنوعی و رضایت‌ها | LOW |
| Settings.AiEntryNote | Gateway status, your key, data consent | وضعیتِ درگاه، کلیدِ شما، رضایتِ داده | LOW |
| Settings.DataStudioEntry | Data Studio | استودیوی داده | LOW |
| Settings.DataStudioEntryNote | Advanced: raw entity editing, backup and restore | پیشرفته: ویرایشِ خامِ موجودیت‌ها، پشتیبان و بازیابی | LOW |
| Settings.LockEntry | App lock | قفلِ برنامه | LOW |
| Settings.LockEntryNote | 4-digit passcode, lock now, remove | رمزِ ۴ رقمی، قفلِ فوری، حذفِ رمز | LOW |

## Build/test gates (actual)
- `dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q` (Debug AND Release):
  `Build succeeded. 0 Warning(s) 0 Error(s)` each (Release tail: 0W/0E, ~31s).
- `dotnet test Tests/LIVORA.Tests.csproj --nologo -v q`: `Passed! - Failed: 0, Passed: 439`
  (no pure-layer file touched; new pure-ish helper Wave3cMotion lives in Presentation).

## Deviations / honest debt
- MAUI 10.0.101 (verified against the shipped Essentials assemblies) has NO FileSaver/save picker
  in this build. "Export All" therefore writes to `FileSystem.AppDataDirectory/LIVORA/
  livora_datastudio_export.json` (the exact LocalDataFiles.cs directory idiom) and shows the REAL
  path; inventing a save dialog would be a fake. Mobile picker debt: a per-platform
  share-catcher/saver should be added with the sync wave — noted, not built here (desktop-first).
- Import All takes a typed path (desktop-first) for the same reason; missing-file reads report
  DataStudio.PathMissing honestly instead of throwing.
- ConnectionTester static hook defaults null in this copy; the button is HIDDEN (not a dead
  disabled control) and the row states "no tester registered in this build". Integrator wires it
  per the APPEND block (lane 02 probe). The Command gate is captured at construction, so wiring
  must happen in MauiProgram BEFORE first page resolution (documented there).
- Startup lock gate is delivered as an exact APPEND proposal (App.xaml.cs after window creation);
  lane never edited App.xaml.cs. Until merged, LockPage is reachable via Settings -> App lock and
  the Lock-now button genuinely calls ILocalPasscodeService.Lock().
- Three-state consent: Untouched renders switch-off + amber "never asked" sub-label; first flip
  records an explicit decision via IConsentService.SetAsync. No consent is implied by the default.
- No fake sign-in: the cloud card renders ICloudAuthService.IsBackendConfigured + the localized
  StatusReasonKey only. There is no form at all.
- DataStudio Delete confirms through DisplayAlertAsync (accept/cancel) — the spec allowed
  ActionSheet or Alert; Alert chosen for consistent RTL styling. Wrong-PIN wording is one generic
  string (no counters/timing words anywhere in Lock keyspace).
