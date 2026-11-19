using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus
{
    /// <summary>
    /// What a save of some root objects reaches, walked once: the children held in their child
    /// collections at every depth, the linked objects their <c>[LinkedObject(forceSave: true)]</c>
    /// properties cascade the save into, and the stored children removed from those collections.
    /// Validation, the save's writes and the commit after it all read the graph from here, so
    /// they cannot disagree about what was saved.
    /// </summary>
    /// <remarks>
    /// Only children already held are walked: reading a collection's held children never loads
    /// one that was left out or is lazy, and the children added to such a collection are held.
    /// Each object is visited once, by type and ID, so cycles through links end.
    /// </remarks>
    public sealed class SaveGraph
    {
        private static readonly ConcurrentDictionary<Type, bool> _reachesDependents = [];

        private readonly HashSet<(Type, Guid)> _seen;
        private readonly HashSet<(Type, Guid)> _removedSeen = [];
        private readonly List<ICoreObject> _roots;
        private readonly List<ICoreObject> _children = [];
        private readonly List<ICoreObject> _links = [];
        private readonly List<ICoreObject> _removed = [];

        private SaveGraph(int roots)
        {
            _seen = new HashSet<(Type, Guid)>(roots);
            _roots = new List<ICoreObject>(roots);
        }

        /// <summary>
        /// True when a save of <paramref name="obj"/> can reach anything besides the object itself:
        /// its type has child collections, or forceSave links. Decided once per type, so a save of
        /// a type that reaches nothing skips the walk, and its cost, altogether.
        /// </summary>
        public static bool CanReachDependents(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return _reachesDependents.GetOrAdd(obj.GetType(), static (_, schema) =>
                schema.HasNucleusChildCollections
                || (schema.HasLinkedObjects && (schema.LinkedObjectProperties ?? []).Any(p => p.ForceLinkedSave)),
                obj.ObjectSchema);
        }

        /// <summary>The objects the save was asked for.</summary>
        public IReadOnlyList<ICoreObject> Roots => _roots;

        /// <summary>The children held in child collections below the roots and the written links, at every depth.</summary>
        public IReadOnlyList<ICoreObject> Children => _children;

        /// <summary>
        /// The forceSave links the save writes, neither roots nor children: each one new, changed,
        /// or holding changed or removed children. Their own children are written with them.
        /// </summary>
        public IReadOnlyList<ICoreObject> Links => _links;

        /// <summary>The stored children removed from collections in the graph, whose rows the save deletes.</summary>
        public IReadOnlyList<ICoreObject> Removed => _removed;

        /// <summary>The objects besides the roots whose rules a save runs: new or changed children, and the links it writes.</summary>
        public IEnumerable<ICoreObject> ChangedDependents => _children.Where(IsChanged).Concat(_links);

        /// <summary>True when the object has something to write: it is new, or has changes of its own.</summary>
        public static bool IsChanged(ICoreObject obj) =>
            obj.IsNew || (!obj.IsReadOnly && obj.ChangedProperties.Count > 0);

        /// <summary>Walks the graph below <paramref name="roots"/>.</summary>
        public static SaveGraph Walk(IEnumerable<ICoreObject> roots)
        {
            ArgumentNullException.ThrowIfNull(roots);
            var graph = new SaveGraph(roots is ICollection<ICoreObject> sized ? sized.Count : 0);
            foreach (var root in roots)
                if (root != null && graph._seen.Add(Key(root)))
                    graph._roots.Add(root);
            foreach (var root in graph._roots)
            {
                if (!CanReachDependents(root))
                    continue;
                graph.VisitChildren(root);
                graph.VisitLinks(root);
            }
            return graph;
        }

        /// <summary>Walks an object's collections; true when anything below it is new, changed or removed.</summary>
        private bool VisitChildren(ICoreObject obj)
        {
            var dirty = false;
            // NucleusChildCollections is an iterator, allocated on every call: skip it for a leaf.
            if (!obj.ObjectSchema.HasNucleusChildCollections)
                return false;
            foreach (var collection in obj.NucleusChildCollections)
            {
                foreach (var removed in collection.RemovedObjects)
                {
                    if (_removedSeen.Add(Key(removed)))
                        _removed.Add(removed);
                    dirty = true;
                }
                foreach (var child in collection.ChildObjects)
                {
                    if (!_seen.Add(Key(child)))
                        continue;
                    _children.Add(child);
                    if (IsChanged(child))
                        dirty = true;
                    if (VisitChildren(child))
                        dirty = true;
                    VisitLinks(child);
                }
            }
            return dirty;
        }

        /// <summary>Follows an object's forceSave links, recording each one the save must write.</summary>
        private void VisitLinks(ICoreObject obj)
        {
            if (!obj.ObjectSchema.HasLinkedObjects)
                return;
            foreach (var property in obj.ObjectSchema.LinkedObjectProperties ?? [])
            {
                if (!property.ForceLinkedSave || property.GetValue(obj) is not ICoreObject link || !_seen.Add(Key(link)))
                    continue;
                var dirtyBelow = VisitChildren(link);
                if (IsChanged(link) || dirtyBelow)
                    _links.Add(link);
                VisitLinks(link);
            }
        }

        private static (Type, Guid) Key(ICoreObject obj) => (obj.GetType(), obj.ID);
    }
}
