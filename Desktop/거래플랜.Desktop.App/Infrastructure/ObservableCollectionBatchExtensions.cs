using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace 거래플랜.Desktop.App.Infrastructure;

public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications)
            return;

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
            return;

        base.OnPropertyChanged(e);
    }
}

public static class ObservableCollectionBatchExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        if (collection is ResettableObservableCollection<T> resettable)
        {
            resettable.ReplaceAll(items);
            return;
        }

        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}
