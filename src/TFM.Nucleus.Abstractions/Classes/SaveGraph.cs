using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus
{
    /// <summary>
    /// What a save of some root objects reaches, walked once: the children held in their child
    /// collections at every depth, the linked objects their <c>[LinkedObject(forceSave: true)]</c>
    /// properties cascade the save into, the members held in their linked collections, and the
    /// stored children and members removed from those collections.
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
        private readonly List<ICoreObject> _members = [];
        private readonly List<ICoreObject> _removedMembers = [];

        private SaveGraph(int roots)
        {
            _seen = new HashSet<(Type, Guid)>(roots);
            _roots = new List<ICoreObject>(roots);
        }

        /// <summary>
        /// True when a save of <paramref name="obj"/> can reach anything besides the object itself:
        /// its type has child collections, forceSave links or linked collections. Decided once per
        /// type, so a save of a type that reaches nothing skips the walk, and its cost, altogether.
        /// </summary>
        public static bool CanReachDependents(ICoreObject obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return _reachesDependents.GetOrAdd(obj.GetType(), static (_, schema) =>
                schema.HasNucleusChildCollections
                || schema.HasLinkedCollections
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

        /// <summary>
        /// The members of linked collections the save writes, neither roots, children nor links:
        /// each one new, changed, or holding changed or removed children. Their own children are
        /// written with them, after the roots.
        /// </summary>
        public IReadOnlyList<ICoreObject> Members => _members;

        /// <summary>
        /// The stored members removed from linked collections in the graph, whose rows the save
        /// deletes. Each is a root of its own, deleted as its type allows a direct delete.
        /// </summary>
        public IReadOnlyList<ICoreObject> RemovedMembers => _removedMembers;

        /// <summary>
        /// The objects besides the roots whose rules a save runs: new or changed children, and the
        /// links and members it writes.
        /// </summary>
        public IEnumerable<ICoreObject> ChangedDependents => _children.Where(IsChanged).Concat(_links).Concat(_members);

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
                graph.VisitLinkedCollections(root);
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
                    VisitLinkedCollections(child);
                }
            }
            return dirty;
        }

        /// <summary>
        /// Follows an object's forceSave links, recording each one the save must write. A link is
        /// read as it is held, never through the property: a loaded link nobody has read is still
        /// only its stored ID, so it has no changes to write, and reading the property would load
        /// it from the database just to find that out.
        /// </summary>
        private void VisitLinks(ICoreObject obj)
        {
            if (!obj.ObjectSchema.HasLinkedObjects)
                return;
            foreach (var property in obj.ObjectSchema.LinkedObjectProperties ?? [])
            {
                if (!property.ForceLinkedSave || obj.GetPropertyValue(property.Name) is not ICoreObject link || !_seen.Add(Key(link)))
                    continue;
                var dirtyBelow = VisitChildren(link);
                if (IsChanged(link) || dirtyBelow)
                    _links.Add(link);
                VisitLinks(link);
                VisitLinkedCollections(link);
            }
        }

        /// <summary>
        /// Walks an object's linked collections, recording each held member the save must write
        /// and each stored member removed. Only collections that exist are walked, and only the
        /// members they hold: reading them never loads a collection, and the members added to an
        /// unloaded one are held.
        /// </summary>
        private void VisitLinkedCollections(ICoreObject obj)
        {
            if (!obj.ObjectSchema.HasLinkedCollections)
                return;
            foreach (var collection in obj.NucleusLinkedCollections)
            {
                foreach (var removed in collection.RemovedMembers)
                    if (_removedSeen.Add(Key(removed)))
                        _removedMembers.Add(removed);
                foreach (var member in collection.Members)
                {
                    if (!_seen.Add(Key(member)))
                        continue;
                    var dirtyBelow = VisitChildren(member);
                    if (IsChanged(member) || dirtyBelow)
                        _members.Add(member);
                    VisitLinks(member);
                    VisitLinkedCollections(member);
                }
            }
        }

        private static (Type, Guid) Key(ICoreObject obj) => (obj.GetType(), obj.ID);
    }
}
