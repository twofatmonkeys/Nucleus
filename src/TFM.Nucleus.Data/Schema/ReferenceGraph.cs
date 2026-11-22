using System;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// Which entity types' rows hold the IDs of which others' rows, by chain: a root type with the
    /// child types below it, which a save writes and a delete removes together. A save writes a
    /// chain after the chains its rows point at, and a delete removes a chain before the chains
    /// that point at it, so no statement references a row that is not there yet or no longer is.
    /// </summary>
    /// <remarks>
    /// A row points at another through a stored <c>[LinkedObject]</c> column, through a
    /// <c>[LinkedBy]</c> key (declared on the one, held by the many) and through a child's parent.
    /// A parent is in its child's chain, so it orders nothing. A reference is to its declared type
    /// or to any type derived from it, because the value it holds may be either. Types that point
    /// at each other in a cycle keep the order they were given in, since no order serves them.
    /// </remarks>
    internal sealed class ReferenceGraph
    {
        /// <summary>A graph with no references, for a provider that stores no types yet.</summary>
        public static readonly ReferenceGraph Empty = new([]);

        // Chain root type -> the declared types its rows point at, outside its own chain.
        private readonly Dictionary<Type, Type[]> _targets;

        // Every type -> the root type of its chain.
        private readonly Dictionary<Type, Type> _chainOf;

        /// <summary>
        /// The references between <paramref name="schemas"/> and the child types below them, at
        /// every depth. A reference is declared on the type that holds it, or for a linked
        /// collection on the one, so the graph of a set of types needs only their own schemas.
        /// </summary>
        public ReferenceGraph(IEnumerable<IObjectSchema> schemas)
        {
            var all = new Dictionary<Type, IObjectSchema>();
            foreach (var schema in schemas)
                AddWithChildren(all, schema);
            _chainOf = all.Keys.ToDictionary(t => t, t => ChainRoot(t, all));
            var targets = new Dictionary<Type, HashSet<Type>>();
            foreach (var schema in all.Values)
            {
                var chain = _chainOf[schema.Type];
                if (schema.HasLinkedObjects)
                    foreach (var link in schema.LinkedObjectProperties ?? [])
                        if (link.IsDBColumn)
                            Add(targets, chain, link.Type);
                if (schema.HasLinkedCollections)
                    foreach (var collection in schema.LinkedCollectionProperties)
                        if (collection.LinkedType is { } many)
                            Add(targets, _chainOf.GetValueOrDefault(many, many), schema.Type);
            }
            _targets = [];
            foreach (var (chain, declared) in targets)
            {
                // A reference into the chain itself orders nothing between chains.
                var outside = declared.Where(d => ChainOf(d) != chain).ToArray();
                if (outside.Length > 0)
                    _targets[chain] = outside;
            }
        }

        /// <summary>True when some chain's rows point at another chain's.</summary>
        public bool HasReferences => _targets.Count > 0;

        /// <summary>The root type of the chain <paramref name="type"/> is written in.</summary>
        public Type ChainOf(Type type) => _chainOf.GetValueOrDefault(type, type);

        /// <summary>
        /// True when rows of <paramref name="chain"/>'s types can hold the ID of a row of
        /// <paramref name="other"/>'s types: through a reference declared as one of them, or as a
        /// type <paramref name="other"/> derives from.
        /// </summary>
        public bool PointsAt(Type chain, Type other)
        {
            if (chain == other || !_targets.TryGetValue(chain, out var declared))
                return false;
            foreach (var target in declared)
            {
                var targetChain = ChainOf(target);
                if (targetChain == other || (targetChain == target && target.IsAssignableFrom(other)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="items"/> in the order a save writes them: each chain after the chains
        /// it points at, and otherwise in the order given. Returns the list itself when nothing
        /// needs to move.
        /// </summary>
        public IReadOnlyList<T> ForWrites<T>(IReadOnlyList<T> items, Func<T, Type> chainOf) =>
            Order(items, chainOf, referencedFirst: true);

        /// <summary>
        /// <paramref name="items"/> in the order a delete removes them: each chain before the
        /// chains it points at, and otherwise in the order given. Returns the list itself when
        /// nothing needs to move.
        /// </summary>
        public IReadOnlyList<T> ForDeletes<T>(IReadOnlyList<T> items, Func<T, Type> chainOf) =>
            Order(items, chainOf, referencedFirst: false);

        /// <summary>
        /// A stable topological order: at each step, the first item left that nothing left must
        /// precede, or the first item left when the rest form a cycle.
        /// </summary>
        private IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, Func<T, Type> chainOf, bool referencedFirst)
        {
            if (items.Count < 2 || !HasReferences)
                return items;
            var chains = new Type[items.Count];
            for (var i = 0; i < chains.Length; i++)
                chains[i] = ChainOf(chainOf(items[i]));
            if (!AnyMustMove(chains, referencedFirst))
                return items;
            var placed = new bool[chains.Length];
            var ordered = new List<T>(chains.Length);
            while (ordered.Count < chains.Length)
            {
                var next = -1;
                for (var i = 0; i < chains.Length && next < 0; i++)
                    if (!placed[i] && !WaitsForAnother(i, chains, placed, referencedFirst))
                        next = i;
                if (next < 0)
                    next = Array.IndexOf(placed, false);
                placed[next] = true;
                ordered.Add(items[next]);
            }
            return ordered;
        }

        /// <summary>True when an item comes before one it must follow.</summary>
        private bool AnyMustMove(Type[] chains, bool referencedFirst)
        {
            for (var i = 0; i < chains.Length; i++)
                for (var j = i + 1; j < chains.Length; j++)
                    if (MustPrecede(chains[j], chains[i], referencedFirst))
                        return true;
            return false;
        }

        private bool WaitsForAnother(int item, Type[] chains, bool[] placed, bool referencedFirst)
        {
            for (var k = 0; k < chains.Length; k++)
                if (k != item && !placed[k] && MustPrecede(chains[k], chains[item], referencedFirst))
                    return true;
            return false;
        }

        // A save writes the chain pointed at first; a delete removes the chain that points first.
        private bool MustPrecede(Type first, Type second, bool referencedFirst) =>
            referencedFirst ? PointsAt(second, first) : PointsAt(first, second);

        private static void AddWithChildren(Dictionary<Type, IObjectSchema> all, IObjectSchema schema)
        {
            if (!schema.IsDatabaseEnabled || !all.TryAdd(schema.Type, schema) || !schema.HasNucleusChildCollections)
                return;
            foreach (var collection in schema.NucleusChildCollectionProperties ?? [])
                if (collection.ChildSchema is { } child)
                    AddWithChildren(all, child);
        }

        private static void Add(Dictionary<Type, HashSet<Type>> targets, Type chain, Type target)
        {
            if (!targets.TryGetValue(chain, out var set))
                targets[chain] = set = [];
            set.Add(target);
        }

        private static Type ChainRoot(Type type, Dictionary<Type, IObjectSchema> all)
        {
            var seen = new HashSet<Type>();
            var current = type;
            while (all.TryGetValue(current, out var schema) && schema.ParentObjectProperty?.Type is { } parent && seen.Add(current))
                current = parent;
            return current;
        }
    }
}
