using TFM.Nucleus.Abstractions.Interfaces;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    // ---- Entities ------------------------------------------------------------------------------

    /// <summary>A one with two linked collections over one many type, through different keys.</summary>
    public class LcCampaign : CoreObject<LcCampaign>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(LcVoucher.CampaignId))]
        public NucleusLinkedCollection<LcVoucher, LcCampaign> Vouchers => GetLinkedCollection<LcVoucher, LcCampaign>();

        [LinkedBy(nameof(LcVoucher.ReferredBy))]
        public NucleusLinkedCollection<LcVoucher, LcCampaign> Referrals => GetLinkedCollection<LcVoucher, LcCampaign>();

        [LinkedBy(nameof(LcDeletableVoucher.CampaignId))]
        public NucleusLinkedCollection<LcDeletableVoucher, LcCampaign> Passes => GetLinkedCollection<LcDeletableVoucher, LcCampaign>();
    }

    /// <summary>A many type that cannot be deleted, keyed by a Guid and by a [LinkedObject].</summary>
    public class LcVoucher : CoreObject<LcVoucher>
    {
        [DatabaseColumn] public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)] public Guid CampaignId { get => GetValue<Guid>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn(Indexed = true)]
        public LcCampaign? ReferredBy { get => GetValue<LcCampaign>(); set => SetValue(value); }
    }

    [Deletable]
    public class LcDeletableVoucher : CoreObject<LcDeletableVoucher>
    {
        [DatabaseColumn] public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)] public Guid? CampaignId { get => GetValue<Guid?>(); set => SetValue(value); }
    }

    /// <summary>A hard-deleted one whose members are soft deleted.</summary>
    [Deletable]
    public class LcHardCampaign : CoreObject<LcHardCampaign>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(LcSoftVoucher.HardCampaignId))]
        public NucleusLinkedCollection<LcSoftVoucher, LcHardCampaign> Vouchers => GetLinkedCollection<LcSoftVoucher, LcHardCampaign>();
    }

    [Deletable(DeleteBehavior.Soft)]
    public class LcSoftVoucher : CoreObject<LcSoftVoucher>
    {
        [DatabaseColumn(Indexed = true)] public Guid? HardCampaignId { get => GetValue<Guid?>(); set => SetValue(value); }
    }

    // ---- Declarations refused at schema build ----------------------------------------------------

    public class LcNoLinkedBy : CoreObject<LcNoLinkedBy>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusLinkedCollection<LcVoucher, LcNoLinkedBy> Vouchers => GetLinkedCollection<LcVoucher, LcNoLinkedBy>();
    }

    public class LcLinkedByOnAColumn : CoreObject<LcLinkedByOnAColumn>
    {
        [LinkedBy(nameof(LcVoucher.CampaignId))]
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class LcSharedKey : CoreObject<LcSharedKey>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(LcLooseVoucher.OwnerId))]
        public NucleusLinkedCollection<LcLooseVoucher, LcSharedKey> First => GetLinkedCollection<LcLooseVoucher, LcSharedKey>();

        [LinkedBy(nameof(LcLooseVoucher.OwnerId))]
        public NucleusLinkedCollection<LcLooseVoucher, LcSharedKey> Second => GetLinkedCollection<LcLooseVoucher, LcSharedKey>();
    }

    [Cacheable]
    public class LcCachedOne : CoreObject<LcCachedOne>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(LcLooseVoucher.OwnerId))]
        public NucleusLinkedCollection<LcLooseVoucher, LcCachedOne> Vouchers => GetLinkedCollection<LcLooseVoucher, LcCachedOne>();
    }

    public class LcChildOne : CoreChildObject<LcChildOne, LcCampaign>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(LcLooseVoucher.OwnerId))]
        public NucleusLinkedCollection<LcLooseVoucher, LcChildOne> Vouchers => GetLinkedCollection<LcLooseVoucher, LcChildOne>();
    }

    /// <summary>Keys that break the rules: the one types below each name one.</summary>
    public class LcLooseVoucher : CoreObject<LcLooseVoucher>
    {
        [DatabaseColumn(Indexed = true)] public Guid OwnerId { get => GetValue<Guid>(); set => SetValue(value); }
        [DatabaseColumn] public Guid UnindexedId { get => GetValue<Guid>(); set => SetValue(value); }
        [DatabaseColumn(Indexed = true)] public string? Text { get => GetValue<string>(); set => SetValue(value); }
        public Guid NotStored { get => GetValue<Guid>(); set => SetValue(value); }
        [DatabaseColumn(Indexed = true)] public LcBadKeys? Unmarked { get => GetValue<LcBadKeys>(); set => SetValue(value); }
    }

    public class LcBadKeys : CoreObject<LcBadKeys>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy("Missing")]
        public NucleusLinkedCollection<LcLooseVoucher, LcBadKeys> Missing => GetLinkedCollection<LcLooseVoucher, LcBadKeys>();

        [LinkedBy(nameof(LcLooseVoucher.UnindexedId))]
        public NucleusLinkedCollection<LcLooseVoucher, LcBadKeys> Unindexed => GetLinkedCollection<LcLooseVoucher, LcBadKeys>();

        [LinkedBy(nameof(LcLooseVoucher.Text))]
        public NucleusLinkedCollection<LcLooseVoucher, LcBadKeys> WrongType => GetLinkedCollection<LcLooseVoucher, LcBadKeys>();

        [LinkedBy(nameof(LcLooseVoucher.NotStored))]
        public NucleusLinkedCollection<LcLooseVoucher, LcBadKeys> NotStored => GetLinkedCollection<LcLooseVoucher, LcBadKeys>();

        [LinkedBy(nameof(LcLooseVoucher.Unmarked))]
        public NucleusLinkedCollection<LcLooseVoucher, LcBadKeys> Unmarked => GetLinkedCollection<LcLooseVoucher, LcBadKeys>();
    }

    /// <summary>
    /// The linked collection model: what a declaration must be, how a collection loads, what
    /// adding and removing do, and how a save walks, writes and commits its members.
    /// </summary>
    public class LinkedCollectionTests
    {
        private sealed class CountingLoader : ILinkedObjectLoader
        {
            private readonly Func<INucleusLinkedCollection, IEnumerable<ICoreObject>> _members;
            private int _sync;
            private int _async;

            public CountingLoader(Func<INucleusLinkedCollection, IEnumerable<ICoreObject>> members) => _members = members;

            public int SyncLoads => Volatile.Read(ref _sync);
            public int AsyncLoads => Volatile.Read(ref _async);

            public ICoreObject? LoadLinkedObject(Type type, Guid id) => throw new InvalidOperationException("No linked object is read here.");

            public void LoadLinkedCollection(INucleusLinkedCollection collection)
            {
                Interlocked.Increment(ref _sync);
                Thread.Sleep(10);
                collection.AttachLoaded(_members(collection));
            }

            public Task LoadLinkedCollectionAsync(INucleusLinkedCollection collection, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _async);
                collection.AttachLoaded(_members(collection));
                return Task.CompletedTask;
            }
        }

        private static T Stored<T>(Dictionary<string, object?> values) where T : CoreObject
        {
            values.TryAdd("ID", Guid.NewGuid());
            return ObjectSchema<T>.BuildObjectViaReflection(values);
        }

        /// <summary>A campaign as a load leaves it: stored, bound to <paramref name="loader"/>.</summary>
        private static LcCampaign LoadedCampaign(ILinkedObjectLoader loader)
        {
            var campaign = Stored<LcCampaign>(new() { ["Name"] = "loaded" });
            ((ICoreObject)campaign).BindLinkedObjectLoader(loader);
            return campaign;
        }

        private static LcVoucher StoredVoucher(LcCampaign campaign, string code) =>
            Stored<LcVoucher>(new() { ["Code"] = code, ["CampaignId"] = campaign.ID, ["ReferredBy"] = campaign.ID });

        private static ILinkedObjectLoader Refusing() =>
            new CountingLoader(c => throw new InvalidOperationException($"{c.PropertyName} must not load here."));

        // ---- Declarations -------------------------------------------------------------------------

        [Fact]
        public void TheSchema_ListsEveryLinkedCollection_AndAllowsTwoOverOneManyTypeByDifferentKeys()
        {
            IObjectSchema schema = ObjectSchema<LcCampaign>.Get();

            Assert.True(schema.HasLinkedCollections);
            Assert.Equal(["Passes", "Referrals", "Vouchers"], schema.LinkedCollectionProperties.Select(p => p.Name).Order());
            var vouchers = schema.LinkedCollectionProperties.Single(p => p.Name == "Vouchers");
            Assert.Equal(typeof(LcVoucher), vouchers.LinkedType);
            Assert.Equal(nameof(LcVoucher.CampaignId), schema.GetLinkedCollectionKey(vouchers).Name);
            Assert.True(schema.GetLinkedCollectionKey(schema.LinkedCollectionProperties.Single(p => p.Name == "Referrals")).IsLinkedObject);
            Assert.False(vouchers.IsDBColumn);
            Assert.True(SaveGraph.CanReachDependents(new LcCampaign()));
        }

        [Theory]
        [InlineData(typeof(LcNoLinkedBy), "[LinkedBy")]
        [InlineData(typeof(LcLinkedByOnAColumn), "[LinkedBy] applies to a NucleusLinkedCollection")]
        [InlineData(typeof(LcSharedKey), "are both kept by LcLooseVoucher.OwnerId")]
        [InlineData(typeof(LcCachedOne), "[Cacheable]")]
        [InlineData(typeof(LcChildOne), "is a child type")]
        public void ADeclarationThatBreaksATypeRule_FailsTheSchema(Type type, string message)
        {
            var ex = Assert.Throws<ObjectSchemaException>(() => ObjectSchema.Get(type));
            Assert.Contains(message, ex.ToString());
        }

        [Theory]
        [InlineData("Missing", "does not have")]
        [InlineData("Unindexed", "not indexed")]
        [InlineData("WrongType", "a String")]
        [InlineData("NotStored", "not a stored column")]
        [InlineData("Unmarked", "not a [LinkedObject]")]
        public void AKeyThatBreaksTheRules_FailsWhenTheOnesTableIsChecked(string collection, string message)
        {
            IObjectSchema schema = ObjectSchema<LcBadKeys>.Get();
            var property = schema.LinkedCollectionProperties.Single(p => p.Name == collection);

            var ex = Assert.Throws<ObjectSchemaException>(() => schema.GetLinkedCollectionKey(property));
            Assert.Contains(message, ex.Message);
            Assert.Throws<ObjectSchemaException>(() => schema.Check());
        }

        // ---- Loading ------------------------------------------------------------------------------

        [Fact]
        public void ANewOne_StartsLoadedAndEmpty_WithoutALoad()
        {
            var campaign = new LcCampaign();
            ((ICoreObject)campaign).BindLinkedObjectLoader(Refusing());

            Assert.True(campaign.Vouchers.IsLoaded);
            Assert.Empty(campaign.Vouchers);
        }

        [Fact]
        public void ReadingTheProperty_LoadsNothing_AndTheFirstReadOfAMemberLoadsOnce()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A"), StoredVoucher(campaign, "B")]);
            campaign = LoadedCampaign(loader);

            var vouchers = campaign.Vouchers;
            Assert.False(vouchers.IsLoaded);
            Assert.Equal(0, loader.SyncLoads);

            Assert.Equal(2, vouchers.Count);
            Assert.Equal(["A", "B"], vouchers.Select(v => v.Code));
            Assert.Same(vouchers, campaign.Vouchers);
            Assert.Equal(1, loader.SyncLoads);
        }

        [Fact]
        public async Task LoadAsync_LoadsOnTheAsynchronousPath_AndTheReadAfterRunsNoLoad()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A")]);
            campaign = LoadedCampaign(loader);

            await campaign.Vouchers.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Single(campaign.Vouchers);
            Assert.Equal(1, loader.AsyncLoads);
            Assert.Equal(0, loader.SyncLoads);
        }

        [Fact]
        public async Task ReadsAtTheSameTime_LoadOnce()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A")]);
            campaign = LoadedCampaign(loader);
            var vouchers = campaign.Vouchers;

            var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => vouchers.Count)));

            Assert.All(counts, count => Assert.Equal(1, count));
            Assert.Equal(1, loader.SyncLoads);
        }

        [Fact]
        public void LoadedMembers_AreUnchanged_AndALinkedObjectKeyHoldsTheOne()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A")]);
            campaign = LoadedCampaign(loader);

            var referral = Assert.Single(campaign.Referrals);

            Assert.Same(campaign, referral.ReferredBy);
            Assert.Empty(((ICoreObject)referral).ChangedProperties);
        }

        [Fact]
        public void AMemberAddedBeforeTheLoad_IsKeptOnce_WhenTheLoadReturnsIt()
        {
            LcCampaign campaign = null!;
            LcVoucher stored = null!;
            var loader = new CountingLoader(_ => [stored, StoredVoucher(campaign, "B")]);
            campaign = LoadedCampaign(loader);
            stored = StoredVoucher(campaign, "A");

            campaign.Vouchers.Add(stored);
            Assert.Equal(0, loader.SyncLoads);

            Assert.Equal(["A", "B"], campaign.Vouchers.Select(v => v.Code));
        }

        // ---- Adding and removing ------------------------------------------------------------------

        [Fact]
        public void Add_PointsTheKeyAtTheOne_WithoutLoading()
        {
            var campaign = LoadedCampaign(Refusing());
            var voucher = new LcVoucher { Code = "A" };
            var referral = new LcVoucher { Code = "R" };

            campaign.Vouchers.Add(voucher);
            campaign.Referrals.Add(referral);

            Assert.Equal(campaign.ID, voucher.CampaignId);
            Assert.Same(campaign, referral.ReferredBy);
            Assert.False(campaign.Vouchers.IsLoaded);
        }

        [Fact]
        public void RemovingAStoredMemberOfATypeThatCannotBeDeleted_Throws_AndChangesNothing()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A")]);
            campaign = LoadedCampaign(loader);
            var voucher = campaign.Vouchers[0];

            var ex = Assert.Throws<InvalidOperationException>(() => campaign.Vouchers.Remove(voucher));
            Assert.Contains("[Deletable]", ex.Message);
            Assert.Throws<InvalidOperationException>(() => campaign.Vouchers.Clear());
            Assert.Single(campaign.Vouchers);
        }

        [Fact]
        public void RemovingANewMember_DropsIt_AndRemovingAStoredDeletableOne_DeletesItWithTheSave()
        {
            LcCampaign campaign = null!;
            LcDeletableVoucher stored = null!;
            var loader = new CountingLoader(_ => [stored]);
            campaign = LoadedCampaign(loader);
            stored = Stored<LcDeletableVoucher>(new() { ["Code"] = "S", ["CampaignId"] = campaign.ID });
            var added = new LcDeletableVoucher { Code = "N" };
            campaign.Passes.Add(added);

            Assert.True(campaign.Passes.Remove(added));
            Assert.True(campaign.Passes.Remove(stored));

            var graph = SaveGraph.Walk([campaign]);
            Assert.Same(stored, Assert.Single(graph.RemovedMembers));
            Assert.Empty(graph.Members);
        }

        // ---- Saving -------------------------------------------------------------------------------

        [Fact]
        public void TheSaveGraph_WritesNewAndChangedMembers_AndLeavesUnchangedOnesAlone()
        {
            LcCampaign campaign = null!;
            var loader = new CountingLoader(_ => [StoredVoucher(campaign, "A"), StoredVoucher(campaign, "B")]);
            campaign = LoadedCampaign(loader);
            var added = new LcVoucher { Code = "N" };
            campaign.Vouchers.Add(added);
            campaign.Vouchers.Single(v => v.Code == "B").Code = "B2";

            var graph = SaveGraph.Walk([campaign]);

            Assert.Equal(["B2", "N"], graph.Members.Cast<LcVoucher>().Select(v => v.Code).Order());
            Assert.Equal(["B2", "N"], graph.ChangedDependents.Cast<LcVoucher>().Select(v => v.Code).Order());
            Assert.Empty(graph.Links);
            Assert.Empty(graph.Children);
        }

        [Fact]
        public void TheSaveGraph_NeverLoadsACollection()
        {
            var campaign = LoadedCampaign(Refusing());
            _ = campaign.Vouchers;
            var added = new LcVoucher { Code = "N" };
            campaign.Vouchers.Add(added);

            Assert.Same(added, Assert.Single(SaveGraph.Walk([campaign]).Members));
        }

        [Fact]
        public void Commit_CommitsTheWrittenMembers_AndForgetsTheRemovals()
        {
            LcCampaign campaign = null!;
            LcDeletableVoucher stored = null!;
            var loader = new CountingLoader(_ => [stored]);
            campaign = LoadedCampaign(loader);
            stored = Stored<LcDeletableVoucher>(new() { ["Code"] = "S", ["CampaignId"] = campaign.ID });
            campaign.Passes.Remove(stored);
            var added = new LcDeletableVoucher { Code = "N" };
            campaign.Passes.Add(added);

            ((ICoreObject)campaign).CommitChanges();

            Assert.False(added.IsNew);
            Assert.Empty(((INucleusLinkedCollection)campaign.Passes).RemovedMembers);
            Assert.Empty(SaveGraph.Walk([campaign]).Members);
        }

        // ---- Include ------------------------------------------------------------------------------

        private static Func<Type, TableSchema?> Tables(params Type[] types)
        {
            var tables = types.ToDictionary(t => t, t => new TableSchema(ObjectSchema.Get(t)));
            return t => tables.GetValueOrDefault(t);
        }

        [Fact]
        public void IncludeLinkedCollection_AddsOneQueryMatchedOnTheKeyColumn()
        {
            var tables = Tables(typeof(LcCampaign), typeof(LcVoucher));
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(LcCampaign))!, tables);

            Assert.True(query.IncludeLinkedCollection(nameof(LcCampaign.Vouchers)));
            Assert.True(query.IncludeLinkedCollection(nameof(LcCampaign.Vouchers)));

            var members = Assert.Single(query.LinkedQueries);
            Assert.Equal(nameof(LcCampaign.Vouchers), members.LinkedCollectionProperty);
            Assert.Equal(nameof(LcVoucher.CampaignId), members.LinkedColumn);
            Assert.Null(members.ParentKeyColumn);
        }

        [Fact]
        public void IncludeLinkedCollection_IsFalseForAPropertyThatIsNotOne_AndThrowsForAManyTypeStoredElsewhere()
        {
            var tables = Tables(typeof(LcCampaign));
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(LcCampaign))!, tables);

            Assert.False(query.IncludeLinkedCollection(nameof(LcCampaign.Name)));
            var ex = Assert.Throws<InvalidQueryException>(() => query.IncludeLinkedCollection(nameof(LcCampaign.Vouchers)));
            Assert.Contains(nameof(LcVoucher), ex.Message);
        }

        [Fact]
        public void TheMembersCheck_CountsLiveMembersOnly()
        {
            // A soft-deleted member is not a member: it does not stop its one being deleted.
            var tables = Tables(typeof(LcHardCampaign), typeof(LcSoftVoucher));
            var members = RelationalDataProvider.MembersQuery(SqlServerQueryRenderer.Instance, tables(typeof(LcHardCampaign))!, _ => { },
                tables(typeof(LcSoftVoucher))!, nameof(LcSoftVoucher.HardCampaignId), tables);

            Assert.True(members.AppliesLiveRowFilter);
        }

        [Fact]
        public void ACollectionSelector_NamesTheProperty()
        {
            Assert.Equal(nameof(LcCampaign.Vouchers), ChildCollectionSelector.PropertyName<LcCampaign>(c => c.Vouchers));
        }
    }
}
