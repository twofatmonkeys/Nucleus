# Writing a provider

A provider package is a dialect and little else. This page describes where the line is drawn, for
someone adding an engine or changing one.

## The provider seam

An application codes against `TFM.Nucleus.Data` and touches a provider package only to register
and configure it, so the same entities run on whichever database a deployment chooses.

`TFM.Nucleus.Data` holds everything that does not depend on the engine:

- **The query model** (`TFM.Nucleus.Data.Queries`): `SelectQuery`, `InsertQuery`, `UpdateQuery` and `DeleteWhereQuery` decide what a query means: its condition tree, order, page, child collection queries, soft-delete filter and the children-first order of a delete cascade. `WhereRenderer` is the one walk of a condition tree; `ParameterBinding` names the parameters an IN term expands to.
- **The schema model** (`TFM.Nucleus.Data.Schema`): `TableSchema` and `ColumnSchema` describe each entity's table, with what each column stores (`ColumnTypeIntent`) and its size. `SchemaManager` compares them with the database and creates tables, adds missing columns in place, rebuilds a table whose column types changed, and syncs indexes.
- **The provider SDK** (`TFM.Nucleus.Data.Providers`): `RelationalDataProvider` and `DbDataContext<TSelf>` are the provider and context over System.Data.Common, with pooling, the transaction tree, reads, deletes, the write paths and failure translation. `BulkWritePlanner` stages bulk writes, `ChangeSubscriptionBase<T>` and `ListenerRefCounter` back change notifications, and `ProviderRegistration` is the registration every provider's `Add...` extensions share. An oversized write the bulk engine cannot take runs its commands in order on its own context, on every engine (`AllowsConcurrentWriters` is obsolete and no longer read). A dialect whose indexes cannot carry included columns sets `ISchemaDialect.SupportsIncludedColumns` to false.

## What a provider supplies

A provider package implements the dialect:

- an `IQueryRenderer` that writes each query;
- an `ISchemaDialect` that writes DDL and maps column types;
- an `ICatalogReader` over the engine's catalog;
- an `ISchemaStore` that ties them together.

It derives its provider and context from the SDK base classes, recognises its engine's unique
violations, and exposes only its registration, its configuration and its exception.

Unit tests hold the line. `ProviderSeamTests` scans the compiled strings of the three neutral
assemblies for SQL and for provider names, and checks they reference no provider.
`ProviderSurfaceTests` holds each provider's public surface to its registration, configuration and
exception.
