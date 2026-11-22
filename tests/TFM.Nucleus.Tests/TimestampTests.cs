using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The server stamps Timestamp on every write and the value is not read back, so a commit
    /// gives each row the save wrote the client clock: the object the save was asked for always,
    /// a dependent only when it was new or changed. A row the save did not write keeps the value
    /// it was loaded with, as the stored row does. An update used to leave a loaded object with
    /// its loaded stamp.
    /// </summary>
    public class TimestampTests
    {
        private static readonly DateTimeOffset Stored = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static T Loaded<T>(Dictionary<string, object?> values) where T : CoreObject
        {
            values.TryAdd("ID", Guid.NewGuid());
            values.TryAdd("Timestamp", Stored);
            return ObjectSchema<T>.BuildObjectViaReflection(values);
        }

        [Fact]
        public void ANewObject_TakesTheClockOnCommit()
        {
            var widget = new TestWidget { Name = "w" };
            var before = DateTimeOffset.Now;

            ((ICoreObject)widget).CommitChanges();

            Assert.InRange(widget.Timestamp, before, DateTimeOffset.Now);
        }

        [Fact]
        public void AChangedLoadedObject_TakesTheClockOnCommit()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored" });
            Assert.Equal(Stored, widget.Timestamp);
            widget.Name = "changed";
            var before = DateTimeOffset.Now;

            ((ICoreObject)widget).CommitChanges();

            Assert.InRange(widget.Timestamp, before, DateTimeOffset.Now);
        }

        [Fact]
        public void AnUnchangedLoadedRoot_TakesTheClock_BecauseASaveAlwaysWritesIt()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored" });

            ((ICoreObject)widget).CommitChanges();

            Assert.NotEqual(Stored, widget.Timestamp);
        }

        [Fact]
        public void OnlyTheChildrenTheSaveWrote_TakeTheRootsStamp()
        {
            var owner = Loaded<CmPropertyOwner>(new() { ["Name"] = "owner" });
            var changed = Loaded<CmPropertyLine>(new() { ["Sequence"] = 1 });
            var unchanged = Loaded<CmPropertyLine>(new() { ["Sequence"] = 2 });
            ((INucleusChildCollection)owner.Lines).AttachLoaded([changed, unchanged]);
            changed.Sequence = 9;
            var added = new CmPropertyLine { Sequence = 3 };
            owner.Lines.Add(added);

            ((ICoreObject)owner).CommitChanges();

            Assert.NotEqual(Stored, owner.Timestamp);
            Assert.Equal(owner.Timestamp, changed.Timestamp);
            Assert.Equal(owner.Timestamp, added.Timestamp);
            Assert.Equal(Stored, unchanged.Timestamp);
        }

        [Fact]
        public void ARolledBackCommit_PutsTheLoadedStampBack()
        {
            var owner = Loaded<CmPropertyOwner>(new() { ["Name"] = "owner" });
            var line = Loaded<CmPropertyLine>(new() { ["Sequence"] = 1 });
            ((INucleusChildCollection)owner.Lines).AttachLoaded([line]);
            owner.Name = "renamed";
            line.Sequence = 9;
            var undo = new CommitUndoLog();

            ((ICoreObject)owner).CommitChangesInTransaction(undo);
            Assert.NotEqual(Stored, owner.Timestamp);
            Assert.NotEqual(Stored, line.Timestamp);
            undo.Revert();

            Assert.Equal(Stored, owner.Timestamp);
            Assert.Equal(Stored, line.Timestamp);
            Assert.DoesNotContain(nameof(CoreObject.Timestamp), ((ICoreObject)owner).ChangedProperties.Keys);
        }

        [Fact]
        public void TwoCommitsInOneTransaction_RollBackToTheLoadedStamp()
        {
            var widget = Loaded<TestWidget>(new() { ["Name"] = "stored" });
            var undo = new CommitUndoLog();
            widget.Name = "first";
            ((ICoreObject)widget).CommitChangesInTransaction(undo);
            widget.Name = "second";
            ((ICoreObject)widget).CommitChangesInTransaction(undo);

            undo.Revert();

            Assert.Equal(Stored, widget.Timestamp);
        }
    }
}
