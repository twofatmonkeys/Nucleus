# Writing a provider

A provider package is a dialect and little else. This page describes where the line is drawn, for
someone adding an engine or changing one.

## The provider seam

An application codes against `TFM.Nucleus.Data` and touches a provider package only to register
and configure it, so the same entities run on whichever database a deployment chooses.

`TFM.Nucleus.Data` holds everything that does not depend on the engine:

- **The query model** (`TFM.Nucleus.Data.Queries`): `SelectQuery`, `InsertQuery`, `UpdateQuery` and `DeleteWhereQuery` decide what a query means: its condition tree, order, page, child collection queries, soft-delete filter and the children-first order of a delete cascade. A renderer writes a root's order as `SelectQuery` decides it: the caller's order, followed by the engine's insertion order (ClusterID, or what stands for it) when `BreaksTiesByInsertion`, or insertion order alone when `OrdersByInsertion`, which every skipped or limited root without an order is, except one marked `ReadsExistenceOnly`. A dialect whose tables carry ClusterID sets `ISchemaDialect.HasClusterIdColumn`, so the schema check rebuilds a table that lacks it, and indexes it. `WhereRenderer` is the one walk of a condition tree; `ParameterBinding` names the parameters an IN term expands to.
- **The schema model** (`TFM.Nucleus.Data.Schema`): `TableSchema` and `ColumnSchema` describe each entity's table, with what each column stores (`ColumnTypeIntent`) and its size. `SchemaManager` compares them with the database and creates tables, adds missing columns in place, rebuilds a table whose column types changed, and syncs indexes.
- **The provider SDK** (`TFM.Nucleus.Data.Providers`): `RelationalDataProvider` and `DbDataContext<TSelf>` are the provider and context over System.Data.Common, with pooling, the transaction tree, reads, deletes, the write paths and failure translation. `BulkWritePlanner` stages bulk writes, `ChangeSubscriptionBase<T>` and `ListenerRefCounter` back change notifications, and `ProviderRegistration` is the registration every provider's `Add...` extensions share. An oversized write the bulk engine cannot take runs its commands in order on its own context, on every engine (`AllowsConcurrentWriters` is obsolete and no longer read). A dialect whose indexes cannot carry included columns sets `ISchemaDialect.SupportsIncludedColumns` to false. `MaxConnectionPoolSize` is the provider's one connection limit: `DbDataContext` takes a root context's slot from `RelationalDataProvider.ConnectionSlots`, and a change subscription takes its own with `ConnectionSlots.AcquireAsync` before it opens its connection and hands it to `ChangeSubscriptionBase<T>.Slot`, which gives it back on dispose. `BuildConnection` returns a connection that does not enlist in an ambient transaction, from one connection string with the driver's pool sized to the setting, so the driver keeps one pool. `ConnectionOpener` takes a total timeout (`ConnectionOpener.TimeoutOf(connection)`), so retries share the connect timeout.

## What a provider supplies

A provider package implements the dialect:

- an `IQueryRenderer` that writes each query. A list of values in a predicate
  (`ids.Contains(x.ID)`) is a term whose value is a `ValueList`: the distinct, non-null values. A
  renderer that can send a list as one parameter (an array, a table, a JSON document) returns true
  from `BindsAsList` for the terms it writes that way, and its context overrides
  `CreateListParameter` to bind them. Left alone, a list binds a parameter per value and is
  refused past `MaxParametersPerCommand`;
- an `ISchemaDialect` that writes DDL and maps column types;
- an `ICatalogReader` over the engine's catalog;
- an `ISchemaStore` that ties them together.

It derives its provider and context from the SDK base classes, recognises its engine's unique
violations, and exposes only its registration, its configuration and its exception.

Unit tests hold the line. `ProviderSeamTests` scans the compiled strings of the three neutral
assemblies for SQL and for provider names, and checks they reference no provider.
`ProviderSurfaceTests` holds each provider's public surface to its registration, configuration and
exception.
