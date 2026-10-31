using TFM.Attribution;
using TFM.Nucleus.Attribution;

namespace TFM.Nucleus.IntegrationTests.Attribution
{
    /// <summary>
    /// The store against the live test database. TFM.Attribution ships no contract package, so the
    /// rules its <see cref="IAttributionStore"/> documents are tested here directly: touches append
    /// and read back by session id, a retried batch adds nothing twice, and a sale is replaced whole.
    /// Every test works on fresh session and sale ids, so no test resets another's rows and no
    /// cross-process lock is needed.
    /// </summary>
    public abstract class NucleusAttributionStoreTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private NucleusAttributionStore Store() => new(fixture.RequireManager());

        private static AttributionTouch Touch(Guid session, DateTimeOffset at, AttributionChannel channel = AttributionChannel.PaidSearch) => new()
        {
            SessionID = session,
            Touched = at,
            VisitStart = true,
            Origin = TouchOrigin.Live,
            Channel = channel,
            Source = "google",
            Medium = "cpc",
            Campaign = "brakes-spring",
            Term = "brake pads",
            Content = "ad-1",
            UtmId = "u1",
            SourcePlatform = "Google Ads",
            CreativeFormat = "text",
            MarketingTactic = "prospecting",
            ClickIdType = "gclid",
            ClickId = "Cj0KCQ-abc",
            GadSource = "1",
            GadCampaignId = "12345",
            PartnerRef = "p1",
            ReferrerHost = "google.com",
            LandingPath = "/category/brakes",
        };

        [Fact]
        public async Task Touches_RoundTripEveryColumn_OrderedByTime()
        {
            var store = Store();
            var session = Guid.NewGuid();
            var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(10));
            var later = Touch(session, start.AddHours(2), AttributionChannel.Email);
            var first = Touch(session, start);

            await store.AddTouchesAsync([later, first], Ct);
            var read = await store.GetTouchesAsync([session], Ct);

            Assert.Equal([first.ID, later.ID], read.Select(t => t.ID));
            var got = read[0];
            Assert.Equal(session, got.SessionID);
            Assert.Equal(start, got.Touched);
            Assert.True(got.VisitStart);
            Assert.Equal(TouchOrigin.Live, got.Origin);
            Assert.Equal(AttributionChannel.PaidSearch, got.Channel);
            Assert.Equal("google", got.Source);
            Assert.Equal("cpc", got.Medium);
            Assert.Equal("brakes-spring", got.Campaign);
            Assert.Equal("brake pads", got.Term);
            Assert.Equal("ad-1", got.Content);
            Assert.Equal("u1", got.UtmId);
            Assert.Equal("Google Ads", got.SourcePlatform);
            Assert.Equal("text", got.CreativeFormat);
            Assert.Equal("prospecting", got.MarketingTactic);
            Assert.Equal("gclid", got.ClickIdType);
            Assert.Equal("Cj0KCQ-abc", got.ClickId);
            Assert.Equal("1", got.GadSource);
            Assert.Equal("12345", got.GadCampaignId);
            Assert.Equal("p1", got.PartnerRef);
            Assert.Equal("google.com", got.ReferrerHost);
            Assert.Equal("/category/brakes", got.LandingPath);
            Assert.Equal(AttributionChannel.Email, read[1].Channel);
        }

        [Fact]
        public async Task RetriedBatch_AddsNothingTwice()
        {
            var store = Store();
            var session = Guid.NewGuid();
            var batch = new[] { Touch(session, DateTimeOffset.UtcNow), Touch(session, DateTimeOffset.UtcNow.AddMinutes(1)) };

            await store.AddTouchesAsync(batch, Ct);
            await store.AddTouchesAsync(batch, Ct);
            await store.AddTouchesAsync([batch[0], Touch(session, DateTimeOffset.UtcNow.AddMinutes(2))], Ct);

            Assert.Equal(3, (await store.GetTouchesAsync([session], Ct)).Count);
        }

        [Fact]
        public async Task DuplicateWithinOneBatch_IsStoredOnce()
        {
            var store = Store();
            var session = Guid.NewGuid();
            var touch = Touch(session, DateTimeOffset.UtcNow);

            await store.AddTouchesAsync([touch, touch], Ct);

            Assert.Single(await store.GetTouchesAsync([session], Ct));
        }

        [Fact]
        public async Task RacingRetries_EndWithOneRowPerTouch()
        {
            var store = Store();
            var session = Guid.NewGuid();
            var batch = Enumerable.Range(0, 20).Select(i => Touch(session, DateTimeOffset.UtcNow.AddSeconds(i))).ToArray();

            // Both attempts may pass the existing-id check before either commits; the unique index
            // then refuses the loser, which the worker would retry. Either way no touch is doubled.
            var results = await Task.WhenAll(
                Attempt(() => store.AddTouchesAsync(batch, Ct)),
                Attempt(() => Store().AddTouchesAsync(batch, Ct)));
            Assert.Contains(true, results);

            Assert.Equal(20, (await store.GetTouchesAsync([session], Ct)).Count);

            static async Task<bool> Attempt(Func<Task> write)
            {
                try { await write(); return true; }
                catch (Exception) { return false; }
            }
        }

        [Fact]
        public async Task GetTouches_SpansSessions_AndIgnoresEmptyAndUnknownIds()
        {
            var store = Store();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await store.AddTouchesAsync([Touch(a, DateTimeOffset.UtcNow), Touch(b, DateTimeOffset.UtcNow.AddMinutes(1))], Ct);

            Assert.Equal(2, (await store.GetTouchesAsync([a, b, Guid.Empty, Guid.NewGuid()], Ct)).Count);
            Assert.Empty(await store.GetTouchesAsync([Guid.Empty], Ct));
            Assert.Empty(await store.GetTouchesAsync([], Ct));
        }

        [Fact]
        public async Task OversizedValues_AreClippedToTheColumn()
        {
            var store = Store();
            var session = Guid.NewGuid();
            var touch = Touch(session, DateTimeOffset.UtcNow);
            touch.LandingPath = "/" + new string('x', AttributionTouch.LandingPathMaxLength + 50);

            await store.AddTouchesAsync([touch], Ct);

            var read = Assert.Single(await store.GetTouchesAsync([session], Ct));
            Assert.Equal(AttributionTouch.LandingPathMaxLength, read.LandingPath!.Length);
        }

        [Fact]
        public async Task UnknownChannelName_ReadsAsOther()
        {
            var manager = fixture.RequireManager();
            var session = Guid.NewGuid();
            var row = new AttributionTouchRecord { TouchId = Guid.NewGuid(), SessionId = session, Touched = DateTimeOffset.UtcNow, Origin = "Live", Channel = "Carrier Pigeon" };
            await manager.SaveAsync(row);

            var read = Assert.Single(await Store().GetTouchesAsync([session], Ct));
            Assert.Equal(AttributionChannel.Other, read.Channel);
        }

        [Fact]
        public async Task ReplaceSale_WritesTheSaleAndShares_AndReplacesThemWhole()
        {
            var store = Store();
            var saleId = Guid.NewGuid();
            var touchA = Guid.NewGuid();
            var touchB = Guid.NewGuid();
            var first = Sale(saleId, 100m,
                Share(touchA, 0, SharePosition.First, 0.5m, 50m),
                Share(touchB, 1, SharePosition.Last, 0.5m, 50m));

            await store.ReplaceSaleAsync(first, Ct);
            var read = await store.GetSaleAsync(saleId, Ct);

            Assert.NotNull(read);
            Assert.Equal("Order", read!.SaleType);
            Assert.Equal(100m, read.SaleValue);
            Assert.Equal(first.SessionIDs, read.SessionIDs);
            Assert.Equal([touchA, touchB], read.Shares.Select(s => s.TouchID!.Value));
            Assert.Equal(SharePosition.Last, read.Shares[1].Position);
            Assert.Equal(0.5m, read.Shares[1].Weight);

            var second = Sale(saleId, 80m, Share(null, 0, SharePosition.None, 1m, 80m, AttributionChannel.Unattributed));
            await store.ReplaceSaleAsync(second, Ct);
            read = await store.GetSaleAsync(saleId, Ct);

            Assert.Equal(80m, read!.SaleValue);
            var only = Assert.Single(read.Shares);
            Assert.Null(only.TouchID);
            Assert.Equal(AttributionChannel.Unattributed, only.Channel);
            Assert.Single(await fixture.RequireManager().Get<AttributionSaleRecord>().Where(s => s.SaleId == saleId).ToListAsync(Ct));
        }

        [Fact]
        public async Task Weight_KeepsSixPlaces()
        {
            var store = Store();
            var saleId = Guid.NewGuid();
            await store.ReplaceSaleAsync(Sale(saleId, 10m,
                Share(Guid.NewGuid(), 0, SharePosition.First, 0.333334m, 3.34m),
                Share(Guid.NewGuid(), 1, SharePosition.Middle, 0.333333m, 3.33m),
                Share(Guid.NewGuid(), 2, SharePosition.Last, 0.333333m, 3.33m)), Ct);

            var read = await store.GetSaleAsync(saleId, Ct);

            Assert.Equal(1m, read!.Shares.Sum(s => s.Weight));
            Assert.Equal(10m, read.Shares.Sum(s => s.SaleValueShare));
        }

        [Fact]
        public async Task CancelledToken_IsHonoured()
        {
            var store = Store();
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.AddTouchesAsync([Touch(Guid.NewGuid(), DateTimeOffset.UtcNow)], cancelled.Token));
        }

        private static AttributionSale Sale(Guid saleId, decimal value, params AttributionShare[] shares) => new()
        {
            SaleID = saleId,
            SaleType = "Order",
            SessionIDs = [Guid.NewGuid(), Guid.NewGuid()],
            Completed = DateTimeOffset.UtcNow,
            SaleValue = value,
            AmountPaid = value,
            WindowDays = 30,
            Model = "PositionBased40-20-40",
            TouchesInWindow = shares.Length,
            TouchesTotal = shares.Length,
            Recorded = DateTimeOffset.UtcNow,
            Shares = shares,
        };

        private static AttributionShare Share(Guid? touch, int order, SharePosition position, decimal weight, decimal value,
            AttributionChannel channel = AttributionChannel.PaidSearch) => new()
        {
            TouchID = touch,
            SortOrder = order,
            Position = position,
            Weight = weight,
            SaleValueShare = value,
            AmountPaidShare = value,
            Channel = channel,
            Source = "google",
            Campaign = "brakes-spring",
        };
    }
}
