using System.Collections.Specialized;
using System.Windows.Controls;
using ProPS1Studio.App.ViewModels;

namespace ProPS1Studio.App.Views;

public partial class DiagnosticsView
{
    private DiagnosticsViewModel? _subscribed;

    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e) => Subscribe();

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e) => Subscribe();

    private void Subscribe()
    {
        if (_subscribed is not null || DataContext is not DiagnosticsViewModel vm)
        {
            return;
        }

        _subscribed = vm;
        vm.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add &&
            _subscribed is { AutoScroll: true } &&
            _subscribed.Entries.Count > 0)
        {
            var last = _subscribed.Entries[^1];
            LogList.ScrollIntoView(last);
        }
    }
}
