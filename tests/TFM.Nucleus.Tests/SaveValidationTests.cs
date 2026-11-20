namespace TFM.Nucleus.Tests
{
    /// <summary>A root with a rule, recording its save checks in the order they run.</summary>
    public class RuleWidget : CoreObject<RuleWidget>
    {
        public readonly List<string> Calls = [];

        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public override void Validate()
        {
            Calls.Add(nameof(Validate));
            if (Name == "bad")
                throw new InvalidOperationException("A widget cannot be named bad.");
            base.Validate();
        }

        public override void PreSave() => Calls.Add(nameof(PreSave));
    }

    /// <summary>An owner and child that count their save hooks.</summary>
    public class SaveHookOwner : CoreObject<SaveHookOwner>
    {
        public int PreSaveCount;

        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<SaveHookLine, SaveHookOwner> Lines => GetChildCollection<SaveHookLine, SaveHookOwner>();

        public override void PreSave() => PreSaveCount++;
    }

    public class SaveHookLine : CoreChildObject<SaveHookLine, SaveHookOwner>
    {
        public int PreSaveCount;

        [DatabaseColumn]
        public int Total { get => GetValue<int>(); set => SetValue(value); }

        public override void PreSave() => PreSaveCount++;
    }

    /// <summary>
    /// Every save path validates. Before, only an entity's own Save and SaveAsync ran
    /// Validate(): a save through the manager wrote an invalid object without a word, and
    /// PreSave() ran on no path at all. Now the manager validates each object before it builds or
    /// writes anything, throws a failed rule as it is, and runs PreSave() after. The context's
    /// saves and bulk saves validate too; those run on a real context, so the integration tests
    /// cover them.
    /// </summary>
    public class SaveValidationTests
    {
        public static TheoryData<string> ManagerSaves => new() { "Save", "SaveAsync", "SaveAsyncBatch" };

        private static (ObjectDataManager Manager, FakeDataProvider Provider) NewManager()
        {
            var provider = new FakeDataProvider();
            return (new ObjectDataManager(provider, new ObjectSchemaProvider(), null), provider);
        }

        private static async Task SaveThrough<T>(ObjectDataManager manager, string path, T obj) where T : ICoreObject<T>, new()
        {
            switch (path)
            {
                case "Save": manager.Save(obj); break;
                case "SaveAsync": await manager.SaveAsync(obj, CancellationToken.None); break;
                default: await manager.SaveAsync<T>([obj], CancellationToken.None); break;
            }
        }

        [Theory]
        [MemberData(nameof(ManagerSaves))]
        public async Task ManagerSave_RejectsAnInvalidObject_WithTheRuleItself_BeforeWriting(string path)
        {
            var (manager, provider) = NewManager();
            var widget = new RuleWidget { Name = "bad" };

            var thrown = await Record.ExceptionAsync(() => SaveThrough(manager, path, widget));

            Assert.IsType<InvalidOperationException>(thrown);
            Assert.Contains("named bad", thrown.Message);
            Assert.Empty(provider.Contexts);
            Assert.True(widget.IsNew);
        }

        [Theory]
        [MemberData(nameof(ManagerSaves))]
        public async Task ManagerSave_ValidatesOnce_ThenRunsPreSave(string path)
        {
            var (manager, _) = NewManager();
            var widget = new RuleWidget { Name = "good" };

            await SaveThrough(manager, path, widget);

            Assert.Equal([nameof(RuleWidget.Validate), nameof(RuleWidget.PreSave)], widget.Calls);
            Assert.False(widget.IsNew);
        }

        [Theory]
        [MemberData(nameof(ManagerSaves))]
        public async Task ManagerSave_RunsTheChildrensRules(string path)
        {
            var (manager, provider) = NewManager();
            var owner = new CmPlainOwner { Name = "o" };
            owner.Lines.Add(new CmPlainLine { Total = -1 });

            var thrown = await Record.ExceptionAsync(() => SaveThrough(manager, path, owner));

            Assert.Contains("negative", Assert.IsType<InvalidOperationException>(thrown).Message);
            Assert.Empty(provider.Contexts);
        }

        [Theory]
        [MemberData(nameof(ManagerSaves))]
        public async Task ManagerSave_OfAChildAsARoot_IsRefused(string path)
        {
            var (manager, provider) = NewManager();

            var thrown = await Record.ExceptionAsync(() => SaveThrough(manager, path, new CmPlainLine { Total = 1 }));

            Assert.IsType<CoreObjectException>(thrown);
            Assert.Empty(provider.Contexts);
        }

        [Fact]
        public async Task ManagerSave_RunsPreSaveOnTheOwnerAndEachNewChild_Once()
        {
            var (manager, _) = NewManager();
            var owner = new SaveHookOwner { Name = "o" };
            var first = new SaveHookLine { Total = 1 };
            var second = new SaveHookLine { Total = 2 };
            owner.Lines.Add(first);
            owner.Lines.Add(second);

            await manager.SaveAsync(owner, CancellationToken.None);

            Assert.Equal(1, owner.PreSaveCount);
            Assert.Equal(1, first.PreSaveCount);
            Assert.Equal(1, second.PreSaveCount);
        }
    }

    /// <summary>
    /// An entity's own Save and SaveAsync route through the ambient runtime, which validates, so
    /// the rules run once, not twice; and a failed rule or a refused delete reaches the caller as
    /// it was thrown, not wrapped by the runtime's reflection dispatch.
    /// </summary>
    [Collection(AmbientRuntime.Name)]
    public class InstanceSaveValidationTests
    {
        private static FakeDataProvider InstallRuntime()
        {
            var provider = new FakeDataProvider();
            NucleusRuntime.Current = new ObjectDataManager(provider, new ObjectSchemaProvider());
            return provider;
        }

        [Fact]
        public void Save_ValidatesOnce_ThenRunsPreSave()
        {
            InstallRuntime();
            var widget = new RuleWidget { Name = "good" };

            widget.Save();

            Assert.Equal([nameof(RuleWidget.Validate), nameof(RuleWidget.PreSave)], widget.Calls);
        }

        [Fact]
        public async Task SaveAsync_ValidatesOnce_ThenRunsPreSave()
        {
            InstallRuntime();
            var widget = new RuleWidget { Name = "good" };

            await widget.SaveAsync();

            Assert.Equal([nameof(RuleWidget.Validate), nameof(RuleWidget.PreSave)], widget.Calls);
        }

        [Fact]
        public void Save_OfAnInvalidObject_ThrowsTheRuleItself()
        {
            var provider = InstallRuntime();
            var widget = new RuleWidget { Name = "bad" };

            Assert.Throws<InvalidOperationException>(widget.Save);
            Assert.Empty(provider.Contexts);
        }

        [Fact]
        public async Task SaveAsync_OfAnInvalidObject_ThrowsTheRuleItself()
        {
            InstallRuntime();
            var widget = new RuleWidget { Name = "bad" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => widget.SaveAsync());
        }

        [Fact]
        public void Delete_OfATypeThatIsNotDeletable_ThrowsTheRefusalItself()
        {
            InstallRuntime();
            var widget = ObjectSchema<TestWidget>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid() });

            Assert.Throws<NotSupportedException>(widget.Delete);
        }
    }
}
