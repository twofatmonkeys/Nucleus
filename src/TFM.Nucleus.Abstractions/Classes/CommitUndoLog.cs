using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace TFM.Nucleus
{
    /// <summary>
    /// What the saves in one transaction changed in memory, so it can be put back if the
    /// transaction does not commit. A data context commits each object it saves as the writes
    /// run, recording here what the commit changed; when the transaction commits the log is
    /// cleared, and when it rolls back, or its commit fails, the log is reverted, which leaves
    /// each object as it was before its first save in the transaction, with any change made
    /// since still pending.
    /// </summary>
    /// <remarks>
    /// Entries are keyed by reference, one per object, so an object saved several times in one
    /// transaction keeps the state from before the first save. Safe to use from several threads,
    /// because the contexts in one transaction tree share their root's log.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class CommitUndoLog
    {
        private readonly object _lock = new();
        private readonly Dictionary<object, ICommitUndo> _entries = new(ReferenceEqualityComparer.Instance);
        private readonly List<ICommitUndo> _order = [];

        /// <summary>True when nothing has been recorded since the log was last cleared or reverted.</summary>
        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                    return _order.Count == 0;
            }
        }

        /// <summary>
        /// The entry recorded for <paramref name="owner"/>, created by <paramref name="create"/> the
        /// first time the owner is committed in this transaction.
        /// </summary>
        public T GetOrAdd<T>(object owner, Func<T> create) where T : class, ICommitUndo
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(create);
            lock (_lock)
            {
                if (_entries.TryGetValue(owner, out var existing))
                    return existing as T ?? throw new InvalidOperationException(
                        $"The undo entry recorded for {owner} is a {existing.GetType().Name}, not a {typeof(T).Name}.");
                var entry = create();
                _entries.Add(owner, entry);
                _order.Add(entry);
                return entry;
            }
        }

        /// <summary>Puts back everything recorded, newest first, then empties the log.</summary>
        public void Revert()
        {
            ICommitUndo[] entries;
            lock (_lock)
            {
                entries = [.. _order];
                _entries.Clear();
                _order.Clear();
            }
            for (var i = entries.Length - 1; i >= 0; i--)
                entries[i].Revert();
        }

        /// <summary>The transaction committed: forgets everything recorded.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _order.Clear();
            }
        }
    }

    /// <summary>One object's entry in a <see cref="CommitUndoLog"/>: puts its in-memory state back.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ICommitUndo
    {
        void Revert();
    }
}
