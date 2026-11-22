// The usage examples of the guide, as methods that compile and that SampleTests runs.
// Each #region is copied into the guide by name; DocSampleTests fails when a copy differs.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Schema;
using TFM.Nucleus.Testing;

namespace PortlyPrimate
{
    public static class Registration
    {
        public static async Task<ServiceProvider> QuickStartAsync(string databaseFile)
        {
            #region quick-start
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus();                         // options and the schema provider; the schema is writable
            services.AddNucleusData();                     // the provider registry and IObjectDataManager
            services.AddNucleusSqliteDataProvider(options =>
            {
                options.Database = databaseFile;           // created, with its folder, when missing
            });

            var provider = services.BuildServiceProvider();
            var data = provider.GetRequiredService<IObjectDataManager>();

            // Connects, then creates or updates a table for every entity class it finds.
            var status = await data.InitializeDataProviderAsync();
            if (status != DataProviderStatus.Ok)
                throw new InvalidOperationException($"The database is not ready: {status}.");
            #endregion
            return provider;
        }

        public static void SqlServer(IServiceCollection services, string password)
        {
            #region register-sqlserver
            services.AddNucleusSqlServerDataProvider(options =>
            {
                options.Server = "localhost";              // host, host:port or host\instance
                options.Database = "PortlyPrimate";
                options.Username = "sanctuary";            // or UseIntegratedSecurity, or ConnectionString
                options.Password = password;
            });
            #endregion
        }

        public static void Postgres(IServiceCollection services, string password)
        {
            #region register-postgres
            services.AddNucleusPostgresDataProvider(options =>
            {
                options.Server = "localhost";              // host or host:port
                options.Database = "portlyprimate";
                options.Username = "sanctuary";            // or ConnectionString
                options.Password = password;
            });
            #endregion
        }

        public static void Sqlite(IServiceCollection services)
        {
            #region register-sqlite
            services.AddNucleusSqliteDataProvider(options =>
            {
                options.Database = @"C:\ProgramData\PortlyPrimate\sanctuary.db";   // created with its folder when missing
            });
            #endregion
        }

        public static void RetryPolicy(IServiceCollection services)
        {
            #region retry-policy
            services.AddNucleusSqliteDataProvider(options =>
            {
                options.Database = @"C:\ProgramData\PortlyPrimate\sanctuary.db";
                // A manager save, delete or load that fails transiently, having applied nothing, is
                // attempted again on a new connection: up to 3 attempts in all, 200 ms then 400 ms apart.
                options.RetryAttempts = 3;
            });
            #endregion
        }

        public static void FromConfiguration(IServiceCollection services, IConfiguration configuration)
        {
            #region register-configuration
            // appsettings.json:
            //   "Nucleus": { "ReadOnlySchema": false, "AppName": "PortlyPrimate.Web" },
            //   "Database": { "Server": "db01", "Database": "PortlyPrimate", "Username": "sanctuary" }
            // The password comes from a secret store, not from the file.
            services.AddNucleus(options => configuration.GetSection("Nucleus").Bind(options));
            services.AddNucleusData();
            services.AddNucleusSqlServerDataProvider(options =>
            {
                configuration.GetSection("Database").Bind(options);
                options.Password = configuration["Database:Password"];
            });
            #endregion
        }

        public static void TwoDatabases(IServiceCollection services, string password)
        {
            #region register-rooted
            // Everything under ShopRoot<T> lives in the shop's database; everything else in the main one.
            services.AddNucleusSqlServerDataProvider(options =>
            {
                options.Server = "localhost";
                options.Database = "PortlyPrimate";
                options.Username = "sanctuary";
                options.Password = password;
            });
            services.AddNucleusSqlServerDataProvider(typeof(ShopRoot<>), options =>
            {
                options.Server = "localhost";
                options.Database = "PortlyPrimateShop";
                options.Username = "sanctuary";
                options.Password = password;
                options.TableNaming = TableNamingPolicy.ObjectName(2);   // tables named by the last two parts of the type name
                options.RenameFullNameTables = true;                     // adopt each existing full-name table
            });
            #endregion
        }
    }

    #region store
    // What an application service or a store looks like: it takes the interface DI registers.
    public sealed class KeeperDesk(IObjectDataManager data)
    {
        public Task<Monkey?> FindAsync(Guid id, CancellationToken ct) =>
            data.LoadByIDAsync<Monkey>(id, ct);

        public Task SaveAllAsync(IReadOnlyList<Monkey> monkeys, CancellationToken ct) =>
            data.SaveAsync(monkeys, ct);                   // one transaction

        // Two types in one transaction, committed when the work is done. A failed save rolls it all back.
        public async Task WeighAsync(Monkey monkey, WeighIn weighIn, CancellationToken ct)
        {
            await using var context = await data.CreateContextAsync<Monkey>(ContextOptions.Transactional, ct);
            await context.SaveAsync(monkey, ct);           // validates, then writes
            await context.SaveAsync(weighIn, ct);
            await context.CommitAsync(ct);
        }
    }
    #endregion

    #region testing-fixture
    // One stack for every test in a collection. xunit v3 shown; any framework's fixture has the same shape.
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private static NucleusTestDatabase Database => NucleusTestDatabase.Sqlite;   // or .SqlServer, or .Postgres
        private NucleusTestStack? _stack;

        public async ValueTask InitializeAsync()
        {
            if (Database.IsConfigured)
                _stack = await Database.StartSharedStackAsync();   // holds the schema lock while it starts
        }

        /// <summary>The manager, or a skipped test when no test database is configured.</summary>
        public IObjectDataManager RequireData()
        {
            Assert.SkipWhen(_stack is null, Database.SkipReason ?? "No test database is configured.");
            return _stack!.Manager;
        }

        public ValueTask DisposeAsync()
        {
            _stack?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    #endregion

    public sealed class Samples(IObjectDataManager data)
    {
        public async Task<Monkey> StaticApiAsync()
        {
            #region static-api
            // The static API routes through the runtime AddNucleusData publishes when the manager is first resolved.
            var gerald = new Monkey { Name = "Gerald", WeightKg = 14.2m, Mood = Mood.Smug };
            gerald.Save();

            var found = CoreObject.LoadByID<Monkey>(gerald.ID);
            var smug = CoreObject.LoadWhere<Monkey>(m => m.Mood == Mood.Smug);
            var first = await CoreObject.LoadFirstWhereAsync<Monkey>(m => m.Name!.StartsWith("Ger"));

            gerald.WeightKg = 14.6m;                       // only the changed column is written
            await gerald.SaveAsync();
            #endregion
            Assert.NotNull(found);
            Assert.NotEmpty(smug);
            return first!;
        }

        public async Task<int> QueriesAsync(CancellationToken ct)
        {
            #region queries
            var monkeys = data.Get<Monkey>();

            var smug = await monkeys.Where(m => m.Mood == Mood.Smug && m.WeightKg > 10).ToListAsync(ct);
            var gerald = await monkeys.FirstOrDefaultAsync(m => m.Name == "Gerald", ct);
            var anyHangry = await monkeys.AnyAsync(m => m.Mood == Mood.Hangry, ct);
            var heavy = await monkeys.CountAsync(m => m.WeightKg > 20, ct);

            // One page and the unpaged total, in a single round trip.
            var page = await monkeys.Where(m => m.WeightKg > 10).ToPagedResultAsync(skip: 0, take: 20, ct);
            Console.WriteLine($"{page.Count} of {page.TotalCount}, more to come: {page.HasMore}");

            // Only Where translates. Order and project in memory, or use a row read.
            var names = smug.OrderBy(m => m.Name).Select(m => m.Name).ToList();
            #endregion
            Assert.NotNull(gerald);
            Assert.False(anyHangry);
            return heavy + names.Count;
        }

        public async Task<BananaOrder> ChildCollectionsAsync(CancellationToken ct)
        {
            #region children
            var order = new BananaOrder { PlacedUtc = DateTimeOffset.UtcNow, Status = OrderStatus.Placed };
            var cavendish = new BananaOrderLine { Variety = "Cavendish", Quantity = 400 };
            order.Lines.Add(cavendish);
            order.Lines.Add(new BananaOrderLine { Variety = "Lady Finger", Quantity = 60 });
            cavendish.Substitutions.Add(new LineSubstitution { Variety = "Plantain", Reason = "supplier ran out" });

            // Save the root. The lines and their substitutions are validated and written with it, in one transaction.
            await data.SaveAsync(order, ct);

            // A load brings the whole graph back.
            var loaded = await data.LoadByIDAsync<BananaOrder>(order.ID, ct);
            var line = loaded!.Lines.First(l => l.Variety == "Cavendish");
            line.Quantity = 360;                           // a changed child is written by its parent's save
            await data.SaveAsync(loaded, ct);

            // Deleting the root deletes its lines and their substitutions, loaded or not.
            // await data.DeleteAsync(loaded, ct);
            #endregion
            return loaded;
        }

        public async Task<int> LoadOptionsAsync(Guid residentId, CancellationToken ct)
        {
            #region load-options
            var options = new LoadOptions
            {
                Include = [nameof(Resident.Naps), nameof(Resident.Keeper)],   // a lazy collection and a reference, in the same round trip
                IncludeDeleted = false,                                       // true also returns soft-deleted rows
            };
            var resident = await data.LoadByIDAsync<Resident>(residentId, options, ct);

            var withoutLines = new LoadOptions { Exclude = [nameof(BananaOrder.Lines)] };   // a child collection left unloaded
            var orders = await data.LoadAsync<BananaOrder>(o => o.Status == OrderStatus.Placed, withoutLines, ct);
            var page = await data.LoadPagedAsync<BananaOrder>(skip: 0, take: 20, o => o.Status == OrderStatus.Placed, withoutLines, ct);

            // An excluded collection throws when read, until it is loaded.
            var firstOrder = orders.First();
            await firstOrder.Lines.LoadAsync(ct);
            #endregion
            return resident!.Naps.Count() + page.Count + firstOrder.Lines.Count();
        }

        public async Task<Resident> LazyAndLinkedAsync(CancellationToken ct)
        {
            #region relationships
            var mavis = new Keeper { Name = "Mavis", Email = "mavis@portlyprimate.example" };
            var loungers = new Troop { Name = "The Loungers", Motto = "Why stand" };
            await data.SaveAsync(mavis, ct);
            await data.SaveAsync(loungers, ct);

            var brenda = new Resident { Name = "Brenda", Keeper = mavis };
            brenda.Naps.Add(new NapLog { StartedUtc = DateTimeOffset.UtcNow, Minutes = 95, Location = "the good hammock" });

            // Add a member to a linked collection and save the one: the member is saved with it, its key set.
            loungers.Residents.Add(brenda);
            await data.SaveAsync(loungers, ct);

            // A listing that reads a lazy collection, a reference or a linked collection on every row includes it.
            var residents = await data.Get<Resident>()
                .Include(r => r.Naps)
                .Include(r => r.Keeper)
                .Where(r => r.TroopId == loungers.ID)
                .ToListAsync(ct);
            var troops = await data.Get<Troop>().Include(t => t.Residents).Where(t => t.Name == "The Loungers").ToListAsync(ct);

            // Without Include, the first read of each is a round trip of its own.
            var loaded = await data.LoadByIDAsync<Resident>(brenda.ID, ct);
            var keeper = loaded!.Keeper;                   // loads the keeper
            var naps = loaded.Naps.Count();                // loads the naps
            #endregion
            Assert.Single(residents);
            Assert.Single(troops[0].Residents);
            Assert.Equal("Mavis", keeper!.Name);
            Assert.Equal(1, naps);
            return loaded;
        }

        public async Task<long> ChildQueryAsync(Resident resident, CancellationToken ct)
        {
            #region child-query
            // Part of a large collection: query the child type by its parent. The parent's collection stays unloaded.
            var longNaps = await data.Get<NapLog>()
                .Where(n => n.Parent.ID == resident.ID && n.Minutes > 60)
                .ToPagedResultAsync(skip: 0, take: 50, ct);
            var total = await data.Rows<NapLog>(n => n.Parent == resident).CountAsync(ct);
            #endregion
            return longNaps.Count + total;
        }

        public async Task<int> RemoveChildAsync(Resident resident, CancellationToken ct)
        {
            #region remove-child
            // Naps is declared [Deletable], so a stored nap removed from it is deleted by the save.
            var nap = resident.Naps.First();
            resident.Naps.Remove(nap);
            await data.SaveAsync(resident, ct);
            #endregion
            return resident.Naps.Count();
        }

        public async Task<long> RowReadsAsync(DateTimeOffset since, CancellationToken ct)
        {
            #region row-reads
            // The newest 100 visits, two columns each, and how many match in all: one round trip.
            var page = await data.Rows<GiftShopVisit>(v => v.OccurredUtc >= since)
                .Select(v => v.OccurredUtc, v => v.Path)
                .OrderByDescending(v => v.OccurredUtc)
                .ToPageAsync(skip: 0, take: 100, ct);

            // One row per kind, with figures worked out in the database.
            var groups = await data.Rows<GiftShopVisit>(v => v.OccurredUtc >= since)
                .GroupBy(v => v.Kind)
                .Count("Visits")
                .CountDistinct(v => v.SessionId, "Sessions")
                .Max(v => v.OccurredUtc, "Last")
                .ToListAsync(ct);
            foreach (var g in groups)
                Console.WriteLine($"{g.Get<VisitKind>("Kind")}: {g.Get<long>("Visits")} visits in {g.Get<long>("Sessions")} sessions");

            var total = await data.Rows<GiftShopVisit>(v => v.Kind == VisitKind.Purchase).CountAsync(ct);
            #endregion
            return page.TotalCount + groups.Count + total;
        }

        public async Task<bool> UniqueViolationAsync(CancellationToken ct)
        {
            #region unique-violation
            try
            {
                await data.SaveAsync(new Keeper { Name = "Colin", Email = "mavis@portlyprimate.example" }, ct);
            }
            catch (UniqueViolationException ex) when (ex.Is<Keeper>(nameof(Keeper.Email)))
            {
                return true;                               // another keeper has that address
            }
            catch (UniqueViolationException ex) when (ex.IsIndex<GiftShopVisit>("SessionPath"))
            {
                return true;                               // a unique [TableIndex], by its declared name
            }
            #endregion
            return false;
        }

        public async Task<(bool Refused, Guid? ReportedBy)> ForeignKeyViolationAsync(CancellationToken ct)
        {
            #region foreign-key-violation
            var colin = new Keeper { Name = "Colin", Email = "colin@portlyprimate.example" };
            await data.SaveAsync(colin, ct);
            var report = new MischiefReport { Culprit = "Brenda", Description = "Hid the keeper's keys.", Severity = Severity.Cheeky, ReportedBy = colin.ID };
            await data.SaveAsync(report, ct);

            var refused = false;
            try
            {
                await data.DeleteAsync(colin, ct);
            }
            // SQLite does not say which key broke, so the names are null there.
            catch (ForeignKeyViolationException ex) when (ex.Is<MischiefReport>(nameof(MischiefReport.ReportedBy)) || ex.EntityType is null)
            {
                refused = true;                            // a live report still names Colin
            }

            await data.DeleteAsync(report, ct);            // soft: the row stays, flagged
            await data.DeleteAsync(colin, ct);             // the flagged report lets go of Colin first
            #endregion
            var stored = await data.LoadByIDAsync<MischiefReport>(report.ID, new LoadOptions { IncludeDeleted = true }, ct);
            return (refused, stored!.ReportedBy);
        }

        public async Task WeighWithRetryAsync(Monkey monkey, WeighIn weighIn, CancellationToken ct)
        {
            #region transient-retry
            // The policy never repeats work on a caller-owned context: the unit of work is the
            // caller's to repeat, from the start, when the failure says nothing was applied.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var context = await data.CreateContextAsync<Monkey>(ContextOptions.Transactional, ct);
                    await context.SaveAsync(monkey, ct);
                    await context.SaveAsync(weighIn, ct);
                    await context.CommitAsync(ct);
                    return;
                }
                catch (DatabaseException ex) when (ex.IsTransient && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);   // a deadlock, a dropped connection, a timeout
                }
            }
            #endregion
        }

        public async Task StatementTimeoutAsync(CancellationToken ct)
        {
            #region statement-timeout
            // A long job's own statements get five minutes; every other context keeps the provider's limit.
            await using var context = await data.CreateContextAsync<WeighIn>(
                new ContextOptions { UseTransaction = true, StatementTimeoutSeconds = 300 }, ct);
            await context.SaveAsync(new WeighIn { MonkeyName = "Gerald", WeightKg = 14.7m, Notes = "after lunch" }, ct);
            await context.CommitAsync(ct);
            #endregion
        }

        public async Task<int> DeletesAsync(CancellationToken ct)
        {
            #region deletes
            var report = new MischiefReport { Culprit = "Brenda", Description = "Stole the keeper's lunch. Again.", Severity = Severity.Brazen };
            await data.SaveAsync(report, ct);

            await data.DeleteAsync(report, ct);            // soft: the row stays, flagged

            var live = await data.LoadAsync<MischiefReport>(r => r.Culprit == "Brenda", ct);                                          // empty
            var all = await data.LoadAsync<MischiefReport>(r => r.Culprit == "Brenda", new LoadOptions { IncludeDeleted = true }, ct);
            var viaQuery = await data.Get<MischiefReport>().IncludeDeleted().Where(r => r.Culprit == "Brenda").ToListAsync(ct);
            var ever = await data.Rows<MischiefReport>(r => r.Culprit == "Brenda").IncludeDeleted().CountAsync(ct);    // a row read sees them when asked too

            // Delete by predicate: no row is loaded, child rows go too, and the count comes back.
            var removed = await data.DeleteWhereAsync<WeighIn>(w => w.MonkeyName == "Nobody", ct);
            #endregion
            Assert.Empty(live);
            Assert.Single(viaQuery);
            Assert.Equal(viaQuery.Count, ever);
            return all.Count() + removed;
        }

        public async Task<int> SearchAsync(CancellationToken ct)
        {
            #region full-text
            // Contains on a FullTextSearch column searches the index on an engine that has one.
            var lunches = await data.Get<MischiefReport>().IncludeDeleted().Where(r => r.Description!.Contains("lunch")).ToListAsync(ct);
            #endregion
            return lunches.Count;
        }

        public async Task<int> MixedSaveAndStreamAsync(CancellationToken ct)
        {
            #region mixed-save
            // Objects of different types in one call: one transaction per provider that stores them.
            // A batch too large for ordinary statements goes to the provider's bulk engine by itself.
            await data.SaveAsync(new ICoreObject?[]
            {
                new BananaVariety { Name = "Cavendish", Curvature = 7 },
                new BananaVariety { Name = "Plantain", Curvature = 3 },
                new WeighIn { MonkeyName = "Sir Reginald", WeightKg = 31.0m, Notes = "  no change  " },
            }, ct);
            #endregion

            #region streaming
            // Rows are yielded as they arrive, so a large result is never held whole.
            var heavy = 0;
            await foreach (var weighIn in data.LoadStreamAsync<WeighIn>(w => w.WeightKg > 0, cancellationToken: ct))
            {
                if (weighIn.IsHeavy)
                    heavy++;
            }
            #endregion
            return heavy;
        }

        public async Task<BananaVariety?> CachedAsync(CancellationToken ct)
        {
            #region cacheable
            // The first read of every row fills the cache; later reads of every row or by ID answer
            // from memory until a save or a delete evicts it.
            var varieties = await data.LoadAsync<BananaVariety>(cancellationToken: ct);   // a list of your own, over the shared objects
            var cavendish = varieties.First(v => v.Name == "Cavendish");
            var again = await data.LoadByIDAsync<BananaVariety>(cavendish.ID, ct);        // by ID: an index lookup in memory
            #endregion
            Assert.Same(cavendish, again);
            return again;
        }

        public static async Task ChangesAsync(IObjectDataManager data, CancellationToken ct)
        {
            #region change-subscription
            // SQL Server and PostgreSQL only: SQLite throws NotSupportedException.
            await using var subscription = await data.SubscribeToChangesAsync<BananaVariety>(ct);
            subscription.Changed += (_, e) => Console.WriteLine($"{e.EntityType.Name} changed outside this process: reload it.");
            subscription.Faulted += (_, e) => Console.WriteLine("The subscription died; subscribe again.");
            #endregion
            await Task.CompletedTask;
        }

        public static async Task TestingAsync(CancellationToken ct)
        {
            #region testing
            var database = NucleusTestDatabase.Sqlite;             // or .SqlServer, or .Postgres
            if (!database.IsConfigured)
                return;                                            // skip with database.SkipReason, your framework's way

            using var schemaLock = await database.SchemaLockAsync(ct);   // another run may share the database
            using var stack = await database.StartStackAsync();    // AddNucleus plus the provider, schema initialised
            var desk = new KeeperDesk(stack.Manager);
            #endregion
            Assert.NotNull(desk);
        }
    }
}
