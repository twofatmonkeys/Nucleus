using System;

namespace TFM.Nucleus.Data
{
    /// <summary>How a table name is made from an entity type's name.</summary>
    public enum TableNamingStyle
    {
        /// <summary>tbl_ and the type's full name, namespace included: Shop.Billing.Invoice is tbl_Shop_Billing_Invoice.</summary>
        FullName = 0,

        /// <summary>
        /// tbl_ and the last levels of the type's full name: with 1 level tbl_Invoice, with 2
        /// tbl_Billing_Invoice. A nested type's outer type counts as a level.
        /// </summary>
        ObjectName = 1,
    }

    /// <summary>
    /// The naming policy of an entity's table, in place of the one its provider is configured
    /// with. A <see cref="DatabaseTableAttribute"/> name still wins over both.
    /// </summary>
    /// <remarks>
    /// Inherited, unlike <see cref="DatabaseTableAttribute"/>: a policy still gives each derived
    /// type a name of its own, so a policy on a root type applies to every entity under it. A
    /// derived type's own attribute wins over its base type's. Two types the policy gives the
    /// same name fail at schema build.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class TableNamingAttribute : Attribute
    {
        /// <param name="style">How the table name is made.</param>
        /// <param name="levels">For <see cref="TableNamingStyle.ObjectName"/>, how many trailing parts of the full name to keep, at least 1. Ignored for <see cref="TableNamingStyle.FullName"/>.</param>
        public TableNamingAttribute(TableNamingStyle style, int levels = 1)
        {
            Style = style;
            Levels = levels;
        }

        /// <summary>How the table name is made.</summary>
        public TableNamingStyle Style { get; }

        /// <summary>For <see cref="TableNamingStyle.ObjectName"/>, how many trailing parts of the full name to keep.</summary>
        public int Levels { get; }
    }
}
