using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus
{
    /// <summary>
    /// The many side of a one-to-many relationship between two root types, held on the one, as in
    /// <c>Campaign.Vouchers</c>: the stored <typeparamref name="TMany"/> rows whose key property
    /// (named by <c>[LinkedBy]</c> on the collection property) holds the one's ID. A member is a
    /// root of its own, loaded, saved and deleted on its own as well as through the one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection loads its stored members in one query the first time a member is read
    /// (enumerating, <c>Count</c>, the indexer, <c>Contains</c>, <c>IndexOf</c>, <c>Remove</c>,
    /// <c>Clear</c>, <c>Insert</c>, <c>CopyTo</c>), on the synchronous path, once however many
    /// threads read it. <see cref="LoadAsync"/> loads them without blocking a thread. Reading the
    /// property alone loads nothing, and neither does <see cref="Add"/>. A listing includes the
    /// collection instead (<c>Include(c =&gt; c.Vouchers)</c>), which loads every row's members in
    /// the same round trip. The members of a new one are all the ones added, so it starts loaded.
    /// </para>
    /// <para>
    /// Adding a member sets its key to the one, and saving the one writes the new and changed
    /// members it holds in the same transaction. Removing a stored member deletes it with that
    /// save when its type is <c>[Deletable]</c>, and throws otherwise; change the key and save
    /// the member to move it to another one.
    /// </para>
    /// </remarks>
    public class NucleusLinkedCollection<TMany, TOne>
        : IList<TMany>, IList, IReadOnlyList<TMany>, INucleusLinkedCollection<TMany, TOne>
        where TMany : ICoreObject<TMany>, new()
        where TOne : ICoreObject<TOne>, new()
    {
        private readonly TOne _owner;
        private readonly IPropertySchema _key;
        private readonly List<TMany> _inner = [];
        // Stored members taken out since the last save; a new member taken out is simply dropped.
        private readonly List<TMany> _removed = [];
        private readonly object _loadLock = new();
        private volatile bool _isLoaded;

        /// <summary>The collection of the one's <paramref name="propertyName"/> property.</summary>
        public NucleusLinkedCollection(TOne owner, string propertyName)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentException.ThrowIfNullOrEmpty(propertyName);
            _owner = owner;
            PropertyName = propertyName;
            var schema = Owner.ObjectSchema;
            var property = schema.LinkedCollectionProperties.FirstOrDefault(p => p.Name == propertyName)
                ?? throw new ArgumentException($"{typeof(TOne).Name}.{propertyName} is not a linked collection property.", nameof(propertyName));
            _key = schema.GetLinkedCollectionKey(property);
            _isLoaded = Owner.IsNew;
        }

        private ICoreObject Owner => _owner;

        ICoreObject INucleusLinkedCollection.Owner => Owner;

        /// <inheritdoc/>
        public Type MemberType => typeof(TMany);

        /// <inheritdoc/>
        public string PropertyName { get; }

        /// <inheritdoc/>
        public bool IsLoaded => _isLoaded;

        [field: NonSerialized]
        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        // ---- Loading -----------------------------------------------------------------------------

        private ILinkedObjectLoader Loader =>
            Owner.LinkedObjectLoader
            ?? NucleusRuntime.Current as ILinkedObjectLoader
            ?? throw new InvalidOperationException(
                $"{typeof(TOne).Name}.{PropertyName} cannot load its members: the {typeof(TOne).Name} was not loaded by Nucleus " +
                "and the ambient runtime cannot load linked collections.");

        /// <inheritdoc/>
        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            _isLoaded ? Task.CompletedTask : Loader.LoadLinkedCollectionAsync(this, cancellationToken);

        /// <summary>Loads the stored members the first time one is read, once, through the stack that loaded the one.</summary>
        private void EnsureLoaded()
        {
            if (_isLoaded)
                return;
            lock (_loadLock)
            {
                if (!_isLoaded)
                    Loader.LoadLinkedCollection(this);
            }
            if (!_isLoaded)
                throw new InvalidOperationException($"The loader did not load the members of {typeof(TOne).Name}.{PropertyName}.");
        }

        void INucleusLinkedCollection.AttachLoaded(IEnumerable<ICoreObject> members)
        {
            ArgumentNullException.ThrowIfNull(members);
            lock (_loadLock)
            {
                var present = _inner.Select(m => ((ICoreObject)m).ID).ToHashSet();
                foreach (var member in members)
                {
                    if (member is not TMany typed)
                        throw new ArgumentException($"{member.GetType().Name} is not a {typeof(TMany).Name}.", nameof(members));
                    if (!present.Add(member.ID))
                        continue;
                    // A [LinkedObject] key holds the one's ID as loaded; bind the one itself as the
                    // stored value, so reading it runs no query and the member is not dirty.
                    if (_key.IsLinkedObject && member.GetPropertyValue(_key.Name) is Guid id && id == Owner.ID)
                        member.BindValue(_key.Name, Owner);
                    _inner.Add(typed);
                }
                _isLoaded = true;
            }
        }

        // ---- Saving ------------------------------------------------------------------------------

        IEnumerable<ICoreObject> INucleusLinkedCollection.Members => _inner.Cast<ICoreObject>();

        IEnumerable<ICoreObject> INucleusLinkedCollection.RemovedMembers => _removed.Cast<ICoreObject>();

        void INucleusLinkedCollection.AcknowledgeRemovals() => _removed.Clear();

        /// <summary>Points the member's key at the one.</summary>
        private void Link(TMany member)
        {
            if (_key.IsLinkedObject)
            {
                if (!ReferenceEquals(member.GetPropertyValue(_key.Name), Owner))
                    _key.SetValue(member, Owner);
            }
            else if (!Equals(member.GetPropertyValue(_key.Name), Owner.ID))
                _key.SetValue(member, Owner.ID);
        }

        /// <summary>
        /// Throws, before anything changes, when a stored member about to leave the collection
        /// cannot be deleted: leaving it out would leave its row pointing at the one with nothing
        /// to say so.
        /// </summary>
        private static void EnsureRemovable(TMany member)
        {
            var obj = (ICoreObject)member;
            if (obj.IsNew || obj.ObjectSchema.IsDeletable)
                return;
            throw new InvalidOperationException(
                $"Removing a stored {typeof(TMany).Name} from {typeof(TOne).Name} would leave its row pointing at the {typeof(TOne).Name}, " +
                $"and {typeof(TMany).Name} rows cannot be deleted. Declare [Deletable] on {typeof(TMany).Name} to have the save delete it, " +
                $"or set its key to another {typeof(TOne).Name} and save it.");
        }

        private void Unlink(TMany member)
        {
            if (!((ICoreObject)member).IsNew)
                _removed.Add(member);
        }

        private bool Bind(TMany member, int index)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (_inner.Any(m => ReferenceEquals(m, member)))
                return false;
            var removed = _removed.FindIndex(m => ReferenceEquals(m, member));
            if (removed >= 0)
                _removed.RemoveAt(removed);
            Link(member);
            _inner.Insert(index, member);
            return true;
        }

        // ---- The list ----------------------------------------------------------------------------

        /// <summary>Adds a member and sets its key to the one. Does not load the collection.</summary>
        public void Add(TMany member)
        {
            if (Bind(member, _inner.Count))
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, member));
        }

        /// <summary>Adds members and sets their keys to the one. Does not load the collection.</summary>
        public void AddRange(IEnumerable<TMany> members)
        {
            ArgumentNullException.ThrowIfNull(members);
            var added = new List<TMany>();
            foreach (var member in members)
                if (Bind(member, _inner.Count))
                    added.Add(member);
            if (added.Count > 0)
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added));
        }

        public TMany this[int index]
        {
            get
            {
                EnsureLoaded();
                return _inner[index];
            }
            set
            {
                EnsureLoaded();
                ArgumentNullException.ThrowIfNull(value);
                var previous = _inner[index];
                if (ReferenceEquals(previous, value))
                    return;
                EnsureRemovable(previous);
                _inner.RemoveAt(index);
                Unlink(previous);
                Bind(value, index);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, value, previous, index));
            }
        }

        public int Count
        {
            get
            {
                EnsureLoaded();
                return _inner.Count;
            }
        }

        public bool IsReadOnly => false;

        public bool Contains(TMany item)
        {
            EnsureLoaded();
            return _inner.Contains(item);
        }

        public void CopyTo(TMany[] array, int arrayIndex)
        {
            EnsureLoaded();
            _inner.CopyTo(array, arrayIndex);
        }

        public int IndexOf(TMany item)
        {
            EnsureLoaded();
            return _inner.IndexOf(item);
        }

        public void Insert(int index, TMany item)
        {
            EnsureLoaded();
            if (Bind(item, index))
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }

        public bool Remove(TMany item)
        {
            EnsureLoaded();
            if (item == null)
                return false;
            var index = _inner.FindIndex(m => ReferenceEquals(m, item));
            if (index < 0)
                index = _inner.IndexOf(item);
            if (index < 0)
                return false;
            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            EnsureLoaded();
            var member = _inner[index];
            EnsureRemovable(member);
            _inner.RemoveAt(index);
            Unlink(member);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, member, index));
        }

        public void Clear()
        {
            EnsureLoaded();
            if (_inner.Count == 0)
                return;
            // All or nothing: every member is checked before any leaves.
            foreach (var member in _inner)
                EnsureRemovable(member);
            foreach (var member in _inner)
                Unlink(member);
            _inner.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public IEnumerator<TMany> GetEnumerator()
        {
            EnsureLoaded();
            return _inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void OnCollectionChanged(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

        // ---- Non-generic IList, for binding ------------------------------------------------------

        bool IList.IsFixedSize => false;
        bool IList.IsReadOnly => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;

        object? IList.this[int index]
        {
            get => this[index];
            set => this[index] = (TMany)value!;
        }

        int IList.Add(object? value)
        {
            Add((TMany)value!);
            return _inner.Count - 1;
        }

        void IList.Insert(int index, object? value) => Insert(index, (TMany)value!);
        void IList.Remove(object? value) => Remove((TMany)value!);
        bool IList.Contains(object? value) => Contains((TMany)value!);
        int IList.IndexOf(object? value) => IndexOf((TMany)value!);

        void ICollection.CopyTo(Array array, int index)
        {
            EnsureLoaded();
            ((ICollection)_inner).CopyTo(array, index);
        }
    }
}
