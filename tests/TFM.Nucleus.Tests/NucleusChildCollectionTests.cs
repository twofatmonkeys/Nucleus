using System.Collections.Specialized;

namespace TFM.Nucleus.Tests
{
    public class TestCustomer : CoreObject<TestCustomer>
    {
        [DatabaseColumn]
        public string? Name
        {
            get => GetValue<string>();
            set => SetValue(value);
        }
    }

    public class TestOrder : CoreChildObject<TestOrder, TestCustomer>
    {
        [DatabaseColumn]
        public decimal Total
        {
            get => GetValue<decimal>();
            set => SetValue(value);
        }
    }

    public class NucleusChildCollectionTests
    {
        private static (NucleusChildCollection<TestOrder, TestCustomer> Collection,
                        TestCustomer Parent,
                        List<NotifyCollectionChangedEventArgs> Events) NewTracked()
        {
            var parent = new TestCustomer { Name = "Parent" };
            var collection = new NucleusChildCollection<TestOrder, TestCustomer>(parent);
            var events = new List<NotifyCollectionChangedEventArgs>();
            collection.CollectionChanged += (_, e) => events.Add(e);
            return (collection, parent, events);
        }

        [Fact]
        public void Add_BindsParentAndRaisesAddEvent()
        {
            var (collection, parent, events) = NewTracked();
            var order = new TestOrder();

            collection.Add(order);

            Assert.Same(parent, (object?)order.Parent);
            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Add, e.Action);
            Assert.Single(collection);
        }

        [Fact]
        public void Add_SameChildTwice_ThrowsParentAlreadyBound()
        {
            var (collection, _, _) = NewTracked();
            var order = new TestOrder();
            collection.Add(order);

            Assert.Throws<CoreObjectException>(() => collection.Add(order));
        }

        // A new child has no row, so removing one is always allowed and leaves nothing to delete.
        // Removing stored children, and recording them for the save, is covered in ChildModelTests.

        [Fact]
        public void Remove_NewChild_DropsItAndRaisesEvent()
        {
            var (collection, _, events) = NewTracked();
            var order = new TestOrder();
            collection.Add(order);
            events.Clear();

            var removed = collection.Remove(order);

            Assert.True(removed);
            Assert.Empty(collection);
            Assert.Empty(collection.RemovedChildren);
            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Remove, e.Action);
        }

        [Fact]
        public void RemoveAt_NewChild_DropsItAndRaisesEvent()
        {
            var (collection, _, events) = NewTracked();
            var order = new TestOrder();
            collection.Add(order);
            events.Clear();

            collection.RemoveAt(0);

            Assert.Empty(collection);
            Assert.Empty(collection.RemovedChildren);
            Assert.Equal(NotifyCollectionChangedAction.Remove, Assert.Single(events).Action);
        }

        [Fact]
        public void Clear_NewChildren_DropsThemAndRaisesReset()
        {
            var (collection, _, events) = NewTracked();
            collection.Add(new TestOrder());
            collection.Add(new TestOrder());
            events.Clear();

            collection.Clear();

            Assert.Empty(collection);
            Assert.Empty(collection.RemovedChildren);
            Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(events).Action);
        }

        [Fact]
        public void AcknowledgeRemovals_ClearsPendingSet()
        {
            var (collection, _, _) = NewTracked();
            var order = new TestOrder();
            collection.Add(order);
            collection.Remove(order);

            collection.AcknowledgeRemovals();

            Assert.Empty(collection.RemovedChildren);
        }

        [Fact]
        public void Insert_BindsParentAtIndex()
        {
            var (collection, parent, events) = NewTracked();
            collection.Add(new TestOrder());
            events.Clear();
            var inserted = new TestOrder();

            collection.Insert(0, inserted);

            Assert.Same(inserted, collection[0]);
            Assert.Same(parent, (object?)inserted.Parent);
            Assert.Equal(0, Assert.Single(events).NewStartingIndex);
        }

        [Fact]
        public void Remove_UntrackedChild_ReturnsFalseWithoutRecording()
        {
            var (collection, _, _) = NewTracked();

            var removed = collection.Remove(new TestOrder());

            Assert.False(removed);
            Assert.Empty(collection.RemovedChildren);
        }
    }
}
