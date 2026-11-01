namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// The one definition of what a [Deletable(DeleteBehavior.Soft)] table is: a delete flags
    /// the <see cref="IsDeletedColumn"/> column and stamps <see cref="DeletedAtColumn"/>, and a
    /// read of live rows filters on the flag. Providers render the filter in their own dialect.
    /// </summary>
    public static class SoftDelete
    {
        /// <summary>The system column flagging a soft-deleted row. No entity property backs it.</summary>
        public const string IsDeletedColumn = "IsDeleted";

        /// <summary>The system column recording when a row was soft deleted.</summary>
        public const string DeletedAtColumn = "DeletedAt";

        public static bool Applies(IDataTableSchema tableSchema) =>
            tableSchema.ObjectSchema is { IsDeletable: true, DeleteBehavior: DeleteBehavior.Soft };

        /// <summary>The SET terms of a soft delete; a fresh pair per statement.</summary>
        public static Parameter[] NewSetParameters() =>
        [
            new Parameter(IsDeletedColumn, true),
            new Parameter(DeletedAtColumn, ServerFunction.CurrentDateTimeOffset),
        ];
    }
}
