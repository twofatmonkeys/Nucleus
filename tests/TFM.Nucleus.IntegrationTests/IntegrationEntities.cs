namespace TFM.Nucleus.IntegrationTests
{
    public class IntWidget : CoreObject<IntWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }

    [Deletable]
    public class IntHardItem : CoreObject<IntHardItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable(DeleteBehavior.Soft)]
    public class IntSoftItem : CoreObject<IntSoftItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Columns named apart from their properties, with a unique one and a child collection.</summary>
    [Deletable]
    public class IntRenamedItem : CoreObject<IntRenamedItem>
    {
        [DatabaseColumn(ColumnName = "item_title", Indexed = true)]
        public string? Title { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn("item_code", Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "item_count")]
        public int Count { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<IntRenamedNote, IntRenamedItem> Notes => GetChildCollection<IntRenamedNote, IntRenamedItem>();
    }

    [Deletable]
    public class IntRenamedNote : CoreChildObject<IntRenamedNote, IntRenamedItem>
    {
        [DatabaseColumn(ColumnName = "note_text")]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Renamed columns without a child collection, for the bulk engine.</summary>
    [Deletable]
    public class IntRenamedRow : CoreObject<IntRenamedRow>
    {
        [DatabaseColumn(ColumnName = "row_title")]
        public string? Title { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "row_code")]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ColumnName = "row_count")]
        public int Count { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A column the server stamps on every write and the entity reads back, with a child collection.</summary>
    [Deletable]
    public class IntStampedItem : CoreObject<IntStampedItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();

        public NucleusChildCollection<IntStampedLine, IntStampedItem> Lines => GetChildCollection<IntStampedLine, IntStampedItem>();
    }

    [Deletable]
    public class IntStampedLine : CoreChildObject<IntStampedLine, IntStampedItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }

    /// <summary>A stamped row without children, for the split and bulk write paths.</summary>
    [Deletable]
    public class IntStampedRow : CoreObject<IntStampedRow>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }

    /// <summary>A stamped row in a table of its own, which a test puts a trigger on.</summary>
    [Deletable]
    public class IntTriggeredRow : CoreObject<IntTriggeredRow>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset, ReturnOutputToProperty = true)]
        public DateTimeOffset StampedAt => GetValue<DateTimeOffset>();
    }

    /// <summary>The save benchmarks' rows, in a table of their own.</summary>
    [Deletable]
    public class IntBenchWidget : CoreObject<IntBenchWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>Columns named with SQL Server keywords: created bracketed, so a rebuild must copy them bracketed too.</summary>
    public class IntKeywordItem : CoreObject<IntKeywordItem>
    {
        [DatabaseColumn(ColumnDataType.Unicode)]
        public string? By { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Order { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn(ColumnDataType.Unicode)]
        public string? Note { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// A table that gains columns: the add-column suite drops Key (indexed, and named with a
    /// keyword) and Extra from a populated table, then starts the stack again.
    /// </summary>
    public class IntGrowingItem : CoreObject<IntGrowingItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public string? Key { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int? Extra { get => GetValue<int?>(); set => SetValue(value); }
    }

    /// <summary>A soft-deletable table that loses its soft-delete columns in the add-column suite.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntGrowingSoftItem : CoreObject<IntGrowingSoftItem>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>The table the failure suite locks by hand, so that holding its lock stalls no other suite.</summary>
    public class IntFailureProbe : CoreObject<IntFailureProbe>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class IntUniqueItem : CoreObject<IntUniqueItem>
    {
        [DatabaseColumn(Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// A unique rule over two columns, the shape a template's files take: one path per template.
    /// Its table is changed under the schema lock by the [TableIndex] suite.
    /// </summary>
    [Deletable(DeleteBehavior.Hard)]
    [TableIndex("KeyPath", nameof(TemplateId), nameof(FilePath), Unique = true)]
    public class IntPathItem : CoreObject<IntPathItem>
    {
        [DatabaseColumn]
        public Guid? TemplateId { get => GetValue<Guid?>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? FilePath { get => GetValue<string>(); set => SetValue(value); }
    }

    public class IntKindWidget : CoreObject<IntKindWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind Kind { get => GetValue<IntWidgetKind>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind? OptionalKind { get => GetValue<IntWidgetKind?>(); set => SetValue(value); }
    }

    public enum IntWidgetKind
    {
        Unknown = 0,
        Standard = 1,
        Premium = 2,
    }

    /// <summary>
    /// Rows for the row read suite: a group key of each kind (text, enum, nullable enum, Guid),
    /// a date for the minimum and maximum, a number for the total and a long text column that a
    /// row read can leave unread. Soft deleted, so the live-row filter is covered too.
    /// </summary>
    [Deletable(DeleteBehavior.Soft)]
    [TableIndex("BatchAt", nameof(Batch), nameof(At), Include = [nameof(Name), nameof(Amount), nameof(Notes)])]
    public class IntRowItem : CoreObject<IntRowItem>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Batch { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind Kind { get => GetValue<IntWidgetKind>(); set => SetValue(value); }

        [DatabaseColumn]
        public IntWidgetKind? OptionalKind { get => GetValue<IntWidgetKind?>(); set => SetValue(value); }

        [DatabaseColumn]
        public Guid Session { get => GetValue<Guid>(); set => SetValue(value); }

        [DatabaseColumn]
        public DateTimeOffset At { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Amount { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn(ColumnDataType.Unicode, -1)]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>A parent whose child rows carry the content, like a bundle and its files.</summary>
    [Deletable]
    public class IntBundle : CoreObject<IntBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntBundleFile, IntBundle> Files => GetChildCollection<IntBundleFile, IntBundle>();
    }

    [Deletable]
    public class IntBundleFile : CoreChildObject<IntBundleFile, IntBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Content { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A soft-deleted parent over hard-deleted children.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntSoftBundle : CoreObject<IntSoftBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntSoftBundleNote, IntSoftBundle> Notes => GetChildCollection<IntSoftBundleNote, IntSoftBundle>();
    }

    [Deletable]
    public class IntSoftBundleNote : CoreChildObject<IntSoftBundleNote, IntSoftBundle>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// The root of the table naming suite's entities. A stack rooted here stores only them, so a
    /// naming policy set on it renames no other suite's table.
    /// </summary>
    public abstract class IntNamingRoot<T> : CoreObject<T> where T : CoreObject<T> { }

    /// <summary>
    /// A table the naming suite adopts from its full-name form under ObjectName(1). The long class
    /// name makes the full-name form's index names longer than PostgreSQL's 63 bytes, so the
    /// rename starts from shortened names.
    /// </summary>
    public class IntAdoptedItemWithALongName : IntNamingRoot<IntAdoptedItemWithALongName>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int? Quantity { get => GetValue<int?>(); set => SetValue(value); }
    }

    /// <summary>A pinned table with two old names, which the naming suite creates by hand.</summary>
    [DatabaseTable(Table)]
    [LegacyTableNames(OldTable, OlderTable)]
    public class IntLegacyItem : IntNamingRoot<IntLegacyItem>
    {
        public const string Table = "tbl_IntLegacyItem";
        public const string OldTable = "tbl_IntLegacyItemOld";
        public const string OlderTable = "tbl_IntLegacyItemOlder";

        [DatabaseColumn(Indexed = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int? Quantity { get => GetValue<int?>(); set => SetValue(value); }
    }

    // ---- The child model: declared deletes, removals, validation, forceSave, lazy collections ----

    /// <summary>
    /// Owns lines that delete through it ([Deletable] on the collection, not on the line), and
    /// each line owns breaks with no declaration at all, which go with their line.
    /// </summary>
    [Deletable]
    public class IntRoster : CoreObject<IntRoster>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [Deletable]
        public NucleusChildCollection<IntRosterLine, IntRoster> Lines => GetChildCollection<IntRosterLine, IntRoster>();
    }

    public class IntRosterLine : CoreChildObject<IntRosterLine, IntRoster>
    {
        /// <summary>Unique, so a replacement with the same code shows the removal's delete runs first.</summary>
        [DatabaseColumn(Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Hours { get => GetValue<int>(); set => SetValue(value); }

        public NucleusChildCollection<IntRosterBreak, IntRosterLine> Breaks => GetChildCollection<IntRosterBreak, IntRosterLine>();

        public override void Validate()
        {
            if (Hours < 0)
                throw new InvalidOperationException("A roster line cannot have negative hours.");
            base.Validate();
        }
    }

    public class IntRosterBreak : CoreChildObject<IntRosterBreak, IntRosterLine>
    {
        [DatabaseColumn]
        public int Minutes { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A soft owner whose undeclared lines are deleted soft with it, and when removed.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntSoftRoster : CoreObject<IntSoftRoster>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntSoftRosterLine, IntSoftRoster> Lines => GetChildCollection<IntSoftRosterLine, IntSoftRoster>();
    }

    public class IntSoftRosterLine : CoreChildObject<IntSoftRosterLine, IntSoftRoster>
    {
        [DatabaseColumn]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable]
    public class IntLinkTarget : CoreObject<IntLinkTarget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Cascades its save into the target it links to.</summary>
    [Deletable]
    public class IntLinkSource : CoreObject<IntLinkSource>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject(forceSave: true)]
        [DatabaseColumn]
        public IntLinkTarget? Target { get => GetValue<IntLinkTarget>(); set => SetValue(value); }

        /// <summary>A reference with no foreign key: its target can be deleted while it points at it.</summary>
        [LinkedObject]
        [DatabaseColumn]
        [ForeignKey(false)]
        public IntLinkTarget? Loose { get => GetValue<IntLinkTarget>(); set => SetValue(value); }
    }

    /// <summary>The one of two linked collections: its vouchers are roots of their own.</summary>
    [Deletable]
    public class IntCampaign : CoreObject<IntCampaign>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(IntVoucher.CampaignId))]
        public NucleusLinkedCollection<IntVoucher, IntCampaign> Vouchers => GetLinkedCollection<IntVoucher, IntCampaign>();

        [LinkedBy(nameof(IntVoucher.ReferredBy))]
        public NucleusLinkedCollection<IntVoucher, IntCampaign> Referrals => GetLinkedCollection<IntVoucher, IntCampaign>();
    }

    /// <summary>
    /// A member of a campaign's two collections: by a Guid key and by a [LinkedObject] key.
    /// Unique by code, so a save can be made to fail in the database; "invalid" fails its rule.
    /// </summary>
    [Deletable]
    public class IntVoucher : CoreObject<IntVoucher>
    {
        public const string InvalidCode = "invalid";

        [DatabaseColumn(Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public Guid CampaignId { get => GetValue<Guid>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn(Indexed = true)]
        public IntCampaign? ReferredBy { get => GetValue<IntCampaign>(); set => SetValue(value); }

        public override void Validate()
        {
            base.Validate();
            if (Code == InvalidCode)
                throw new InvalidOperationException("A voucher needs a real code.");
        }
    }

    /// <summary>Refers to a roster, a type with a child collection of its own, without owning it.</summary>
    [Deletable]
    public class IntRosterNote : CoreObject<IntRosterNote>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public IntRoster? Roster { get => GetValue<IntRoster>(); set => SetValue(value); }
    }

    /// <summary>Refers to a roster line, a child type, without owning it.</summary>
    [Deletable]
    public class IntLineNote : CoreObject<IntLineNote>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public IntRosterLine? Line { get => GetValue<IntRosterLine>(); set => SetValue(value); }
    }

    /// <summary>A lazy collection: left out of every load, read on first access or included.</summary>
    [Deletable]
    public class IntLazyOwner : CoreObject<IntLazyOwner>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [RetrieveOnDemand]
        public NucleusChildCollection<IntLazyItem, IntLazyOwner> Items => GetChildCollection<IntLazyItem, IntLazyOwner>();
    }

    public class IntLazyItem : CoreChildObject<IntLazyItem, IntLazyOwner>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>Records its load hook, to prove every load path runs it once the object is finished.</summary>
    [Deletable]
    public class IntHookOwner : CoreObject<IntHookOwner>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<IntHookLine, IntHookOwner> Lines => GetChildCollection<IntHookLine, IntHookOwner>();

        [RetrieveOnDemand]
        public NucleusChildCollection<IntHookNote, IntHookOwner> Notes => GetChildCollection<IntHookNote, IntHookOwner>();

        public int HookRuns;
        public bool LinesWereLoaded;
        public int FinishedLinesSeen;

        protected override void OnAfterLoadFromDB()
        {
            HookRuns++;
            LinesWereLoaded = Lines.IsLoaded;
            if (LinesWereLoaded)
                FinishedLinesSeen = Lines.Count(l => l.HookRuns == 1 && l.SawParent);
        }
    }

    public class IntHookLine : CoreChildObject<IntHookLine, IntHookOwner>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        public int HookRuns;
        public bool SawParent;

        protected override void OnAfterLoadFromDB()
        {
            HookRuns++;
            SawParent = Parent != null;
        }
    }

    public class IntHookNote : CoreChildObject<IntHookNote, IntHookOwner>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }

        public int HookRuns;
        public bool SawParent;

        protected override void OnAfterLoadFromDB()
        {
            HookRuns++;
            SawParent = Parent != null;
        }
    }

    // ---- Foreign keys: ForeignKeyIntegrationTests' own tables -----------------------------------

    [Deletable]
    public class IntKeyKeeper : CoreObject<IntKeyKeeper>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Points at a keeper through a key; not forceSave, so a new keeper is not written with it.</summary>
    [Deletable]
    public class IntKeyResident : CoreObject<IntKeyResident>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public IntKeyKeeper? Keeper { get => GetValue<IntKeyKeeper>(); set => SetValue(value); }
    }

    /// <summary>Soft deleted: a hard delete of its keeper clears its reference first.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class IntKeySoftReport : CoreObject<IntKeySoftReport>
    {
        [DatabaseColumn]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public IntKeyKeeper? Keeper { get => GetValue<IntKeyKeeper>(); set => SetValue(value); }
    }

    /// <summary>Rows of one table that point at each other.</summary>
    [Deletable]
    public class IntKeyMentor : CoreObject<IntKeyMentor>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public IntKeyMentor? Mentor { get => GetValue<IntKeyMentor>(); set => SetValue(value); }
    }
}
