using System.Collections;
using System.Collections.Specialized;

namespace LIVORA.Presentation.Components;
/// <summary>
/// A vertical stack that renders an items collection using a DataTemplate (zero-dependency
/// equivalent of BindableLayout). Rebuilds children when the collection or template changes,
/// so Today/Health/Goals stay declarative without nested scrolling.
///
/// LEAK AUDIT (why this file is deliberately unchanged): the old source of a CollectionChanged
/// subscription is detached in OnItemsSourceChanged before the new one is attached, and delegate
/// equality (target + method) makes the -= effective, so swapping ItemsSource — including
/// null -> collection and collection -> collection — leaves no subscription behind.
///
/// COST AUDIT (why a full rebuild per event is acceptable here): the largest collection in the app
/// is 6 items (Today's DailyMetrics is fixed at 4, Habits 3, ActiveGoals 3, Programs ~6/7), so a
/// load costs at most ~21 template instantiations instead of ~6 — sub-millisecond, and all of it
/// already happens behind the async pipeline (provider + baselines + rule engine). Coalescing the
/// Clear()/Add() burst onto a dispatcher turn was tried and rejected: it defers children to a later
/// queue item than the mutation, which risks a frame with stale/empty children for no measurable win.
/// If a screen ever binds a list in the dozens, revisit coalescing then.
/// </summary>
public class ItemsStackLayout : VerticalStackLayout
{
    public static readonly BindableProperty ItemsSourceProperty =
        BindableProperty.Create(nameof(ItemsSource), typeof(IList), typeof(ItemsStackLayout),
            propertyChanged: OnItemsSourceChanged);

    public static readonly BindableProperty ItemTemplateProperty =
        BindableProperty.Create(nameof(ItemTemplate), typeof(DataTemplate), typeof(ItemsStackLayout),
            propertyChanged: (b, o, n) => ((ItemsStackLayout)b).Rebuild());

    public IList? ItemsSource
    {
        get => (IList?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    private static void OnItemsSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (ItemsStackLayout)bindable;
        // Unsubscribe first: a replaced collection must never keep a dead layout alive.
        if (oldValue is INotifyCollectionChanged oldIncc)
            oldIncc.CollectionChanged -= view.OnCollectionChanged;
        if (newValue is INotifyCollectionChanged newIncc)
            newIncc.CollectionChanged += view.OnCollectionChanged;
        view.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (ItemsSource is null || ItemTemplate is null) return;

        foreach (var item in ItemsSource)
        {
            if (ItemTemplate.CreateContent() is not View view) continue;
            view.BindingContext = item;
            Children.Add(view);
        }
    }
}
