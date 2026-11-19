using System;

namespace TFM.Nucleus
{
    /// <summary>How a [Deletable] type's deletions are persisted.</summary>
    public enum DeleteBehavior
    {
        /// <summary>Rows are physically removed.</summary>
        Hard = 0,

        /// <summary>
        /// Rows are flagged (IsDeleted/DeletedAt system columns) and filtered out of reads;
        /// pass IncludeDeleted to see them.
        /// </summary>
        Soft = 1,
    }

    /// <summary>
    /// Opts rows into deletion. Nothing is deletable unless a declaration says so.
    /// <list type="bullet">
    /// <item>On an entity type, the type's own rows can be deleted directly (<c>Delete()</c>,
    /// <c>DeleteWhere</c>), and everything it owns through its child collections, at every depth,
    /// goes with it.</item>
    /// <item>On a <c>NucleusChildCollection</c> property, removing a stored child from the
    /// collection and saving the parent deletes the child's row, with everything it owns.</item>
    /// <item>On a child type, the same as on any type, and removals from its collection delete.</item>
    /// </list>
    /// A child type deletable only through its owner (declared on the owner or on the collection,
    /// not on itself) cannot be deleted on its own. <c>[Deletable(false)]</c> on a child type
    /// forbids deleting its rows at all; a declaration above it that would delete them is then
    /// an error when the schema is built, because the rows would be orphaned.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class DeletableAttribute : Attribute
    {
        /// <summary>False for <c>[Deletable(false)]</c>, which forbids deleting the type's rows.</summary>
        public bool Enabled { get; }

        /// <summary>Hard or soft. Hard unless declared.</summary>
        public DeleteBehavior Behavior { get; }

        /// <summary>
        /// True when the declaration names a behaviour. A collection property without one takes
        /// the behaviour of the declaration above it; a type without one is hard.
        /// </summary>
        public bool IsBehaviorDeclared { get; }

        /// <summary>Enables deletion, hard unless a declaration above says otherwise for a collection.</summary>
        public DeletableAttribute()
        {
            Enabled = true;
            Behavior = DeleteBehavior.Hard;
        }

        /// <summary>Enables deletion with the given behaviour.</summary>
        public DeletableAttribute(DeleteBehavior behavior)
        {
            Enabled = true;
            Behavior = behavior;
            IsBehaviorDeclared = true;
        }

        /// <summary><c>[Deletable(false)]</c> forbids deletion; <c>[Deletable(true)]</c> is the same as <c>[Deletable]</c>.</summary>
        public DeletableAttribute(bool enabled)
        {
            Enabled = enabled;
            Behavior = DeleteBehavior.Hard;
        }
    }
}
