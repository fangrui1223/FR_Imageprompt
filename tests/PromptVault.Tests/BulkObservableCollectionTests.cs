using System.Collections.Specialized;
using PromptVault.App;

namespace PromptVault.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAllThirtyThousandItemsRaisesOneReset()
    {
        var collection = new BulkObservableCollection<int> { -1 };
        var collectionEvents = new List<NotifyCollectionChangedEventArgs>();
        var countChanges = 0;
        var indexerChanges = 0;
        collection.CollectionChanged += (_, args) => collectionEvents.Add(args);
        ((System.ComponentModel.INotifyPropertyChanged)collection).PropertyChanged +=
            (_, args) =>
            {
                if (args.PropertyName == nameof(collection.Count)) countChanges++;
                if (args.PropertyName == "Item[]") indexerChanges++;
            };

        collection.ReplaceAll(Enumerable.Range(0, 30_000));

        Assert.Equal(30_000, collection.Count);
        Assert.Equal(0, collection[0]);
        Assert.Equal(29_999, collection[^1]);
        var reset = Assert.Single(collectionEvents);
        Assert.Equal(NotifyCollectionChangedAction.Reset, reset.Action);
        Assert.Equal(1, countChanges);
        Assert.Equal(1, indexerChanges);
    }
}
