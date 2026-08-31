# Code review findings

Findings from the review carried out during the monorepo restructure of August 2026. **None of
these are fixed.** That pass deliberately changed only project structure, packaging and
documentation, so that the restructure diff stayed reviewable. This file is the backlog.

Line numbers refer to the state of the code at the time of the review and will drift.

## Confirmed defects

### Infinite recursion in `ObjectSchema.Get(dynamic)`

`src\TFM.Nucleus\Classes\ObjectSchema.cs`

```csharp
public IObjectSchema Get(dynamic _) => this.Get(_);
```

Calls itself unconditionally, so any call stack overflows. `IObjectSchema` declares no such
member, so it satisfies no contract. Delete it.

### `CoreObject.Changes` is null on every entity loaded from the database

`Changes` is assigned only in the public constructor, but the materialiser deliberately builds
entities with `RuntimeHelpers.GetUninitializedObject`, which runs no constructor. Any call to
`ClearChanges()` or to the `DataChanged` property on a loaded entity throws
`NullReferenceException`. Only entities constructed in code have a usable `Changes` collection.

### `PropertySchema.GetDBValue` rejects nullable value types

The write path compares `DBWriteType == rawValue.GetType()` by reference. For an `int?` property
`DBWriteType` is `Nullable<Int32>` while the boxed value's type is `Int32`, so the comparison
fails and the method throws `InvalidObjectTypeException`. It needs
`Nullable.GetUnderlyingType` normalisation and an assignability check rather than reference
equality.

### Type tests that are always false

- `PropertySchema.cs`: `Type is ICoreObject` asks whether a `System.Type` instance implements
  `ICoreObject`, which it never does. `ObjectSchema<T>.LinkedObjectSchemas` therefore returns an
  array of nulls. Should be `Type.IsAssignableTo(typeof(ICoreObject))`.
- `CoreObject.IsDerivedClass(Type)`: `t.IsSubclassOf(typeof(ICoreObject))` always returns false,
  because `IsSubclassOf` never matches an interface. Both overloads always return false.

### Writes to non-public setters are silently dropped

`PropertySchema` resolves `Setter = pInfo.GetSetMethod()`, which returns public setters only, and
`SetValue` is `Setter?.Invoke(...)`. A property with a protected or private setter therefore
accepts a write that does nothing, with no error. The dead code in `CoreObject` uses the correct
`GetSetMethod() ?? GetSetMethod(true)` pattern, so the two disagree.

### `CoreObject<T>.BindDataSet` produces entities that re-insert

It hydrates through `SetRawCurrentValue`, which for a new object writes into the committed
dictionary and never clears `_isNew`. The equivalent `DataSet.Bind` path in `Abstractions`
correctly uses `BindValue`, which clears `_isNew` when the `ID` arrives. Entities from
`BindDataSet` are therefore INSERTed again on save instead of updated. The method also sets
`IsLoading = false` without ever setting it true, and calls `Activator.CreateInstance<T>()`
without a `new()` constraint.

### Duplicate key crash on shadowed members

The `ObjectSchema<T>` constructor uses `Dictionary.Add` for the property, database-property and
field caches. Any derived class that redeclares a base member with `new`, or any base and derived
field name collision, throws `ArgumentException`, surfaced as the opaque message "Unable to build
object schema for type ...".

### `ObjectSchema.Initialize` is not thread safe

It uses `Interlocked.Equals(IsInitialized, 0)` in four places as though it were an atomic read.
That is the inherited `static object.Equals(object, object)`: it boxes both operands and compares
them normally, with no memory barrier. The surrounding busy-wait sleeps on a non-volatile static
`int`, and on timeout resets the other thread's flag before throwing. Replace with `Volatile.Read`,
a `Lazy<T>` or a `SemaphoreSlim`.

### Dirty checking compares against the wrong value

`SetValue` compares the incoming value only against the pending value, so assigning a property back
to its committed value still marks it changed. That produces spurious UPDATE columns and false
audit lines such as "Updated Name from A to A". The `IsNew` branch does no comparison at all.

### `CommitChanges` overwrites the server timestamp

It assigns `_properties["Timestamp"] = DateTimeOffset.Now`, discarding whatever the provider wrote
back from `SYSDATETIMEOFFSET` and substituting the client clock.

### `ClearChanges` does nothing for child objects

The child branch casts through `(childObj as ITFMHasChanges)?.ClearChanges()`, but `CoreObject`
does not implement `ITFMHasChanges`, so the cast always yields null. The same method dereferences
`childCollection.GetValue(this)!` without a guard and throws if the collection property is unset.

### `SaveAsync(IDataContext, CancellationToken)` ignores its token

The token parameter is accepted and never passed to `dataContext.SaveAsync(this)`.

### `ExpressionExtensions.ChangeTypeOfT` handles only six node types

The rewrite switch covers `OrElse`, `AndAlso`, `Equal`, `NotEqual`, `Call` and `MemberAccess`.
Every other node type, including `Not`, the relational operators, `Coalesce`, `Conditional` and
unary conversions, is returned unmodified and therefore still references the old parameter, which
fails at compile or execute time with "variable of type X referenced from scope". The `Call` branch
also assumes a static method.

### Parameterless `AddNucleus()` silently enables schema migration

`NucleusOptions.ReadOnlySchema` defaults to `true`, but the convenience `AddNucleus()` overload
sets it to `false`. The shortest registration is therefore the one that allows automatic schema
creation and migration against the target database, which is a surprising default for production.

### `IPropertySchema.Create` throws for most properties

`PropertyCreator` is only built for `[LinkedChildCollection]` properties, but `Create` invokes it
unconditionally behind `#pragma warning disable CS8602`, which suppresses the compiler warning
about exactly the null dereference that follows. It should throw a descriptive exception instead.

### `NucleusLoggerProvider` loses log batches

`Flush` dequeues entries into a local list before calling `BulkSave`. If the save throws, the
swallow-all catch discards the whole batch. `Dispose` likewise discards everything buffered if the
runtime was never initialised. The XML comment claims entries logged before initialisation are
dropped; they are actually retained until the 10,000 entry cap. `_buffer.Count` is also evaluated
on every log call, which is not O(1) on `ConcurrentQueue`.

### Registration methods use `AddSingleton`, not `TryAddSingleton`

`AddNucleus` and `AddNucleusLogging` both register unconditionally, so calling either twice
duplicates the registration. For `AddNucleusLogging` that means a second `ILoggerProvider` and
every log line persisted twice. `AddNucleusData` already uses `TryAddSingleton` and is the model to
follow.

### Array `Contains` in a predicate breaks on C# 13 and later

From C# 13 the compiler resolves `someArray.Contains(x)` to the span-based
`MemoryExtensions.Contains` rather than `Enumerable.Contains`. `Evaluator.SubtreeEvaluator`
evaluates such a subtree with `Expression.Lambda(e).Compile().DynamicInvoke(null)`, which throws
`NotSupportedException` for a method with a ref struct parameter. So a consumer on C# 13 or later
writing `LoadWhere<T>(x => someArray.Contains(x.Name))` fails at runtime, while the identical
`List<T>` version works.

This is why the test projects are pinned to `LangVersion 12.0` in `tests\Directory.Build.props`.
`ExpressionParserTests.ArrayContains_RendersInWithPerElementParameters` fails without that pin.
The fix belongs in `ExpressionParser`, which should recognise the `MemoryExtensions` overload;
remove the pin once it does.

### `System.Linq.AsyncEnumerable` makes LINQ operators ambiguous on net10.0

`ObjectDataAdaptor<T>` implements both `IQueryable<T>` and `IAsyncEnumerable<T>`. On `net10.0` the
BCL adds `System.Linq.AsyncEnumerable`, so an unqualified `.Where(...)` on an adaptor is ambiguous
between `Queryable.Where` and `AsyncEnumerable.Where` and does not compile. This is why the test
projects are pinned to `net8.0`; the shipping libraries themselves multi-target cleanly, but any
consumer on `net10.0` hits the same ambiguity. Worth resolving before consumers move to .NET 10.

## Error handling

- **Exception type laundering.** Roughly a dozen `throw new Exception(...)` sites across
  `ObjectSchema.cs`, `CoreObject.cs` and `PropertySchema.cs`, despite four purpose-built exception
  types in the same files. `ObjectSchema<T>.TryAdd` and `ExtractNucleusTypes` interpolate
  `ex.Message` into a new exception without passing the inner exception, destroying the stack trace
  of the real failure.
- **Empty catches.** `catch (Exception) { }` in the assembly scan loop of `ObjectSchema.Initialize`
  makes every assembly load failure invisible. `TryRetrievePropertyFromDB` wraps a
  `NotImplementedException` in an empty catch, so the `[RetrieveOnDemand]` feature silently does
  nothing. `catch (Exception) { throw; }` in `ObjectSchema<T>.Extract` is a no-op.
  `catch (Exception) { return null; }` in `TryGetBackingField` hides the "more than one match"
  case.
- **`ObjectSchema.TryGet` catches everything** and returns false, so a genuine schema bug is
  indistinguishable from "not a Nucleus type". Since `ExtractNucleusTypes` uses it, a misconfigured
  entity is silently skipped at startup rather than reported.
- **Missing null guards.** `GetLinkedIDsForLogging` dereferences `(obj as CoreObject)` unguarded.
  `LogChanges` and `_hasChangedLinkedObjects` call `ObjectSchema.GetProperty(key)` for every key in
  the change dictionary, which throws `PropertyNotFoundException` for any key not in the schema,
  for example a column still in the database but removed from the class.
- **Stale error message.** `EnsureSaveable` tells the caller that "`_Validate()` method must be
  called from all overidden Validate() methods". There is no `_Validate()` method, and "overidden"
  is misspelled. The message is not actionable.

## Dead code

- Roughly 430 unreachable private lines in `CoreObject.cs`: three near-identical `DynamicMethod` IL
  materialisers (`BuildObjectCollection<T>`, `BuildObjectCollection(Type)`, `ExtractCollection`)
  that duplicate the one now owned by `ObjectSchema<T>`, plus `CreateDictionaryFromDT` twice,
  `TryRetrievePropertyFromDB`, `UpdateProperty<T>`, `Base_PropertyChanged`, `Init<T>`,
  `_CurrentCache` and the `data_Context` fields. Several would fail if called: they do
  `type.GetProperty("IsLoading").GetSetMethod()` when `IsLoading` is a field, so `GetProperty`
  returns null.
- `TFMInitializeWithString` was re-added during the v2 refactor even though its only callers are
  those dead IL blocks. It and they should go together.
- `ObjectCollection<T>`, `IUser` and `IUserProvider` have no references anywhere in the workspace.
- `[SuppressCopying]` has no consumer left; `LinkedObject.LinkedIDProperty` is never read;
  `[InitializePriority]` is parsed into `ObjectSchema<T>.Priority` but the ordering code that used
  it is commented out.
- `NucleusTypeConverter` is documented in its own header as broken, yet is still wired up by
  `[TypeConverter(...)]` on both public `CoreObject` classes. Either rebind it to the v2 load
  pipeline or delete it and the attribute together.
- The legacy `Change` and `ChangeCollection` types coexist with the newer dictionary tracking.
  `HasChanges` reads the new system and `DataChanged` reads the old one, so the two can disagree.
- Large commented-out blocks in `PropertySchema.cs`, `ObjectSchema.cs`, `IObjectSchema.cs`,
  `SQLConfiguration.cs` and `DBColumn.cs`.

## Naming and style

- Attribute types in `TFM.Nucleus` lack the `Attribute` suffix (`Cacheable`, `SuppressLogging`,
  `LinkedObject`, `ParentObject`) while the ones in `Abstractions` have it
  (`DatabaseColumnAttribute`, `DeletableAttribute`). `CoreObject.cs` writes both
  `[DatabaseColumnAttribute(...)]` and `[DatabaseColumn]`.
- `AttributeUsage` is applied inconsistently. Half the attributes declare it, half do not and so
  default to `AttributeTargets.All` with `Inherited = true`. `[SuppressLogging]` being inherited
  means a base class silently suppresses audit logging for every descendant, which is almost
  certainly not intended and is asymmetric with `[Cacheable]`.
- `Changes` is a public mutable field on `CoreObject`, and `Change`'s members are public fields.
- `ObjectSchema<T>` exposes `public readonly` fields where `IObjectSchema` declares properties,
  which forces about twenty lines of explicit interface forwarding.
- `TFM`-prefixed types inside the `TFM.Nucleus` namespace: `TFMInitializeWithString`,
  `ITFMHasChanges`.
- Field naming drifts: `data_Context`, `_CurrentCache`, `dProperties`, `_ObjectSchema`,
  and `IsInitialized` / `IsInitializing` as private static fields named like properties.
- `CoreObject.cs` line 2 carries an accidental IDE auto-import,
  `using static System.Runtime.InteropServices.JavaScript.JSType;`, which pulls `Object`, `String`,
  `Number`, `Function`, `Error`, `Date` and `Void` into scope of the framework's central file.
- Boilerplate `using System; using System.Collections.Generic; ...` headers in most files despite
  `ImplicitUsings` being enabled.

## Performance

- `CoreObject.ObjectSchema` performs a `ConcurrentDictionary` lookup keyed on `GetType()` on every
  `SetValue`, `ToString` and `CommitChanges` call.
- `PropertySchema.GetValue` and `SetValue` go through `MethodInfo.Invoke` per property access
  rather than a cached delegate.
- `LinqExtensions.GetValue` compiles a fresh lambda on every call with no cache.

## Suppression backlog

`Directory.Build.props` suppresses IDE0040, CS1570, CS0414, CS0169 and the CS8600 to CS8625
nullable analysis warnings, roughly 500 in total. `tests\Directory.Build.props` suppresses
xUnit1051. All were pre-existing; they are suppressed so that CI has a green baseline to protect.
Each code removed from those lists is a small, self-contained cleanup.
