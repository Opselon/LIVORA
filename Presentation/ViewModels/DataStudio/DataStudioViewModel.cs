using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LIVORA.Application.Abstractions;

namespace LIVORA.Presentation;

/// <summary>
/// Wave 3c (lane 07) — Data Studio: the honest "advanced" screen over
/// <see cref="ILocalDataCatalogService"/>. Inspect one entity as raw JSON, apply edited JSON back
/// through the catalog's validate-before-write path, delete entities behind a confirm, and export
/// / import the whole store.
///
/// Honesty gates in this VM:
///  - First entry per app session raises the raw-data warning (this screen edits stored truth;
///    a wrong JSON shape can break derivations). Declining backs out of the page.
///  - Every outcome of a write is reported as what the service returned — error keys rendered
///    through the existing Tr pattern (key + localized text rows), never a generic "failed".
///  - Export writes to the app data dir (the same directory JsonFileStore/LocalDataFiles use) and
///    shows the real full path. This MAUI version (10.0.101, verified against the shipped
///    assemblies) has no FileSaver/save-picker API, so inventing a "Save dialog…" label would be
///    a lie; the desktop-first path+Open-folder flow is what is actually true. Mobile picker debt
///    is recorded in the lane NOTES.
///  - With no catalog registered (lane not merged) the whole screen renders one honest
///    "not available" state instead of dead controls.
/// </summary>
public sealed class DataStudioViewModel : ObservableObject
{
    /// <summary>Session gate: the raw-data warning is shown once per app run, not per visit —
    /// the point is a deliberate first crossing, not nagging on every tab push.</summary>
    private static bool _sessionWarned;
    internal static void ResetSessionWarnedForTests() => _sessionWarned = false;

    private readonly ILocalDataCatalogService? _catalog;

    public DataStudioViewModel()
    {
        _catalog = ServiceHelper.TryGet<ILocalDataCatalogService>();
        SubscribeLanguage();

        LoadEntityCommand = new Command(async () => await LoadEntityAsync(),
            () => _catalog is not null && SelectedKind is { Length: > 0 } && EntityId.Trim().Length > 0);
        ApplyCommand = new Command(async () => await ApplyAsync(),
            () => _catalog is not null && SelectedKind is { Length: > 0 } && EditorText.Trim().Length > 0);
        DeleteCommand = new Command(async () => await DeleteAsync(),
            () => _catalog is not null && SelectedKind is { Length: > 0 } && EntityId.Trim().Length > 0);
        ExportAllCommand = new Command(async () => await ExportAllAsync(), () => _catalog is not null);
        ImportAllCommand = new Command(async () => await ImportAllAsync(),
            () => _catalog is not null && ImportPath.Trim().Length > 0);
        BackCommand = new Command(async () => await GoBackAsync());
    }

    public bool HasCatalog => _catalog is not null;
    public string Title => L("DataStudio.Title");
    public string NotAvailable => L("Common.NotAvailable");
    public string AdvancedBanner => L("DataStudio.AdvancedBanner");
    public string EntityHeader => L("DataStudio.Entity.Header");
    public string KindLabel => L("DataStudio.Kind");
    public string EntityIdLabel => L("DataStudio.EntityId");
    public string EntityIdPlaceholder => L("DataStudio.EntityId.Placeholder");
    public string LoadLabel => L("DataStudio.Load");
    public string ApplyLabel => L("DataStudio.Apply");
    public string DeleteLabel => L("Common.Delete");
    public string ExportAllHeader => L("DataStudio.ExportAll.Header");
    public string ExportAllNote => L("DataStudio.ExportAll.Note");
    public string ExportAllLabel => L("DataStudio.ExportAll.Action");
    public string ImportAllHeader => L("DataStudio.ImportAll.Header");
    public string ImportAllNote => L("DataStudio.ImportAll.Note");
    public string ImportAllLabel => L("DataStudio.ImportAll.Action");
    public string MergeLabel => L("DataStudio.Merge");
    public string PathLabel => L("DataStudio.Path");
    public string PathPlaceholder => L("DataStudio.Path.Placeholder");
    public string ErrorsHeader => L("DataStudio.Errors.Header");
    public string ImportOkMessage => L("DataStudio.ImportOk");
    public string NothingLoaded => L("DataStudio.NothingLoaded");
    public string ConfirmDeleteTitle => L("DataStudio.ConfirmDelete.Title");
    public string WarningTitle => L("DataStudio.Warning.Title");
    public string WarningBody => L("DataStudio.Warning.Body");
    public string CommonCancel => L("Common.Cancel");
    public string CommonContinue => L("Common.Continue");
    public string CommonDone => L("Common.Done");
    public string BackText => L("Common.Back");
    public string FailedMessage => L("DataStudio.Failed");

    // ---- Kind picker (machine tokens shown verbatim — technical names stay Latin) ----

    public ObservableCollection<string> Kinds { get; } = new();
    private string? _selectedKind;
    public string? SelectedKind
    {
        get => _selectedKind;
        set { if (Set(ref _selectedKind, value)) NotifyCommands(); }
    }
    public bool HasKinds => Kinds.Count > 0;
    public string NoKindsNote => L("DataStudio.NoKinds");

    // ---- Entity editor -----------------------------------------------------------

    private string _entityId = string.Empty;
    public string EntityId { get => _entityId; set { if (Set(ref _entityId, value)) NotifyCommands(); } }

    private string _editorText = string.Empty;
    public string EditorText { get => _editorText; set { if (Set(ref _editorText, value)) NotifyCommands(); } }

    private bool _hasEditorContent;
    public bool HasEditorContent => _hasEditorContent;

    // ---- Import results (error keys rendered through the Tr pattern) ----------------

    public ObservableCollection<DataStudioErrorRow> ImportErrors { get; } = new();
    public bool HasErrors => ImportErrors.Count > 0;
    /// <summary>True after a clean Apply/Import (green honest "accepted", distinct from "nothing yet").</summary>
    private bool _lastWriteOk;
    public bool LastWriteOk => _lastWriteOk;

    // ---- Import All inputs -----------------------------------------------------------

    private bool _merge = true;
    public bool Merge { get => _merge; set => Set(ref _merge, value); }

    private string _importPath = string.Empty;
    public string ImportPath { get => _importPath; set { if (Set(ref _importPath, value)) NotifyCommands(); } }

    private string _exportedPath = string.Empty;
    /// <summary>Full path of the last real export (verbatim, monospace-ish in the UI). Shown only
    /// when a file actually exists there.</summary>
    public string ExportedPath => _exportedPath;
    public bool HasExportedPath => _exportedPath.Length > 0 && SafeExists(_exportedPath);

    // ---- Commands ----------------------------------------------------------------------

    public Command LoadEntityCommand { get; }
    public Command ApplyCommand { get; }
    public Command DeleteCommand { get; }
    public Command ExportAllCommand { get; }
    public Command ImportAllCommand { get; }
    public Command BackCommand { get; }

    /// <summary>Shell-route "advanced off" seam: the page pops itself when the user declines the
    /// first-entry warning. Executed page-side to keep Shell out of the VM.</summary>
    public event Func<Task>? BackRequested;

    private void NotifyCommands()
    {
        LoadEntityCommand.ChangeCanExecute();
        ApplyCommand.ChangeCanExecute();
        DeleteCommand.ChangeCanExecute();
        ExportAllCommand.ChangeCanExecute();
        ImportAllCommand.ChangeCanExecute();
    }

    // ---- Load / session gate -------------------------------------------------------------

    /// <summary>Returns false when the user declined the raw-data warning (page backs out).</summary>
    public async Task<bool> LoadAsync()
    {
        if (!_sessionWarned)
        {
            _sessionWarned = true; // warned is warned — declining must not re-arm it per visit
            var go = await ConfirmAsync(WarningTitle, WarningBody, CommonContinue, CommonCancel);
            if (!go)
            {
                if (BackRequested is { } back) await back();
                return false;
            }
        }
        await LoadKindsAsync();
        RaiseAll();
        return true;
    }

    private async Task LoadKindsAsync()
    {
        if (_catalog is null) return;
        IsBusy = true;
        try
        {
            var kinds = await _catalog.GetKindsAsync();
            Kinds.Clear();
            foreach (var k in kinds) Kinds.Add(k);
            SelectedKind ??= Kinds.FirstOrDefault();
        }
        catch { /* keep whatever the picker already shows; no invented kinds */ }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasKinds));
            Raise(nameof(NoKindsNote));
            NotifyCommands();
        }
    }

    private async Task LoadEntityAsync()
    {
        if (_catalog is null || SelectedKind is not { Length: > 0 } kind) return;
        var id = EntityId.Trim();
        if (id.Length == 0) return;
        IsBusy = true;
        try
        {
            var json = await _catalog.ExportEntityAsync(kind, id);
            EditorText = json ?? string.Empty;
            _hasEditorContent = EditorText.Trim().Length > 0;
            _lastWriteOk = false;
        }
        catch { EditorText = string.Empty; _hasEditorContent = false; }
        finally
        {
            IsBusy = false;
            Raise(nameof(HasEditorContent));
            Raise(nameof(LastWriteOk));
            NotifyCommands();
        }
    }

    private async Task ApplyAsync()
    {
        if (_catalog is null || SelectedKind is not { Length: > 0 } kind) return;
        var json = EditorText;
        if (json.Trim().Length == 0) return;
        IsBusy = true;
        try
        {
            var errors = await _catalog.ImportEntityAsync(kind, json);
            RenderErrors(errors);
            _lastWriteOk = errors.Count == 0;
        }
        catch
        {
            RenderErrors(Array.Empty<string>());
            _lastWriteOk = false;
            await NotifyAsync(Title, FailedMessage);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(LastWriteOk));
            NotifyCommands();
        }
    }

    private async Task DeleteAsync()
    {
        if (_catalog is null || SelectedKind is not { Length: > 0 } kind) return;
        var id = EntityId.Trim();
        if (id.Length == 0) return;
        // Explicit confirm first — this is the destructive path of the raw editor.
        var go = await ConfirmAsync(ConfirmDeleteTitle,
            L("DataStudio.ConfirmDelete.Body", kind, id), CommonDone, CommonCancel);
        if (!go) return;
        IsBusy = true;
        try
        {
            var errors = await _catalog.DeleteEntityAsync(kind, new[] { id });
            RenderErrors(errors);
            _lastWriteOk = errors.Count == 0;
            if (_lastWriteOk)
            {
                EditorText = string.Empty;
                _hasEditorContent = false;
                Raise(nameof(HasEditorContent));
            }
        }
        catch { await NotifyAsync(Title, FailedMessage); }
        finally
        {
            IsBusy = false;
            Raise(nameof(LastWriteOk));
        }
    }

    private const string ExportFileName = "livora_datastudio_export.json";

    /// <summary>Whole-store export written to the app data dir (the JsonFileStore/LocalDataFiles
    /// directory idiom), then the REAL path is shown. See class notes: no save-picker exists in
    /// this MAUI version, so the honest desktop flow is write-here-and-tell-where.</summary>
    private async Task ExportAllAsync()
    {
        if (_catalog is null) return;
        IsBusy = true;
        try
        {
            var json = await _catalog.ExportAllAsync();
            var dir = Path.Combine(FileSystem.AppDataDirectory, "LIVORA");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ExportFileName);
            await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8);
            _exportedPath = path;
        }
        catch { _exportedPath = string.Empty; await NotifyAsync(Title, FailedMessage); }
        finally
        {
            IsBusy = false;
            Raise(nameof(ExportedPath));
            Raise(nameof(HasExportedPath));
        }
    }

    private async Task ImportAllAsync()
    {
        if (_catalog is null) return;
        var path = ImportPath.Trim();
        if (path.Length == 0) return;
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);
        }
        catch
        {
            await NotifyAsync(Title, L("DataStudio.PathMissing"));
            return;
        }
        IsBusy = true;
        try
        {
            var errors = await _catalog.ImportAllAsync(json, merge: Merge);
            RenderErrors(errors);
            _lastWriteOk = errors.Count == 0;
        }
        catch
        {
            RenderErrors(Array.Empty<string>());
            _lastWriteOk = false;
            await NotifyAsync(Title, FailedMessage);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(LastWriteOk));
        }
    }

    private void RenderErrors(IReadOnlyList<string> errorKeys)
    {
        ImportErrors.Clear();
        foreach (var key in errorKeys) ImportErrors.Add(new DataStudioErrorRow(key));
        Raise(nameof(HasErrors));
    }

    // ---- Busy / plumbing -----------------------------------------------------------------

    private bool _isBusy;
    /// <summary>Drives <c>BaseContentPage.IsLoading</c> (dim + spinner + input lock).</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static async Task GoBackAsync()
    {
        try
        {
            var shell = Shell.Current;
            if (shell is not null)
            {
                if (shell.Navigation.ModalStack.Count > 0) { await shell.Navigation.PopModalAsync(); return; }
                if (shell.Navigation.NavigationStack.Count > 1) { await shell.Navigation.PopAsync(); return; }
            }
        }
        catch { /* a failed pop is a platform quirk, not a user error */ }
    }

    private static string T(string key, params object[] args)
    {
        var loc = ServiceHelper.TryGet<ILocalizationService>();
        if (loc is null) return $"[{key}]";
        return args.Length == 0 ? loc[key] : loc.T(key, args);
    }

    private static async Task NotifyAsync(string title, string body)
    {
        var page = PageHost();
        if (page is null) return;
        try { await page.DisplayAlertAsync(title, body, T("Common.Done")); }
        catch { /* a failed dialog must never escalate */ }
    }

    private static async Task<bool> ConfirmAsync(string title, string body, string accept, string cancel)
    {
        var page = PageHost();
        if (page is null) return true; // no host: don't trap the user behind an undeliverable dialog
        try { return await page.DisplayAlertAsync(title, body, accept, cancel); }
        catch { return false; }
    }

    private static Microsoft.Maui.Controls.Page? PageHost()
        => Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(HasCatalog), nameof(HasKinds), nameof(HasEditorContent), nameof(HasErrors),
            nameof(LastWriteOk), nameof(ExportedPath), nameof(HasExportedPath),
            nameof(NoKindsNote), nameof(NothingLoaded),
        }) Raise(name);
        foreach (var row in ImportErrors) row.RaiseLanguage();
        NotifyCommands();
    }

    private static readonly string[] LocalizedProperties =
    {
        nameof(Title), nameof(AdvancedBanner), nameof(EntityHeader), nameof(KindLabel),
        nameof(EntityIdLabel), nameof(EntityIdPlaceholder), nameof(LoadLabel), nameof(ApplyLabel),
        nameof(DeleteLabel), nameof(ExportAllHeader), nameof(ExportAllNote), nameof(ExportAllLabel),
        nameof(ImportAllHeader), nameof(ImportAllNote), nameof(ImportAllLabel), nameof(MergeLabel),
        nameof(PathLabel), nameof(PathPlaceholder), nameof(ErrorsHeader), nameof(ImportOkMessage),
        nameof(NothingLoaded), nameof(ConfirmDeleteTitle),
        nameof(WarningTitle), nameof(WarningBody), nameof(CommonCancel), nameof(CommonContinue),
        nameof(CommonDone), nameof(BackText), nameof(FailedMessage), nameof(NotAvailable),
 nameof(NoKindsNote), nameof(NothingLoaded),
 };

    protected override void OnLanguageChanged()
    {
        foreach (var key in LocalizedProperties) Raise(key);
        RaiseAll();
    }
}

/// <summary>
/// One import/reject error. Keeps the RAW key (what the catalog returned — the debuggable truth,
/// also what a support export needs) next to its localized rendering through the existing Tr
/// pattern; an unresolvable key renders as the key itself, never as a crash or a blank row.
/// </summary>
public sealed class DataStudioErrorRow : INotifyPropertyChanged
{
    public string Key { get; }
    public DataStudioErrorRow(string key) => Key = key ?? string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Text
    {
        get
        {
            var loc = ServiceHelper.TryGet<ILocalizationService>();
            if (loc is null) return $"[{Key}]";
            var value = loc[Key];
            return value == $"[{Key}]" ? Key : value;
        }
    }

    internal void RaiseLanguage() => Raise(nameof(Text));
}
