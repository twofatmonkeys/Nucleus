namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A value the server returns for a column during a write is held until the object's changes
    /// are committed. It used to replace the object's value, and drop its pending one, as soon as
    /// the write returned, so a save that then failed left the object holding a value from a
    /// rolled-back write, and saving it again wrote that value instead of the object's own.
    /// </summary>
    public class ServerValueTests
    {
        private static TestWidget Loaded(string name)
        {
            var widget = new TestWidget { Name = name };
            ((ICoreObject)widget).CommitChanges();
            return widget;
        }

        [Fact]
        public void AServerValue_IsNotTheObjectsValue_UntilItsChangesAreCommitted()
        {
            var widget = new TestWidget { Name = "client" };

            widget.AcceptServerValue(nameof(TestWidget.Name), "server");

            Assert.Equal("client", widget.Name);
            Assert.True(widget.IsNew);
        }

        [Fact]
        public void ACommit_TakesTheServerValue_OverThePendingOne()
        {
            var widget = Loaded("stored");
            widget.Name = "client";

            widget.AcceptServerValue(nameof(TestWidget.Name), "server");
            ((ICoreObject)widget).CommitChanges();

            Assert.Equal("server", widget.Name);
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void ASaveThatDidNotCommit_LeavesTheObjectAsItWas_AndTheNextSaveDropsItsValues()
        {
            var widget = Loaded("stored");
            widget.Name = "client";
            widget.AcceptServerValue(nameof(TestWidget.Name), "server");   // a write that then rolled back

            ((ICoreObject)widget).PrepareForSave();                        // the next save
            ((ICoreObject)widget).CommitChanges();                         // commits without that write's value

            Assert.Equal("client", widget.Name);
        }

        [Fact]
        public void ARolledBackTransaction_PutsALoadedObjectBack_WithItsPendingValue()
        {
            var widget = Loaded("stored");
            widget.Name = "client";
            var undo = new CommitUndoLog();

            widget.AcceptServerValue(nameof(TestWidget.Name), "server");
            ((ICoreObject)widget).CommitChangesInTransaction(undo);
            Assert.Equal("server", widget.Name);
            undo.Revert();

            Assert.Equal("client", widget.Name);
            Assert.Equal("client", Assert.Contains(nameof(TestWidget.Name), widget.ChangedProperties));
        }

        [Fact]
        public void ARolledBackTransaction_PutsANewObjectBack_WithItsOwnValue()
        {
            var widget = new TestWidget { Name = "client" };
            var undo = new CommitUndoLog();

            widget.AcceptServerValue(nameof(TestWidget.Name), "server");
            ((ICoreObject)widget).CommitChangesInTransaction(undo);
            undo.Revert();

            Assert.True(widget.IsNew);
            Assert.Equal("client", widget.Name);
        }

        [Fact]
        public void ARolledBackTransaction_KeepsAnEditMadeSinceTheCommit()
        {
            var widget = Loaded("stored");
            var undo = new CommitUndoLog();

            widget.AcceptServerValue(nameof(TestWidget.Name), "server");
            ((ICoreObject)widget).CommitChangesInTransaction(undo);
            widget.Name = "edited";
            undo.Revert();

            Assert.Equal("edited", widget.Name);
            Assert.True(widget.HasChanges);
        }
    }
}
