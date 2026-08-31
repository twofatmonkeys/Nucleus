using System.Collections.Specialized;

namespace TFM.Nucleus.Tests
{
    public class NucleusCollectionTests
    {
        private static (NucleusCollection<TestWidget> Collection, List<NotifyCollectionChangedEventArgs> Events) NewTracked()
        {
            var collection = new NucleusCollection<TestWidget>();
            var events = new List<NotifyCollectionChangedEventArgs>();
            collection.CollectionChanged += (_, e) => events.Add(e);
            return (collection, events);
        }

        [Fact]
        public void Add_RaisesAddEvent()
        {
            var (collection, events) = NewTracked();
            var widget = new TestWidget { Name = "A" };

            collection.Add(widget);

            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Add, e.Action);
            Assert.Same(widget, e.NewItems![0]);
            Assert.Single(collection);
        }

        [Fact]
        public void AddRange_RaisesSingleAddEvent()
        {
            var (collection, events) = NewTracked();

            collection.AddRange([new TestWidget(), new TestWidget()]);

            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Add, e.Action);
            Assert.Equal(2, e.NewItems!.Count);
            Assert.Equal(2, collection.Count);
        }

        [Fact]
        public void Remove_RaisesRemoveEvent()
        {
            var (collection, events) = NewTracked();
            var widget = new TestWidget();
            collection.Add(widget);
            events.Clear();

            var removed = collection.Remove(widget);

            Assert.True(removed);
            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Remove, e.Action);
            Assert.Empty(collection);
        }

        [Fact]
        public void Clear_RaisesResetEvent()
        {
            var (collection, events) = NewTracked();
            collection.Add(new TestWidget());
            events.Clear();

            collection.Clear();

            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
            Assert.Empty(collection);
        }

        [Fact]
        public void Insert_RaisesAddEventAtIndex()
        {
            var (collection, events) = NewTracked();
            collection.Add(new TestWidget());
            events.Clear();
            var inserted = new TestWidget();

            collection.Insert(0, inserted);

            var e = Assert.Single(events);
            Assert.Equal(0, e.NewStartingIndex);
            Assert.Same(inserted, collection[0]);
        }

        [Fact]
        public void Dispose_ClearsItemsAndMutesEvents()
        {
            var (collection, events) = NewTracked();
            collection.Add(new TestWidget());
            events.Clear();

            collection.Dispose();

            Assert.Empty(collection);
            Assert.Empty(events);
        }
    }
}
