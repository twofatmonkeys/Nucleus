using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    // ---- Entities ------------------------------------------------------------------------------

    public class RoKeeper : CoreObject<RoKeeper>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class RoHammock : CoreObject<RoHammock>
    {
        [DatabaseColumn] public string? Location { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Points at a keeper itself, and at a hammock through its naps.</summary>
    public class RoResident : CoreObject<RoResident>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public RoKeeper? Keeper { get => GetValue<RoKeeper>(); set => SetValue(value); }

        public NucleusChildCollection<RoNap, RoResident> Naps => GetChildCollection<RoNap, RoResident>();
    }

    public class RoNap : CoreChildObject<RoNap, RoResident>
    {
        [DatabaseColumn] public int Minutes { get => GetValue<int>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public RoHammock? Hammock { get => GetValue<RoHammock>(); set => SetValue(value); }
    }

    /// <summary>The one of a linked collection: its members point at it by key.</summary>
    public class RoTroop : CoreObject<RoTroop>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(RoTroopMember.TroopId))]
        public NucleusLinkedCollection<RoTroopMember, RoTroop> Members => GetLinkedCollection<RoTroopMember, RoTroop>();
    }

    public class RoTroopMember : CoreObject<RoTroopMember>
    {
        [DatabaseColumn(Indexed = true)] public Guid TroopId { get => GetValue<Guid>(); set => SetValue(value); }
    }

    /// <summary>Two types that point at each other: no order serves both.</summary>
    public class RoRival : CoreObject<RoRival>
    {
        [LinkedObject]
        [DatabaseColumn]
        public RoOtherRival? Other { get => GetValue<RoOtherRival>(); set => SetValue(value); }
    }

    public class RoOtherRival : CoreObject<RoOtherRival>
    {
        [LinkedObject]
        [DatabaseColumn]
        public RoRival? Other { get => GetValue<RoRival>(); set => SetValue(value); }
    }

    /// <summary>A keeper's mentor is another keeper: rows of one table point at each other.</summary>
    public class RoMentoredKeeper : CoreObject<RoMentoredKeeper>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public RoMentoredKeeper? Mentor { get => GetValue<RoMentoredKeeper>(); set => SetValue(value); }
    }

    /// <summary>
    /// A save writes a type after the types its rows point at, and a delete removes a type before
    /// the types that point at it, so neither depends on the order the caller passed the objects
    /// in. Without this, a foreign key refuses the statement; without one, a reader in between
    /// sees a row that points at nothing.
    /// </summary>
    public class ReferenceOrderTests
    {
        private static ReferenceGraph Graph(params Type[] types) =>
            new(types.Select(ObjectSchema.Get));

        private static readonly ReferenceGraph Sanctuary = Graph(
            typeof(RoKeeper), typeof(RoHammock), typeof(RoResident), typeof(RoNap),
            typeof(RoTroop), typeof(RoTroopMember), typeof(RoRival), typeof(RoOtherRival), typeof(RoMentoredKeeper));

        private static Type[] Writes(params Type[] types) => [.. Sanctuary.ForWrites(types, t => t)];

        private static Type[] Deletes(params Type[] types) => [.. Sanctuary.ForDeletes(types, t => t)];

        [Fact]
        public void AWrite_PutsTheTypePointedAtFirst()
        {
            Assert.Equal([typeof(RoKeeper), typeof(RoResident)], Writes(typeof(RoResident), typeof(RoKeeper)));
        }

        [Fact]
        public void ADelete_PutsTheTypeThatPointsFirst()
        {
            Assert.Equal([typeof(RoResident), typeof(RoKeeper)], Deletes(typeof(RoKeeper), typeof(RoResident)));
        }

        [Fact]
        public void AChildsReference_OrdersItsWholeChain()
        {
            // A nap points at a hammock, and naps are written with their resident.
            Assert.True(Sanctuary.PointsAt(typeof(RoResident), typeof(RoHammock)));
            Assert.Equal(typeof(RoResident), Sanctuary.ChainOf(typeof(RoNap)));
            Assert.Equal([typeof(RoHammock), typeof(RoResident)], Writes(typeof(RoResident), typeof(RoHammock)));
        }

        [Fact]
        public void ALinkedByKey_PointsTheMembersAtTheOne()
        {
            Assert.True(Sanctuary.PointsAt(typeof(RoTroopMember), typeof(RoTroop)));
            Assert.False(Sanctuary.PointsAt(typeof(RoTroop), typeof(RoTroopMember)));
            Assert.Equal([typeof(RoTroop), typeof(RoTroopMember)], Writes(typeof(RoTroopMember), typeof(RoTroop)));
            Assert.Equal([typeof(RoTroopMember), typeof(RoTroop)], Deletes(typeof(RoTroop), typeof(RoTroopMember)));
        }

        [Fact]
        public void TypesThatPointAtNothingInTheSave_KeepTheirOrder_AndTheListIsReturnedAsItIs()
        {
            Type[] items = [typeof(RoHammock), typeof(RoTroop), typeof(RoKeeper)];

            Assert.Same(items, Sanctuary.ForWrites(items, t => t));
            Assert.Same(items, Sanctuary.ForDeletes(items, t => t));
        }

        [Fact]
        public void AnOrderThatAlreadyHolds_IsReturnedAsItIs()
        {
            Type[] items = [typeof(RoKeeper), typeof(RoHammock), typeof(RoResident)];

            Assert.Same(items, Sanctuary.ForWrites(items, t => t));
        }

        [Fact]
        public void TypesInACycle_KeepTheOrderGiven()
        {
            Assert.Equal([typeof(RoRival), typeof(RoOtherRival)], Writes(typeof(RoRival), typeof(RoOtherRival)));
            Assert.Equal([typeof(RoOtherRival), typeof(RoRival)], Writes(typeof(RoOtherRival), typeof(RoRival)));
        }

        [Fact]
        public void OnlyTheTypesThatMustMove_Move()
        {
            // The hammock and the troop owe nothing to anyone and stay where they were.
            Assert.Equal([typeof(RoHammock), typeof(RoKeeper), typeof(RoResident), typeof(RoTroop)],
                Writes(typeof(RoHammock), typeof(RoResident), typeof(RoKeeper), typeof(RoTroop)));
        }

        [Fact]
        public void ATypeThatPointsAtItself_OrdersNothingBetweenTypes()
        {
            Assert.False(Sanctuary.PointsAt(typeof(RoMentoredKeeper), typeof(RoMentoredKeeper)));
        }

        // ---- Rows of one table ---------------------------------------------------------------

        private static IReadOnlyList<IPropertySchema> MentorReference =>
            new TableSchema(ObjectSchema.Get(typeof(RoMentoredKeeper))).SelfReferences;

        [Fact]
        public void ATablesSelfReferences_AreItsLinksToItsOwnType()
        {
            Assert.Equal(nameof(RoMentoredKeeper.Mentor), Assert.Single(MentorReference).Name);
            Assert.Empty(new TableSchema(ObjectSchema.Get(typeof(RoResident))).SelfReferences);
        }

        [Fact]
        public void RowsOfABatch_AreInsertedAfterTheRowsTheyPointAt()
        {
            var mavis = new RoMentoredKeeper { Name = "Mavis" };
            var colin = new RoMentoredKeeper { Name = "Colin", Mentor = mavis };
            var trainee = new RoMentoredKeeper { Name = "Trainee", Mentor = colin };

            var ordered = RelationalDataProvider.ReferencedFirst([trainee, colin, mavis], MentorReference);

            Assert.Equal(["Mavis", "Colin", "Trainee"], ordered.Cast<RoMentoredKeeper>().Select(k => k.Name));
        }

        [Fact]
        public void RowsThatPointOutsideTheBatch_KeepTheirOrder_AndTheListIsReturnedAsItIs()
        {
            var stored = new RoMentoredKeeper { Name = "Mavis" };
            List<ICoreObject> batch = [new RoMentoredKeeper { Name = "Colin", Mentor = stored }, new RoMentoredKeeper { Name = "Trainee" }];

            Assert.Same(batch, RelationalDataProvider.ReferencedFirst(batch, MentorReference));
        }

        [Fact]
        public void RowsThatPointAtEachOther_AreAllKept()
        {
            var colin = new RoMentoredKeeper { Name = "Colin" };
            var mavis = new RoMentoredKeeper { Name = "Mavis", Mentor = colin };
            colin.Mentor = mavis;

            var ordered = RelationalDataProvider.ReferencedFirst([colin, mavis], MentorReference);

            Assert.Equal(2, ordered.Count);
            Assert.Contains(colin, ordered);
            Assert.Contains(mavis, ordered);
        }
    }
}
