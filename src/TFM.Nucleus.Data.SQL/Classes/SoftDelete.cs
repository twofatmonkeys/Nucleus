namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The one definition of how a [Deletable(DeleteBehavior.Soft)] table is deleted and read:
    /// a delete flags [IsDeleted] and stamps [DeletedAt], and a read of live rows filters on the
    /// flag. Every query that deletes or selects goes through here, so the rule has one home.
    /// </summary>
    internal static class SoftDelete
    {
        /// <summary>The condition that selects rows which have not been soft deleted.</summary>
        public const string LiveRowFilter = "[IsDeleted] = 0";

        public static bool Applies(SqlTableSchema tableSchema) =>
            tableSchema.ObjectSchema is { IsDeletable: true, DeleteBehavior: DeleteBehavior.Soft };

        /// <summary>The SET terms of a soft delete; a fresh pair per statement.</summary>
        public static Parameter[] NewSetParameters() =>
        [
            new Parameter("IsDeleted", true),
            new Parameter("DeletedAt", ServerFunction.SysDateTimeOffset),
        ];
    }
}
