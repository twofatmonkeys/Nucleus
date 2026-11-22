using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    // ---- Entities ------------------------------------------------------------------------------

    [Deletable]
    public class FkKeeper : CoreObject<FkKeeper>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class FkHammock : CoreObject<FkHammock>
    {
        [DatabaseColumn] public string? Location { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable]
    public class FkResident : CoreObject<FkResident>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public FkKeeper? Keeper { get => GetValue<FkKeeper>(); set => SetValue(value); }

        /// <summary>A reference the schema cannot see, declared by hand.</summary>
        [DatabaseColumn]
        [ForeignKey(typeof(FkHammock))]
        public Guid? HammockId { get => GetValue<Guid?>(); set => SetValue(value); }

        /// <summary>A reference whose key is dropped.</summary>
        [LinkedObject]
        [DatabaseColumn]
        [ForeignKey(false)]
        public FkKeeper? FormerKeeper { get => GetValue<FkKeeper>(); set => SetValue(value); }

        /// <summary>A reference the database sets to null when its keeper goes.</summary>
        [LinkedObject]
        [DatabaseColumn]
        [ForeignKey(OnDelete = ForeignKeyAction.SetNull)]
        public FkKeeper? Backup { get => GetValue<FkKeeper>(); set => SetValue(value); }

        [Deletable]
        public NucleusChildCollection<FkNap, FkResident> Naps => GetChildCollection<FkNap, FkResident>();
    }

    public class FkNap : CoreChildObject<FkNap, FkResident>
    {
        [DatabaseColumn] public int Minutes { get => GetValue<int>(); set => SetValue(value); }
    }

    [Deletable]
    public class FkTroop : CoreObject<FkTroop>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedBy(nameof(FkMember.TroopId))]
        public NucleusLinkedCollection<FkMember, FkTroop> Members => GetLinkedCollection<FkMember, FkTroop>();
    }

    /// <summary>Soft deleted, so a hard delete of what it points at clears its references first.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class FkMember : CoreObject<FkMember>
    {
        [DatabaseColumn(Indexed = true)] public Guid? TroopId { get => GetValue<Guid?>(); set => SetValue(value); }

        [LinkedObject]
        [DatabaseColumn]
        public FkKeeper? Keeper { get => GetValue<FkKeeper>(); set => SetValue(value); }
    }

    // A reference typed as a base entity can hold a derived one, stored in a table of its own.
    public class FkAnimal : CoreObject<FkAnimal>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class FkBaboon : FkAnimal
    {
        [DatabaseColumn] public int Bananas { get => GetValue<int>(); set => SetValue(value); }
    }

    public class FkFeeding : CoreObject<FkFeeding>
    {
        [LinkedObject]
        [DatabaseColumn]
        public FkAnimal? Animal { get => GetValue<FkAnimal>(); set => SetValue(value); }
    }

    // ---- Declarations refused at schema build ----------------------------------------------------

    public class FkOnText : CoreObject<FkOnText>
    {
        [DatabaseColumn]
        [ForeignKey(typeof(FkKeeper))]
        public string? KeeperCode { get => GetValue<string>(); set => SetValue(value); }
    }

    public class FkWrongType : CoreObject<FkWrongType>
    {
        [LinkedObject]
        [DatabaseColumn]
        [ForeignKey(typeof(FkHammock))]
        public FkKeeper? Keeper { get => GetValue<FkKeeper>(); set => SetValue(value); }
    }

    public class FkNotStored : CoreObject<FkNotStored>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [ForeignKey(typeof(FkKeeper))]
        public Guid KeeperId { get => GetValue<Guid>(); set => SetValue(value); }
    }

    public class FkUntyped : CoreObject<FkUntyped>
    {
        [DatabaseColumn]
        [ForeignKey]
        public Guid? KeeperId { get => GetValue<Guid?>(); set => SetValue(value); }
    }

    /// <summary>
    /// The foreign keys the schema derives from the entity model, and how the schema update keeps
    /// them, through the SQL Server dialect over a recorded catalog.
    /// </summary>
    public class ForeignKeySchemaTests
    {
        private static readonly CatalogSnapshot Empty = new([], [], []);

        private static readonly Type[] Sanctuary =
            [typeof(FkKeeper), typeof(FkHammock), typeof(FkResident), typeof(FkNap), typeof(FkTroop), typeof(FkMember)];

        private static (RecordingSchemaStore Store, SchemaManager Manager) Build(bool foreignKeys, Func<Type, bool>? routes = null, params Type[] types)
        {
            var store = new RecordingSchemaStore(Empty, types.Length == 0 ? Sanctuary : types) { ForeignKeys = foreignKeys, Routes = routes };
            var manager = store.Manager();
            manager.BuildSchema();
            return (store, manager);
        }

        private static TableSchema Table<T>(SchemaManager manager) => manager.TableCache[typeof(T)];

        private static ForeignKeySchema Key<T>(SchemaManager manager, string column) =>
            Assert.Single(Table<T>(manager).ForeignKeys, k => k.Column.ColumnName == column);

        /// <summary>A catalog where every table and index the schema needs exists.</summary>
        private static void AllTablesCurrent(RecordingSchemaStore store, SchemaManager manager)
        {
            var tables = manager.TableCache.Values.Select(t => SchemaCatalog.Current("sqlserver", t)).ToList();
            store.Snapshot = new CatalogSnapshot(
                [.. tables.SelectMany(t => t.Tables)], [.. tables.SelectMany(t => t.Columns)], [.. tables.SelectMany(t => t.Indexes)]);
        }

        private static CatalogForeignKey Stored(ForeignKeySchema key, bool isChecked = true) => new()
        {
            TableName = key.Table.TableName,
            KeyName = key.Name,
            ColumnName = key.Column.ColumnName,
            ReferencedTableName = key.Referenced.TableName,
            ReferencedColumnName = "ID",
            OnDelete = key.OnDelete,
            IsChecked = isChecked,
        };

        // ---- What gets a key -----------------------------------------------------------------

        [Fact]
        public void EveryReferenceTheModelImplies_GetsAKey_ToTheTableItPointsAt()
        {
            var (_, manager) = Build(foreignKeys: true);

            Assert.Equal((ForeignKeySource.LinkedObject, typeof(FkKeeper)), Describe(Key<FkResident>(manager, "Keeper")));
            Assert.Equal((ForeignKeySource.Declared, typeof(FkHammock)), Describe(Key<FkResident>(manager, "HammockId")));
            Assert.Equal((ForeignKeySource.Parent, typeof(FkResident)), Describe(Key<FkNap>(manager, "Parent")));
            Assert.Equal((ForeignKeySource.LinkedBy, typeof(FkTroop)), Describe(Key<FkMember>(manager, "TroopId")));
            Assert.Equal((ForeignKeySource.LinkedObject, typeof(FkKeeper)), Describe(Key<FkMember>(manager, "Keeper")));

            static (ForeignKeySource, Type) Describe(ForeignKeySchema key) => (key.Source, key.Referenced.ObjectSchema.Type);
        }

        [Fact]
        public void AKey_IsNamedForItsTableAndColumn_AndIsNoActionUnlessDeclared()
        {
            var (_, manager) = Build(foreignKeys: true);

            var keeper = Key<FkResident>(manager, "Keeper");
            Assert.Equal($"FK_{Table<FkResident>(manager).TableName}_Keeper", keeper.Name);
            Assert.Equal(ForeignKeyAction.NoAction, keeper.OnDelete);
            Assert.Equal(ForeignKeyAction.SetNull, Key<FkResident>(manager, "Backup").OnDelete);
        }

        [Fact]
        public void ForeignKeyFalse_DropsTheKey()
        {
            var (_, manager) = Build(foreignKeys: true);

            Assert.DoesNotContain(Table<FkResident>(manager).ForeignKeys, k => k.Column.ColumnName == "FormerKeeper");
            Assert.False(Table<FkResident>(manager).FindByColumnName("FormerKeeper")!.HasColumnIndex);
        }

        [Fact]
        public void EveryKeyColumn_IsIndexed()
        {
            var (_, manager) = Build(foreignKeys: true);

            foreach (var key in manager.TableCache.Values.SelectMany(t => t.ForeignKeys))
            {
                Assert.True(key.Column.HasForeignKey);
                Assert.Contains(key.Column, key.Table.IndexedColumns);
            }
        }

        [Fact]
        public void WithKeysOff_NoTableHasKeys_AndNoReferenceIsIndexedForOne()
        {
            var (_, manager) = Build(foreignKeys: false);

            Assert.All(manager.TableCache.Values, t => Assert.Empty(t.ForeignKeys));
            Assert.False(Table<FkResident>(manager).FindByColumnName("Keeper")!.HasColumnIndex);
        }

        [Fact]
        public void ASoftDeletedTypesNoActionKeys_AreClearedByAHardDeleteOfWhatTheyPointAt()
        {
            var (_, manager) = Build(foreignKeys: true);

            Assert.Equal(["Keeper"], Table<FkKeeper>(manager).SoftReferencesIn.Select(k => k.Column.ColumnName));
            Assert.Equal(["TroopId"], Table<FkTroop>(manager).SoftReferencesIn.Select(k => k.Column.ColumnName));
            // A hard-deleted resident's references are not soft rows.
            Assert.DoesNotContain(Table<FkKeeper>(manager).SoftReferencesIn, k => k.Table == Table<FkResident>(manager));
        }

        // ---- What gets none ------------------------------------------------------------------

        [Fact]
        public void AReferenceToATypeThisProviderDoesNotStore_GetsNoKey()
        {
            var (_, manager) = Build(foreignKeys: true, routes: null, typeof(FkHammock), typeof(FkResident), typeof(FkNap));

            Assert.DoesNotContain(Table<FkResident>(manager).ForeignKeys, k => k.Column.ColumnName == "Keeper");
            Assert.Contains(Table<FkResident>(manager).ForeignKeys, k => k.Column.ColumnName == "HammockId");
        }

        [Fact]
        public void AReferenceToATypeAnotherProviderKeeps_GetsNoKey_EvenWithATableForIt()
        {
            var (_, manager) = Build(foreignKeys: true, routes: t => t != typeof(FkKeeper));

            Assert.DoesNotContain(Table<FkResident>(manager).ForeignKeys, k => k.Referenced.ObjectSchema.Type == typeof(FkKeeper));
            Assert.Empty(Table<FkKeeper>(manager).ForeignKeys);
        }

        [Fact]
        public void ADeclaredKeyThatCannotHold_FailsTheBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Build(foreignKeys: true, routes: t => t != typeof(FkHammock)));

            Assert.Contains("FkResident.HammockId", ex.Message);
            Assert.Contains("FkHammock", ex.Message);
        }

        [Fact]
        public void AReferenceToATypeWithAStoredDerivedType_GetsNoKey()
        {
            var (_, manager) = Build(foreignKeys: true, routes: null, typeof(FkAnimal), typeof(FkBaboon), typeof(FkFeeding));

            Assert.Empty(Table<FkFeeding>(manager).ForeignKeys);
        }

        [Theory]
        [InlineData(typeof(FkOnText), "Guid")]
        [InlineData(typeof(FkWrongType), "names FkHammock")]
        [InlineData(typeof(FkNotStored), "not a stored column")]
        [InlineData(typeof(FkUntyped), "without a type")]
        public void AForeignKeyMistake_FailsTheBuild_WithKeysOnOrOff(Type type, string message)
        {
            foreach (var keys in new[] { true, false })
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Build(keys, null, type, typeof(FkKeeper), typeof(FkHammock)));
                Assert.Contains(message, ex.Message);
            }
        }

        // ---- Keeping them --------------------------------------------------------------------

        [Fact]
        public void AMissingKey_MakesTheSchemaNotCurrent_AndTheUpdateCreatesItChecked()
        {
            var (store, manager) = Build(foreignKeys: true);
            AllTablesCurrent(store, manager);

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            var keeper = Key<FkResident>(manager, "Keeper");
            Assert.Contains(store.Dialect.CountOrphans(keeper).Text, store.OutsideTransaction);
            Assert.Contains(store.Dialect.AddForeignKey(keeper, checkExisting: true).Text, store.OutsideTransaction);
            Assert.Empty(store.InTransaction);
        }

        [Fact]
        public void AKeyOverRowsPointingAtNothing_IsCreatedUnchecked_WithAWarning()
        {
            var (store, manager) = Build(foreignKeys: true);
            AllTablesCurrent(store, manager);
            store.Orphans = 3;

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            var keeper = Key<FkResident>(manager, "Keeper");
            Assert.Contains(store.Dialect.AddForeignKey(keeper, checkExisting: false).Text, store.OutsideTransaction);
            Assert.Contains(store.Logged, l => l.Level == LogLevel.Warning && l.Message.Contains(keeper.Name) && l.Message.Contains("3 existing rows"));
        }

        [Fact]
        public void KeysThatAllExist_AreCurrent_AndAnUncheckedOneIsReportedNotReplaced()
        {
            var (store, manager) = Build(foreignKeys: true);
            AllTablesCurrent(store, manager);
            var keys = manager.TableCache.Values.SelectMany(t => t.ForeignKeys).ToList();
            var unchecked_ = Key<FkResident>(manager, "Keeper");
            store.Keys = [.. keys.Select(k => Stored(k, isChecked: k != unchecked_))];

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            Assert.Empty(store.OutsideTransaction);
            Assert.Contains(store.Logged, l => l.Level == LogLevel.Warning && l.Message.Contains(unchecked_.Name));
        }

        [Fact]
        public void AKeyNoLongerNeeded_OrChanged_IsDropped_AndAChangedOneCreatedAgain()
        {
            var (store, manager) = Build(foreignKeys: true);
            AllTablesCurrent(store, manager);
            var keys = manager.TableCache.Values.SelectMany(t => t.ForeignKeys).ToList();
            var backup = Key<FkResident>(manager, "Backup");
            var resident = Table<FkResident>(manager);
            store.Keys =
            [
                .. keys.Where(k => k != backup).Select(k => Stored(k)),
                // Was NO ACTION; the model says SET NULL now.
                new() { TableName = resident.TableName, KeyName = backup.Name, ColumnName = "Backup", ReferencedTableName = backup.Referenced.TableName, ReferencedColumnName = "ID", OnDelete = ForeignKeyAction.NoAction },
                new() { TableName = resident.TableName, KeyName = $"FK_{resident.TableName}_FormerKeeper", ColumnName = "FormerKeeper", ReferencedTableName = backup.Referenced.TableName, ReferencedColumnName = "ID" },
            ];

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            var drops = store.OutsideTransaction.Where(s => s.Contains("DROP CONSTRAINT")).ToList();
            Assert.Equal(2, drops.Count);
            Assert.Contains(store.Dialect.DropForeignKey(resident.TableName, backup.Name).Text, drops);
            Assert.Contains(store.Dialect.AddForeignKey(backup, checkExisting: true).Text, store.OutsideTransaction);
            // Every drop runs before any create.
            Assert.True(store.OutsideTransaction.FindLastIndex(s => s.Contains("DROP CONSTRAINT")) < store.OutsideTransaction.FindIndex(s => s.Contains("ADD CONSTRAINT")));
        }

        [Fact]
        public void TurningKeysOff_DropsTheKeysNucleusCreated()
        {
            var (_, withKeys) = Build(foreignKeys: true);
            var stored = withKeys.TableCache.Values.SelectMany(t => t.ForeignKeys).Select(k => Stored(k)).ToList();
            var (store, manager) = Build(foreignKeys: false);
            AllTablesCurrent(store, manager);
            store.Keys = stored;

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            Assert.Equal(stored.Count, store.OutsideTransaction.Count(s => s.Contains("DROP CONSTRAINT")));
            Assert.DoesNotContain(store.OutsideTransaction, s => s.Contains("ADD CONSTRAINT"));
        }

        [Fact]
        public void ATableToRebuild_LosesTheKeysFromAndToIt_InTheTransaction_BeforeItIsRenamed()
        {
            var (store, manager) = Build(foreignKeys: true);
            AllTablesCurrent(store, manager);
            var keeperTable = Table<FkKeeper>(manager);
            store.Snapshot = store.Snapshot.Retyped(keeperTable.TableName, "Name", "int");
            var keys = manager.TableCache.Values.SelectMany(t => t.ForeignKeys).ToList();
            store.Keys = [.. keys.Select(k => Stored(k))];

            Assert.Equal(DataSchemaStatus.Ok, manager.Initialize());

            var intoKeeper = keys.Where(k => k.Referenced == keeperTable).ToList();
            Assert.NotEmpty(intoKeeper);
            var firstRename = store.InTransaction.FindIndex(s => s.Contains("sp_rename"));
            Assert.True(firstRename > 0);
            foreach (var key in intoKeeper)
            {
                var drop = store.Dialect.DropForeignKey(key.Table.TableName, key.Name).Text;
                Assert.InRange(store.InTransaction.IndexOf(drop), 0, firstRename - 1);
            }
            // Keys of tables that do not change stay.
            var hammock = Key<FkResident>(manager, "HammockId");
            Assert.DoesNotContain(store.Dialect.DropForeignKey(hammock.Table.TableName, hammock.Name).Text, store.InTransaction);
        }
    }
}
