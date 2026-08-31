# Code review findings

Findings from the review carried out during the monorepo restructure of August 2026, and the state
of each one after the fix pass that followed.

Everything under "Fixed" has regression cover in `tests/TFM.Nucleus.Tests/RegressionTests.cs` unless
noted otherwise. Everything under "Open" is still true of the code.

## Open

### The parameterless AddNucleus() enables schema migration

`NucleusOptions.ReadOnlySchema` defaults to `true`, but the convenience `AddNucleus()` overload sets
it to `false`. The shortest registration is therefore the one that allows automatic schema creation
and migration against the target database.

**Deliberately not changed.** Two production sites already call it and depend on the migration
behaviour; flipping the default would silently stop their schema updates, which is a worse failure
than the surprise. Changing it is a product decision, and the safe route is an explicit
`AddNucleus(o => o.ReadOnlySchema = true)` at the call sites first. The behaviour is now called out
in the registration reference in `README.md`.

### IDE0040, roughly 450 occurrences

Explicit `public` on interface members, which the shared `.editorconfig` asks to be left implicit.
Suppressed in `Directory.Build.props`. It is a mechanical edit across most files in the repo and was
kept out of the behavioural fixes so that this diff stayed reviewable.

### xUnit1051 in the test projects

Calls taking a `CancellationToken` should pass `TestContext.Current.CancellationToken`. Suppressed
in `tests/Directory.Build.props`. Worth doing, but it is a test-wide edit.

### Features that are declared but not implemented

- `[RetrieveOnDemand]` marks a property for lazy per-property loading. Nothing implements it. The
  attribute and `IPropertySchema.RetrieveOnDemand` remain so the intent is not lost; the dead
  `TryRetrievePropertyFromDB` that pretended to back it has been deleted.
- `[InitializePriority]` is parsed into `ObjectSchema<T>.Priority`, but the ordering code that
  consumed it is still commented out.

### The legacy ChangeCollection is vestigial

`CoreObject.Changes` and the `Change` / `ChangeCollection` types are the v1 change tracking. Nothing
populates them any more: audit summaries are built from the property dictionaries, and
`DataChanged` now reports the same state as `HasChanges`. `Changes` is documented as legacy and is
safe to call, but removing it is a public API change and was left for a major version.

### Commented-out code

Sizeable commented blocks remain in `PropertySchema.cs`, `IObjectSchema.cs`, `SQLConfiguration.cs`
and `DBColumn.cs`.

## Fixed

### Infinite recursion in ObjectSchema.Get(dynamic)

`public IObjectSchema Get(dynamic _) => this.Get(_);` called itself unconditionally and satisfied no
interface. Deleted.

### CoreObject.Changes was null on every object loaded from the database

`Changes` was assigned only in the constructor, but the materialiser builds objects with
`RuntimeHelpers.GetUninitializedObject`, which runs no constructor. `ClearChanges()` and
`DataChanged` threw `NullReferenceException` on any loaded object. Now created on demand.

### PropertySchema.GetDBValue rejected nullable value types

The write path compared `DBWriteType == rawValue.GetType()` by reference. For an `int?` property
`DBWriteType` is `Nullable<Int32>` while the boxed value's type is `Int32`, so it threw
`InvalidObjectTypeException` for every non-null nullable value. It now normalises through
`Nullable.GetUnderlyingType` and tests assignability.

### Type tests that were always false

- `PropertySchema`: `Type is ICoreObject` asked whether a `System.Type` instance implements
  `ICoreObject`, so `LinkedObjectSchemas` was an array of nulls. Now `Type.IsAssignableTo(...)`,
  and the schema arrays filter out entries that do not resolve.
- `CoreObject.IsDerivedClass`: `IsSubclassOf` never matches an interface, so both overloads always
  returned false. Now uses `IsAssignableFrom` and excludes abstract types and interfaces.

### Writes to non-public setters were silently dropped

`GetSetMethod()` returns public setters only, and `SetValue` was `Setter?.Invoke(...)`, so a
property with a protected or private setter accepted a write that did nothing. Both accessors now
fall back to `nonPublic: true`, and a genuinely unwritable property throws instead of no-opping.

### BindDataSet produced objects that re-inserted

It hydrated through `SetRawCurrentValue`, which for a new object writes into the committed
dictionary and never clears `_isNew`, so every object it returned was INSERTed again on save
instead of updated. It now binds through `BindValue`, which clears `_isNew` when the ID arrives, and
calls `OnAfterLoadFromDB`.

### Duplicate key crash on shadowed members

The `ObjectSchema<T>` constructor used `Dictionary.Add` for the property, database-property and
field caches, so any derived class redeclaring a base member with `new` threw `ArgumentException`.
Now `TryAdd`, which keeps the most derived member.

### ObjectSchema.Initialize was not thread safe

It used `Interlocked.Equals(...)`, which is the inherited `object.Equals` and carries no memory
barrier, around a busy-wait on a non-volatile field that reset the other thread's flag on timeout.
Replaced with a lock and `Volatile` reads. Assembly load failures are no longer swallowed
indiscriminately: only the expected load exceptions are caught, and a
`ReflectionTypeLoadException` now uses the types that did load.

### Dirty checking compared against the wrong value

`SetValue` compared only against the pending value, so assigning a property back to its committed
value still marked it changed, wrote a needless column and logged "updated X from A to A".
It now compares against the value the property actually reads as, and assigning back to the
committed value clears the pending edit.

### CommitChanges overwrote the server timestamp

It assigned `DateTimeOffset.Now` unconditionally, discarding the `SYSDATETIMEOFFSET` value the
provider had written back. The local clock is now only a fallback when the server supplied nothing.

### ClearChanges did nothing for child objects

The child branch cast through `ITFMHasChanges`, which `CoreObject` does not implement, so the cast
always yielded null and every child was skipped. It also dereferenced an unmaterialised collection
property. Both fixed.

### SaveAsync(IDataContext, CancellationToken) ignored its token

`IDataContext` had a token overload for every async member except `SaveAsync` and `DeleteAsync`.
Those overloads were added, the SQL provider implements them, and `CoreObject.SaveAsync` now passes
the token through.

### ExpressionExtensions.ChangeTypeOfT handled only six node types

Every other node type was returned unmodified and therefore still referenced the old parameter,
failing with "variable of type X referenced from scope". Rewritten as an `ExpressionVisitor`, which
handles all node types, and it now rebinds instance method calls rather than assuming static ones.

### Array Contains in a predicate broke on C# 13 and later

From C# 13 the compiler resolves `someArray.Contains(x)` to the span-based
`MemoryExtensions.Contains`. `Evaluator` tried to fold the span conversion into a constant, which
throws `NotSupportedException` for a ref struct. `Evaluator` now refuses to fold ref-struct-typed
subtrees, and `ExpressionParser.HandleContains` unwraps the span conversion, so both spellings
translate to the same `IN` clause. The `LangVersion 12.0` pin on the test projects is gone.

### System.Linq.AsyncEnumerable made LINQ operators ambiguous on net10.0

`ObjectDataAdaptor<T>` is both `IQueryable<T>` and `IAsyncEnumerable<T>`, so on .NET 10 an
unqualified `.Where(...)` was ambiguous between `Queryable.Where` and the new
`AsyncEnumerable.Where` and did not compile. `IObjectDataAdaptor<T>` now declares `Where` as an
instance method, which beats both extension methods. The `net8.0` pin on the test projects is gone
and the whole suite runs on both target frameworks.

### IPropertySchema.Create threw for most properties

`PropertyCreator` is only built for `[LinkedChildCollection]` properties, but `Create` invoked it
unconditionally behind a `#pragma warning disable CS8602` that suppressed the warning about exactly
the null dereference that followed. It now throws a descriptive `PropertySchemaException`.

### NucleusLoggerProvider lost log batches

`Flush` dequeued into a local list before saving, so a failure discarded the whole batch. The batch
is now returned to the buffer for the next attempt. `_buffer.Count`, which is not O(1) on
`ConcurrentQueue` and was evaluated on every log call, is replaced by a tracked counter. The class
comment claimed entries logged before initialisation were dropped; they are retained, and the
comment now says so.

### Registration methods were not idempotent

`AddNucleus` and `AddNucleusLogging` used `AddSingleton`, so calling either twice registered twice
and, for the logger, persisted every log line twice. Now `TryAddSingleton` and `TryAddEnumerable`.

### NucleusChildCollection never raised PropertyChanged

The collection exposes `INotifyPropertyChanged` and callers can subscribe, but nothing ever raised
the event, so every subscriber stayed silent. `NotifyParent` now raises it alongside notifying the
parent.

### Exception handling

- The schema exception types dropped their message and inner exception instead of passing them to
  the base, so stack traces of the real failure were lost. All three now chain properly and accept
  an inner exception, and the bare `throw new Exception(...)` sites use them.
- `ObjectSchema.TryGet` caught everything and returned false, making a genuine schema bug
  indistinguishable from "not a Nucleus type" and silently skipping misconfigured entities at
  startup. It now answers only the "is this an entity type" question and lets real faults throw.
- `catch (Exception) { throw; }` in `Extract` and `catch (Exception) { return null; }` in
  `TryGetBackingField` are gone. The latter hid the "more than one match" case for shadowed
  members, which is now handled by taking the most derived match.
- The audit log walked `ObjectSchema.GetProperty(key)` for every changed key and threw
  `PropertyNotFoundException` for any column no longer on the class. It now skips unknown keys.
- `EnsureSaveable` told callers to call a `_Validate()` method that does not exist. The message now
  describes what to actually do.

### Dead code

Roughly 560 lines removed: the three unreachable `DynamicMethod` materialisers in `CoreObject` and
their helpers (`CreateDictionaryFromDT` twice, `ExtractCollection`, `TryRetrievePropertyFromDB`,
`UpdateProperty`, `Base_PropertyChanged`, `Init<T>`, `_CurrentCache`, the `data_Context` pair), the
`TFMInitializeWithString` type they were the only callers of, `ObjectCollection<T>`, `IUser`,
`IUserProvider`, `[SuppressCopying]`, and the unused `_parent` field on `CoreChildObject`. The
accidental `using static System.Runtime.InteropServices.JavaScript.JSType` import at the top of
`CoreObject.cs` is gone too.

Deleting this removed nearly every nullable-analysis warning in the repo. The suppression list in
`Directory.Build.props` went from twelve codes to one, and the remaining real warnings in the SQL
provider were fixed rather than suppressed.

### NucleusTypeConverter advertised a conversion it could not do

`ConvertFrom` reflected for a nonexistent static `Load` method on the descriptor context's own type
and threw for every input, while `CanConvertFrom` claimed it worked. It now supports only the
entity-to-Guid direction it can actually perform and points callers at `LoadByID<T>`.

### Attribute usage was inconsistent

Half the attributes declared `AttributeUsage` and half defaulted to `AttributeTargets.All` with
`Inherited = true`. All now declare their targets explicitly. `[SuppressLogging]` stays inherited,
deliberately: a base type that opts out of logging is making a decision about its data, and a
derived type should have to opt back in rather than start logging it by default.

### Performance

- `CoreObject.ObjectSchema` did a dictionary lookup keyed on `GetType()` on every `SetValue`,
  `ToString` and `CommitChanges`. Now cached per instance, in a field that is left null so an object
  materialised without a constructor still resolves it on first use.
- `PropertySchema.GetValue` and `SetValue` went through `MethodInfo.Invoke` on every property
  access. They now use delegates bound once at schema build time, falling back to reflection if the
  runtime will not bind one.
- `LinqExtensions.GetValue` compiled a fresh lambda on every call. Constants and closure field reads
  are now read directly, which covers the overwhelming majority of translated predicates.
