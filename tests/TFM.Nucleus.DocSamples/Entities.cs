// The entities of the Portly Primate Sanctuary, the sample domain of docs\sample-domain.md.
// Each #region is copied into the guide by name; DocSampleTests fails when a copy differs.

namespace PortlyPrimate
{
    #region entity-monkey
    public enum Mood { Content, Smug, Plotting, Hangry, Asleep }

    [Deletable]                                            // deleting is opt-in
    public class Monkey : CoreObject<Monkey>
    {
        [DatabaseColumn(Unique = true)]                    // no two monkeys share a name
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Scale = 1)]
        public decimal WeightKg { get => GetValue<decimal>(); set => SetValue(value); }

        [DatabaseColumn]                                   // an enum is stored as its integer
        public Mood Mood { get => GetValue<Mood>(); set => SetValue(value); }
    }
    #endregion

    #region entity-order
    [Deletable]
    public class BananaOrder : CoreObject<BananaOrder>
    {
        [DatabaseColumn(Indexed = true)]
        public DateTimeOffset PlacedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn]
        public OrderStatus Status { get => GetValue<OrderStatus>(); set => SetValue(value); }

        // The order owns its lines: they are saved, loaded and deleted with it.
        public NucleusChildCollection<BananaOrderLine, BananaOrder> Lines => GetChildCollection<BananaOrderLine, BananaOrder>();
    }

    public class BananaOrderLine : CoreChildObject<BananaOrderLine, BananaOrder>
    {
        [DatabaseColumn]
        public string? Variety { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        // A grandchild collection: owned rows two levels down.
        public NucleusChildCollection<LineSubstitution, BananaOrderLine> Substitutions => GetChildCollection<LineSubstitution, BananaOrderLine>();

        public override void Validate()
        {
            if (Quantity <= 0)
                throw new InvalidOperationException("An order line needs at least one banana.");
            base.Validate();                               // marks the line valid; always call it
        }
    }

    public class LineSubstitution : CoreChildObject<LineSubstitution, BananaOrderLine>
    {
        [DatabaseColumn]
        public string? Variety { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Reason { get => GetValue<string>(); set => SetValue(value); }
    }

    public enum OrderStatus { Placed, Ripening, Delivered, Eaten }
    #endregion

    #region entity-keeper
    [Deletable]
    public class Keeper : CoreObject<Keeper>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Unique = true)]
        public string? Email { get => GetValue<string>(); set => SetValue(value); }
    }
    #endregion

    #region entity-resident
    // A monkey in residence: it has a keeper, belongs to a troop and logs its naps.
    [Deletable]
    public class Resident : CoreObject<Resident>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        // A reference to another root. It is stored as the keeper's ID; [DatabaseColumn] gives it a column.
        [LinkedObject]
        [DatabaseColumn]
        public Keeper? Keeper { get => GetValue<Keeper>(); set => SetValue(value); }

        // The key a troop's linked collection is kept by. It must be indexed.
        [DatabaseColumn(Indexed = true)]
        public Guid TroopId { get => GetValue<Guid>(); set => SetValue(value); }

        // Can grow large, so it is lazy: left out of loads and read on first use.
        // [Deletable] here lets a nap removed from the collection be deleted by the save.
        [RetrieveOnDemand]
        [Deletable]
        public NucleusChildCollection<NapLog, Resident> Naps => GetChildCollection<NapLog, Resident>();
    }

    public class NapLog : CoreChildObject<NapLog, Resident>
    {
        [DatabaseColumn(Indexed = true)]
        public DateTimeOffset StartedUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Minutes { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Location { get => GetValue<string>(); set => SetValue(value); }
    }
    #endregion

    #region entity-troop
    // Residents are roots of their own, loaded and saved without their troop, so the troop holds
    // them in a linked collection, kept by the key the resident declares.
    [Deletable]
    public class Troop : CoreObject<Troop>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Motto { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(Resident.TroopId))]
        public NucleusLinkedCollection<Resident, Troop> Residents => GetLinkedCollection<Resident, Troop>();
    }
    #endregion

    #region entity-variety
    // A small lookup table, read often and changed rarely: held whole in memory, refreshed after
    // 120 seconds in the background and dropped after 900.
    [Cacheable(refreshInterval: 120, cacheLifetime: 900)]
    public class BananaVariety : CoreObject<BananaVariety>
    {
        [DatabaseColumn(Unique = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Curvature { get => GetValue<int>(); set => SetValue(value); }
    }
    #endregion

    #region entity-mischief
    public enum Severity { Cheeky, Brazen, Felonious }

    // Soft delete: a deleted report keeps its row, flagged, and ordinary reads no longer see it.
    [Deletable(DeleteBehavior.Soft)]
    public class MischiefReport : CoreObject<MischiefReport>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Culprit { get => GetValue<string>(); set => SetValue(value); }

        // Contains() on this column searches the full-text index where the engine has one.
        [DatabaseColumn(FullTextSearch = true)]
        public string? Description { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public Severity Severity { get => GetValue<Severity>(); set => SetValue(value); }

        // A keeper's ID, not a [LinkedObject]: [ForeignKey] gives it the key a reference would get.
        [DatabaseColumn]
        [ForeignKey(typeof(Keeper))]
        public Guid? ReportedBy { get => GetValue<Guid?>(); set => SetValue(value); }

        // Stamped by the database server on every insert and update, whatever the client sets.
        [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset)]
        public DateTimeOffset RecordedAt { get => GetValue<DateTimeOffset>(); set => SetValue(value); }
    }
    #endregion

    #region entity-weighin
    [Deletable]
    public class WeighIn : CoreObject<WeighIn>
    {
        [DatabaseColumn(Indexed = true)]
        public string? MonkeyName { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Scale = 1)]
        public decimal WeightKg { get => GetValue<decimal>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }

        /// <summary>Worked out when the row is loaded; not stored.</summary>
        public bool IsHeavy { get; private set; }

        // Runs after validation and before anything is written. What it sets is saved.
        public override void PreSave()
        {
            Notes = Notes?.Trim();
            base.PreSave();
        }

        // Runs once per loaded object, after its columns and children are in place.
        protected override void OnAfterLoadFromDB()
        {
            IsHeavy = WeightKg > 20;
            base.OnAfterLoadFromDB();
        }
    }
    #endregion

    #region entity-naming
    // Every entity under this root is named by its class alone: tbl_GiftShopVisit, not
    // tbl_PortlyPrimate_GiftShopVisit.
    [TableNaming(TableNamingStyle.ObjectName, 1)]
    public abstract class ShopRoot<T> : CoreObject<T> where T : CoreObject<T> { }

    public enum VisitKind { Browse, Purchase, Refund }

    [DatabaseTable("tbl_ShopVisits")]                      // this name, whatever the policy
    [LegacyTableNames("tbl_GiftShopVisit")]                // and the name it had before: adopted, not recreated
    [TableIndex("KindTime", nameof(Kind), nameof(OccurredUtc), Include = [nameof(Path)])]
    [TableIndex("SessionPath", nameof(SessionId), nameof(Path), Unique = true)]
    public class GiftShopVisit : ShopRoot<GiftShopVisit>
    {
        [DatabaseColumn]
        public Guid SessionId { get => GetValue<Guid>(); set => SetValue(value); }

        [DatabaseColumn]
        public DateTimeOffset OccurredUtc { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn(ColumnDataType.Unicode, 200)]
        public string? Path { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public VisitKind Kind { get => GetValue<VisitKind>(); set => SetValue(value); }
    }
    #endregion
}
