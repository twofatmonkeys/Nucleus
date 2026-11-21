using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A save through a data context commits its objects in memory as the writes run, recording
    /// what it changed in the transaction's <see cref="CommitUndoLog"/>. A transaction that does not
    /// commit reverts the log, which must leave each object as it was before its first save in the
    /// transaction, with every change made since still pending, so a retry saves it without a reload.
    /// </summary>
    public class CommitUndoTests
    {
        private static T Loaded<T>(Dictionary<string, object?> values) where T : CoreObject
        {
            values.TryAdd("ID", Guid.NewGuid());
            return ObjectSchema<T>.BuildObjectViaReflection(values);
        }

        private static void Commit(ICoreObject obj, CommitUndoLog undo) => obj.CommitChangesInTransaction(undo);

        // ---- Properties -----------------------------------------------------------------------

        [Fact]
        public void ANewObject_IsNewAgain_WithEveryValuePending_AndNoTimestamp()
        {
            var widget = new TestWidget { Name = "w", Quantity = 3 };
            var undo = new CommitUndoLog();

            Commit(widget, undo);
            Assert.False(widget.IsNew);
            Assert.False(widget.HasChanges);

            undo.Revert();

            Assert.True(widget.IsNew);
            var changed = ((ICoreObject)widget).ChangedProperties;
            Assert.Equal("w", changed[nameof(TestWidget.Name)]);
            Assert.Equal(3, changed[nameof(TestWidget.Quantity)]);
            Assert.DoesNotContain(nameof(CoreObject.Timestamp), changed.Keys);
            Assert.True(undo.IsEmpty);
        }

        [Fact]
        public void ALoadedObject_GetsItsStoredValuesBack_AndItsWrittenChangesPendingAgain()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored", ["Quantity"] = 1 });
            widget.Name = "changed";
            var undo = new CommitUndoLog();

            Commit(widget, undo);
            Assert.False(widget.HasChanges);

            undo.Revert();

            Assert.False(widget.IsNew);
            Assert.Equal("changed", widget.Name);
            var changed = ((ICoreObject)widget).ChangedProperties;
            Assert.Equal("changed", Assert.Single(changed).Value);
            // What the object holds as stored is the row as it was before the transaction.
            widget.Name = "stored";
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void AnEditAfterTheSave_StaysPending_AndAnEditBackToTheStoredValueIsNoChange()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored", ["Quantity"] = 1 });
            widget.Name = "changed";
            var undo = new CommitUndoLog();
            Commit(widget, undo);

            widget.Name = "stored";
            widget.Quantity = 7;
            undo.Revert();

            var changed = ((ICoreObject)widget).ChangedProperties;
            Assert.DoesNotContain(nameof(TestWidget.Name), changed.Keys);
            Assert.Equal(7, changed[nameof(TestWidget.Quantity)]);
            Assert.Equal("stored", widget.Name);
        }

        [Fact]
        public void TwoSavesInOneTransaction_RevertToBeforeTheFirst_WithTheEditsBetweenThemPending()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored", ["Quantity"] = 1 });
            widget.Name = "first";
            var undo = new CommitUndoLog();
            Commit(widget, undo);
            widget.Quantity = 2;
            Commit(widget, undo);

            undo.Revert();

            var changed = ((ICoreObject)widget).ChangedProperties;
            Assert.Equal("first", changed[nameof(TestWidget.Name)]);
            Assert.Equal(2, changed[nameof(TestWidget.Quantity)]);
            widget.Name = "stored";
            widget.Quantity = 1;
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void ANewObjectSavedTwice_IsNewAgain_WithTheEditBetweenTheSaves()
        {
            var widget = new TestWidget { Name = "w" };
            var undo = new CommitUndoLog();
            Commit(widget, undo);
            widget.Quantity = 5;
            Commit(widget, undo);

            undo.Revert();

            Assert.True(widget.IsNew);
            var changed = ((ICoreObject)widget).ChangedProperties;
            Assert.Equal("w", changed[nameof(TestWidget.Name)]);
            Assert.Equal(5, changed[nameof(TestWidget.Quantity)]);
        }

        [Fact]
        public void AClearedLog_RevertsNothing()
        {
            var widget = new TestWidget { Name = "w" };
            var undo = new CommitUndoLog();
            Commit(widget, undo);

            undo.Clear();
            undo.Revert();

            Assert.False(widget.IsNew);
            Assert.False(widget.HasChanges);
        }

        // ---- Child collections ----------------------------------------------------------------

        [Fact]
        public void ChildCollection_NewChildrenAreNewAgain_AndTheRemovalsArePendingAgain()
        {
            var owner = Loaded<CmPropertyOwner>(new() { ["Name"] = "owner" });
            var stored = Loaded<CmPropertyLine>(new() { ["Sequence"] = 1 });
            ((INucleusChildCollection)owner.Lines).AttachLoaded([stored]);
            owner.Lines.Remove(stored);
            var added = new CmPropertyLine { Sequence = 2 };
            owner.Lines.Add(added);
            var undo = new CommitUndoLog();

            Commit(owner, undo);
            Assert.False(added.IsNew);
            Assert.Empty(((INucleusChildCollection)owner.Lines).RemovedObjects);

            undo.Revert();

            Assert.True(added.IsNew);
            Assert.Same(stored, Assert.Single(((INucleusChildCollection)owner.Lines).RemovedObjects));
        }

        [Fact]
        public void ChildCollection_AChangedLoadedChildHasItsChangePendingAgain()
        {
            var owner = Loaded<CmPropertyOwner>(new() { ["Name"] = "owner" });
            var stored = Loaded<CmPropertyLine>(new() { ["Sequence"] = 1 });
            ((INucleusChildCollection)owner.Lines).AttachLoaded([stored]);
            stored.Sequence = 9;
            var undo = new CommitUndoLog();

            Commit(owner, undo);
            Assert.False(stored.HasChanges);
            undo.Revert();

            Assert.Equal(9, Assert.Single(((ICoreObject)stored).ChangedProperties).Value);
        }

        // ---- Linked collections ---------------------------------------------------------------

        private sealed class MembersLoader(Func<IEnumerable<ICoreObject>> members) : ILinkedObjectLoader
        {
            public ICoreObject? LoadLinkedObject(Type type, Guid id) => throw new InvalidOperationException("No linked object is read here.");

            public void LoadLinkedCollection(INucleusLinkedCollection collection) => collection.AttachLoaded(members());

            public Task LoadLinkedCollectionAsync(INucleusLinkedCollection collection, CancellationToken cancellationToken = default)
            {
                collection.AttachLoaded(members());
                return Task.CompletedTask;
            }
        }

        [Fact]
        public void LinkedCollection_NewMembersAreNewAgain_AndTheRemovalsArePendingAgain()
        {
            var campaign = Loaded<LcCampaign>(new() { ["Name"] = "campaign" });
            var stored = Loaded<LcDeletableVoucher>(new() { ["Code"] = "S", ["CampaignId"] = campaign.ID });
            ((ICoreObject)campaign).BindLinkedObjectLoader(new MembersLoader(() => [stored]));
            campaign.Passes.Remove(stored);
            var added = new LcDeletableVoucher { Code = "N" };
            campaign.Passes.Add(added);
            var undo = new CommitUndoLog();

            Commit(campaign, undo);
            Assert.False(added.IsNew);
            Assert.Empty(((INucleusLinkedCollection)campaign.Passes).RemovedMembers);

            undo.Revert();

            Assert.True(added.IsNew);
            Assert.Same(stored, Assert.Single(((INucleusLinkedCollection)campaign.Passes).RemovedMembers));
            Assert.Same(added, Assert.Single(SaveGraph.Walk([campaign]).Members));
        }

        [Fact]
        public void LinkedCollection_AMemberAddedBackSinceIsNotRemovedAgain()
        {
            var campaign = Loaded<LcCampaign>(new() { ["Name"] = "campaign" });
            var stored = Loaded<LcDeletableVoucher>(new() { ["Code"] = "S", ["CampaignId"] = campaign.ID });
            ((ICoreObject)campaign).BindLinkedObjectLoader(new MembersLoader(() => [stored]));
            campaign.Passes.Remove(stored);
            var undo = new CommitUndoLog();
            Commit(campaign, undo);

            campaign.Passes.Add(stored);
            undo.Revert();

            Assert.Empty(((INucleusLinkedCollection)campaign.Passes).RemovedMembers);
            Assert.Same(stored, Assert.Single(campaign.Passes));
        }
    }
}
