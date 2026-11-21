using System.Linq.Expressions;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// Linked collections end to end: saving the one writes the members it holds in its own
    /// transaction, a collection loads its members on first read from the stack that loaded the
    /// one, an include loads every row's members in the same read, a loaded member changed or
    /// removed is written or deleted by the one's save, and a one with members cannot be deleted.
    /// </summary>
    public abstract class LinkedCollectionIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Tag(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

        /// <summary>
        /// Saves a campaign holding <paramref name="vouchers"/> vouchers, the first of them also a
        /// referral, through the campaign's own save; runs <paramref name="check"/>, then removes
        /// what the test wrote.
        /// </summary>
        private async Task WithCampaignAsync(int vouchers, Func<ObjectDataManager, IntCampaign, string, Task> check)
        {
            var manager = fixture.RequireManager();
            var name = Tag("lc");
            var campaign = new IntCampaign { Name = name };
            for (var i = 0; i < vouchers; i++)
                campaign.Vouchers.Add(new IntVoucher { Code = $"{name}-{i}" });
            if (vouchers > 0)
                campaign.Referrals.Add(campaign.Vouchers[0]);
            await manager.SaveAsync(campaign, Ct);
            try
            {
                await check(manager, campaign, name);
            }
            finally
            {
                await manager.DeleteWhereAsync<IntVoucher>(v => v.Code!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntCampaign>(c => c.Name!.StartsWith(name), Ct);
            }
        }

        private static async Task<IntCampaign> ReloadAsync(ObjectDataManager manager, IntCampaign campaign) =>
            Assert.Single(await manager.Get<IntCampaign>().Where(c => c.ID == campaign.ID).ToListAsync(Ct));

        private static Task<long> StoredVouchersAsync(ObjectDataManager manager, IntCampaign campaign) =>
            manager.Rows<IntVoucher>(v => v.CampaignId == campaign.ID).CountAsync(Ct);

        // ---- Saving -------------------------------------------------------------------------------

        [Fact]
        public async Task SavingTheOne_WritesTheMembersItHolds()
        {
            await WithCampaignAsync(3, async (manager, campaign, name) =>
            {
                Assert.Equal(3, await StoredVouchersAsync(manager, campaign));
                Assert.All(campaign.Vouchers, v => Assert.False(v.IsNew));
                Assert.Equal(1, await manager.Rows<IntVoucher>(v => v.ReferredBy == campaign).CountAsync(Ct));
            });
        }

        [Fact]
        public async Task AFailedSave_WritesNeitherTheOneNorItsMembers()
        {
            var manager = fixture.RequireManager();
            var name = Tag("lf");
            var taken = new IntVoucher { Code = $"{name}-taken" };
            await manager.SaveAsync(taken, Ct);
            try
            {
                var campaign = new IntCampaign { Name = name };
                campaign.Vouchers.Add(new IntVoucher { Code = $"{name}-fresh" });
                campaign.Vouchers.Add(new IntVoucher { Code = taken.Code });

                await Assert.ThrowsAsync<UniqueViolationException>(() => manager.SaveAsync(campaign, Ct));

                Assert.True(campaign.IsNew);
                Assert.Equal(0, await manager.Rows<IntCampaign>(c => c.ID == campaign.ID).CountAsync(Ct));
                Assert.Equal(0, await manager.Rows<IntVoucher>(v => v.Code == $"{name}-fresh").CountAsync(Ct));
            }
            finally
            {
                await manager.DeleteWhereAsync<IntVoucher>(v => v.Code!.StartsWith(name), Ct);
                await manager.DeleteWhereAsync<IntCampaign>(c => c.Name!.StartsWith(name), Ct);
            }
        }

        [Fact]
        public async Task AMembersRule_RejectsTheOnesSave_BeforeAnythingIsWritten()
        {
            var manager = fixture.RequireManager();
            var campaign = new IntCampaign { Name = Tag("lv") };
            campaign.Vouchers.Add(new IntVoucher { Code = IntVoucher.InvalidCode });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(campaign, Ct));

            Assert.Contains("real code", ex.Message);
            Assert.Equal(0, await manager.Rows<IntCampaign>(c => c.ID == campaign.ID).CountAsync(Ct));
        }

        [Fact]
        public async Task ALoadedMemberChangedThroughTheOne_IsWrittenByItsSave()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                var loaded = await ReloadAsync(manager, campaign);
                loaded.Vouchers.Single(v => v.Code == $"{name}-1").Code = $"{name}-renamed";
                loaded.Vouchers.Add(new IntVoucher { Code = $"{name}-added" });
                await manager.SaveAsync(loaded, Ct);

                var codes = await manager.Get<IntVoucher>().Where(v => v.CampaignId == campaign.ID).ToListAsync(Ct);
                Assert.Equal([$"{name}-0", $"{name}-added", $"{name}-renamed"], codes.Select(v => v.Code).Order());
            });
        }

        [Fact]
        public async Task AStoredMemberRemovedFromTheOne_IsDeletedByItsSave()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                var loaded = await ReloadAsync(manager, campaign);
                Assert.True(loaded.Vouchers.Remove(loaded.Vouchers.Single(v => v.Code == $"{name}-1")));
                await manager.SaveAsync(loaded, Ct);

                Assert.Equal(1, await StoredVouchersAsync(manager, campaign));
                Assert.Empty(SaveGraph.Walk([loaded]).RemovedMembers);
            });
        }

        // ---- Loading ------------------------------------------------------------------------------

        [Fact]
        public async Task TheFirstReadLoadsTheMembers_AsTheyAreThen()
        {
            await WithCampaignAsync(3, async (manager, campaign, name) =>
            {
                var loaded = await ReloadAsync(manager, campaign);
                var vouchers = loaded.Vouchers;
                Assert.False(vouchers.IsLoaded);

                // Deleted after the one's load but before the first read: the read does not see it.
                await manager.DeleteWhereAsync<IntVoucher>(v => v.Code == $"{name}-2", Ct);

                Assert.Equal([$"{name}-0", $"{name}-1"], vouchers.Select(v => v.Code).Order());
                Assert.All(vouchers, v => Assert.Empty(((ICoreObject)v).ChangedProperties));
            });
        }

        [Fact]
        public async Task LoadAsync_LoadsTheMembers_AndALinkedObjectKeyHoldsTheOne()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                var loaded = await ReloadAsync(manager, campaign);
                await loaded.Referrals.LoadAsync(Ct);

                Assert.True(loaded.Referrals.IsLoaded);
                var referral = Assert.Single(loaded.Referrals);
                Assert.Same(loaded, referral.ReferredBy);
                Assert.Empty(((ICoreObject)referral).ChangedProperties);
            });
        }

        [Fact]
        public async Task AOneAContextLoaded_LoadsItsMembersThroughThatProvider()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                Expression<Func<IntCampaign, bool>> mine = c => c.ID == campaign.ID;
                IntCampaign loaded;
                using (var context = await manager.CreateContextAsync<IntCampaign>(token: Ct))
                    loaded = Assert.Single(await context.LoadAsync<IntCampaign>(mine.Body));

                // The context is gone; the read runs on a context of its own.
                Assert.Equal(2, loaded.Vouchers.Count);
            });
        }

        /// <summary>Every campaign holds its members already: the rows are deleted, so nothing could load them now.</summary>
        private static async Task AssertMembersWereIncludedAsync(ObjectDataManager manager, string name, IReadOnlyList<IntCampaign> campaigns, int each)
        {
            await manager.DeleteWhereAsync<IntVoucher>(v => v.Code!.StartsWith(name), Ct);
            Assert.All(campaigns, c => Assert.True(c.Vouchers.IsLoaded));
            Assert.All(campaigns, c => Assert.Equal(each, c.Vouchers.Count));
            Assert.All(campaigns, c => Assert.All(c.Vouchers, v => Assert.Equal(c.ID, v.CampaignId)));
        }

        /// <summary>Saves two more campaigns beside <paramref name="first"/>, each with two vouchers.</summary>
        private static async Task<List<IntCampaign>> TwoMoreAsync(ObjectDataManager manager, string name)
        {
            var more = Enumerable.Range(1, 2).Select(n =>
            {
                var campaign = new IntCampaign { Name = $"{name}-{n}" };
                campaign.Vouchers.AddRange([new IntVoucher { Code = $"{name}-{n}-a" }, new IntVoucher { Code = $"{name}-{n}-b" }]);
                return campaign;
            }).ToList();
            await manager.SaveAsync<IntCampaign>(more, Ct);
            return more;
        }

        [Fact]
        public async Task Include_LoadsTheMembersOfEveryRow_InTheSameRead()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                await TwoMoreAsync(manager, name);

                var loaded = await manager.Get<IntCampaign>().Include(c => c.Vouchers).Where(c => c.Name!.StartsWith(name)).ToListAsync(Ct);

                Assert.Equal(3, loaded.Count);
                await AssertMembersWereIncludedAsync(manager, name, loaded, 2);
            });
        }

        [Fact]
        public async Task Include_OnAPage_LoadsTheMembersOfThatPage()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                await TwoMoreAsync(manager, name);
                Expression<Func<IntCampaign, bool>> mine = c => c.Name!.StartsWith(name);

                var page = await manager.LoadPagedAsync<IntCampaign>(1, 2, mine, new LoadOptions { Include = [nameof(IntCampaign.Vouchers)] }, Ct);

                Assert.Equal(2, page.Count);
                Assert.Equal(3, page.TotalCount);
                await AssertMembersWereIncludedAsync(manager, name, page, 2);
            });
        }

        [Fact]
        public async Task Include_OnAStreamedRead_LoadsTheMembersOfEveryRow()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                await TwoMoreAsync(manager, name);
                Expression<Func<IntCampaign, bool>> mine = c => c.Name!.StartsWith(name);

                var streamed = new List<IntCampaign>();
                await foreach (var c in manager.LoadStreamAsync<IntCampaign>(mine, new LoadOptions { Include = [nameof(IntCampaign.Vouchers)] }, Ct))
                    streamed.Add(c);

                Assert.Equal(3, streamed.Count);
                await AssertMembersWereIncludedAsync(manager, name, streamed, 2);
            });
        }

        [Fact]
        public async Task Include_OfAKeyedByLinkedObjectCollection_BindsTheOneIntoEveryMember()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                var loaded = Assert.Single(await manager.Get<IntCampaign>().Include(c => c.Referrals).Where(c => c.ID == campaign.ID).ToListAsync(Ct));

                var referral = Assert.Single(loaded.Referrals);
                Assert.Same(loaded, referral.ReferredBy);
                Assert.False(loaded.Vouchers.IsLoaded);
            });
        }

        // ---- Deleting the one ---------------------------------------------------------------------

        [Fact]
        public async Task DeletingAOneWithMembers_IsRefused_AndDeletesNothing()
        {
            await WithCampaignAsync(2, async (manager, campaign, name) =>
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => manager.DeleteAsync(campaign, Ct));
                Assert.Contains("still has IntVoucher members", ex.ToString());
                await Assert.ThrowsAnyAsync<Exception>(() => manager.DeleteWhereAsync<IntCampaign>(c => c.ID == campaign.ID, Ct));

                Assert.Equal(1, await manager.Rows<IntCampaign>(c => c.ID == campaign.ID).CountAsync(Ct));
                Assert.Equal(2, await StoredVouchersAsync(manager, campaign));
            });
        }

        [Fact]
        public async Task DeletingAOneWithNoMembers_Deletes()
        {
            await WithCampaignAsync(0, async (manager, campaign, name) =>
            {
                await manager.DeleteAsync(campaign, Ct);

                Assert.Equal(0, await manager.Rows<IntCampaign>(c => c.ID == campaign.ID).CountAsync(Ct));
            });
        }
    }
}
