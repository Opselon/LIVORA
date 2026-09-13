using System.ComponentModel;
using System.Reflection;
using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Localization;
using Microsoft.Maui.Controls.Xaml;

namespace LIVORA.Presentation.Components;

/// <summary>
/// XAML markup extension that binds a target property to a localization key and keeps it correct
/// when the language (or a bound argument) changes at runtime:
///
///     &lt;Label Text="{localize:Tr Today.Title}" /&gt;
///     &lt;Label Text="{localize:Tr Common.StreakDays, Arg0=5}" /&gt;
///     &lt;Label Text="{localize:Tr Update.Latest, BindArg0=LatestVersion}" /&gt;
///
/// Why not "resolve once and return the string": the extension would freeze the language it first
/// saw, so the app looks half-translated after a live switch (the exact failure mode this project
/// cannot have — Persian is the default language and switching without restart is a requirement).
///
/// Mechanism: bindable targets (Text, Placeholder, SemanticProperties.Description, …) get a real
/// <see cref="BindableObject.SetBinding(BindableProperty, BindingBase)"/> against this instance, so
/// every <see cref="PropertyChanged"/> — a language switch or a bound argument change — re-sets the
/// property through the normal binding machinery, on every platform, with no per-page glue code.
///
/// Instances are shared by the XAML parser per (type, property) pair, so no per-page state is held:
/// the argument source is read from the *target's* BindingContext at resolve time.
/// </summary>
[ContentProperty(nameof(Key))]
public sealed class TrExtension : IMarkupExtension<object>, INotifyPropertyChanged
{
    public string? Key { get; set; }

    /// <summary>Static format arguments (values known in XAML).</summary>
    public string? Arg0 { get; set; }
    public string? Arg1 { get; set; }
    public string? Arg2 { get; set; }

    /// <summary>
    /// Binding paths on the target's BindingContext supplying {0}, {1}, {2}. When a bound value is
    /// not (yet) available the matching static Arg is used, so a partially-wired page still reads well.
    /// </summary>
    public string? BindArg0 { get; set; }
    public string? BindArg1 { get; set; }
    public string? BindArg2 { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private ILocalizationService? _loc;
    private BindableObject? _target;
    private INotifyPropertyChanged? _contextSubscription;
    private Action? _languageHandler;

    /// <summary>Never null: an unresolvable key renders as <c>[Key]</c> (a debuggable bug, not a crash).</summary>
    public object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
            return string.Empty;

        _loc = ServiceHelper.TryGet<ILocalizationService>();
        if (_loc is null)
            return $"[{Key}]"; // never throw while inflating a page: a visible key is a debuggable bug

        // Subscribe through the weak hook, NOT the singleton's event: a strong event subscription
        // from a singleton to an extension would keep every inflated page alive for the app's life.
        // The hook's weak entry dies exactly when this extension (and thus its page) is collected.
        LanguageHook.Subscribe(_languageHandler = () => Raise());

        var provideValueTarget = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        var target = provideValueTarget?.TargetObject as BindableObject;
        var targetProperty = provideValueTarget?.TargetProperty as BindableProperty;

        if (target is null || targetProperty is null)
            return Value; // design-time / non-bindable: current translation, no live updates

        _target = target;
        HookBindingContext(target);
        target.SetBinding(targetProperty, new Binding(nameof(Value), source: this, mode: BindingMode.OneWay));
        return target.GetValue(targetProperty);
    }

    // Declared non-nullable (object, not object?): the XAML source generator emits call sites
    // typed IMarkupExtension<object>, and an object? implementation there logs CS8619 on every
    // generated line. ProvideValue never returns null — an unresolvable key yields "[Key]".
    object IMarkupExtension<object>.ProvideValue(IServiceProvider serviceProvider)
        => ProvideValue(serviceProvider) ?? string.Empty;

    /// <summary>The string for the current language with the current argument values.</summary>
    public string Value
    {
        get
        {
            var loc = _loc ??= ServiceHelper.TryGet<ILocalizationService>();
            if (loc is null || string.IsNullOrWhiteSpace(Key)) return $"[{Key}]";
            var args = BuildArgs();
            return args.Length == 0 ? loc[Key!] : loc.T(Key!, [.. args]);
        }
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));

    private void HookBindingContext(BindableObject target)
    {
        // BindingContext is assigned after the page's InitializeComponent in most cases, so follow
        // changes to it rather than reading it once.
        target.BindingContextChanged += (_, _) =>
        {
            if (_contextSubscription is not null)
                _contextSubscription.PropertyChanged -= OnContextPropertyChanged;
            _contextSubscription = target.BindingContext as INotifyPropertyChanged;
            if (_contextSubscription is not null)
                _contextSubscription.PropertyChanged += OnContextPropertyChanged;
            Raise();
        };
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) { Raise(); return; }
        if (string.Equals(BindArg0?.Split('.')[^1], e.PropertyName, StringComparison.Ordinal) ||
            string.Equals(BindArg1?.Split('.')[^1], e.PropertyName, StringComparison.Ordinal) ||
            string.Equals(BindArg2?.Split('.')[^1], e.PropertyName, StringComparison.Ordinal) ||
            e.PropertyName.Length == 0 /* ObservableObject's blanket raise */)
            Raise();
    }

    private object[] BuildArgs()
    {
        var list = new List<object?>(3)
        {
            ResolveArg(BindArg0, Arg0),
            ResolveArg(BindArg1, Arg1),
            ResolveArg(BindArg2, Arg2),
        };
        // Trailing holes must not become empty strings: a plain key stays a plain key.
        while (list.Count > 0 && list[^1] is null) list.RemoveAt(list.Count - 1);
        // A hole in the middle renders as "" (never "null") so a half-wired argument can't leak
        // the word "null" into user-facing text.
        return list.Select(x => x ?? (object)string.Empty).ToArray();
    }

    private object? ResolveArg(string? bindPath, string? staticArg)
    {
        if (!string.IsNullOrEmpty(bindPath))
        {
            var source = _target?.BindingContext;
            if (source is not null)
            {
                var v = GetPathValue(source, bindPath!);
                if (v is not null) return v;
            }
        }
        return string.IsNullOrEmpty(staticArg) ? null : staticArg;
    }

    private static object? GetPathValue(object source, string path)
    {
        object? cur = source;
        foreach (var seg in path.Split('.'))
        {
            if (cur is null) return null;
            var pi = cur.GetType().GetProperty(seg, BindingFlags.Public | BindingFlags.Instance);
            if (pi is null) return null;
            cur = pi.GetValue(cur);
        }
        return cur;
    }
}
