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
    /// Opts an entity type into deletion. Types without this attribute cannot be deleted —
    /// any delete attempt throws.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class DeletableAttribute : Attribute
    {
        public DeleteBehavior Behavior { get; }

        public DeletableAttribute(DeleteBehavior behavior = DeleteBehavior.Hard)
        {
            Behavior = behavior;
        }
    }
}
