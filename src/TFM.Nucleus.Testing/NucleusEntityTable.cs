using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Testing;

/// <summary>
/// The table Nucleus gives an entity and the old names it adopts, computed from the entity's
/// attributes without a database. A store's tests pin these, because a table that changes name
/// without its old name listed is a new, empty table on the next start.
/// </summary>
/// <param name="Entity">The entity type.</param>
/// <param name="TableName">The table the entity's rows live in.</param>
/// <param name="LegacyTableNames">The names from the entity's <c>[LegacyTableNames]</c>, in declaration order.</param>
public sealed record NucleusEntityTable(Type Entity, string TableName, IReadOnlyList<string> LegacyTableNames)
{
    /// <summary>
    /// The names for <paramref name="entity"/> under a provider configured with
    /// <paramref name="providerPolicy"/> (full name, the default, when null). An entity's own
    /// <c>[DatabaseTable]</c> or <c>[TableNaming]</c> wins over the provider's policy.
    /// </summary>
    public static NucleusEntityTable For(Type entity, TableNamingPolicy? providerPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var schema = new TableSchema(ObjectSchema.Get(entity), providerPolicy ?? TableNamingPolicy.FullName);
        return new NucleusEntityTable(entity, schema.TableName, schema.LegacyTableNames);
    }
}
