# Writing a provider

## Provider seam

Applications code against `TFM.Nucleus.Data` and touch a provider package only to register and configure it, so the same entities run on whichever database a deployment chooses.

`TFM.Nucleus.Data` holds everything that does not depend on the engine:

- **The query model** (`TFM.Nucleus.Data.Queries`): `SelectQuery`, `InsertQuery`, `UpdateQuery` and `DeleteWhereQuery` decide what a query means: its condition tree, order, page, child collection queries, soft-delete filter and the children-first order of a delete cascade. `WhereRenderer` is the one walk of a condition tree; `ParameterBinding` names the parameters an IN term expands to.
- **The schema model** (`TFM.Nucleus.Data.Schema`): `TableSchema` and `ColumnSchema` describe each entity's table, with what each column stores (`ColumnTypeIntent`) and its size. `SchemaManager` compares them with the database and creates tables, adds missing columns in place, rebuilds a table whose column types changed, and syncs indexes.
- **The provider SDK** (`TFM.Nucleus.Data.Providers`): `RelationalDataProvider` and `DbDataContext<TSelf>` are the provider and context over System.Data.Common, with pooling, the transaction tree, reads, deletes, the write paths and failure translation. `BulkWritePlanner` stages bulk writes, `ChangeSubscriptionBase<T>` and `ListenerRefCounter` back change notifications, and `ProviderRegistration` is the registration every provider's `Add...` extensions share. An engine that lets one connection write at a time sets `DbDataContext.AllowsConcurrentWriters` to false, and an oversized write then runs on its own context instead of fanning out; one whose indexes cannot carry included columns sets `ISchemaDialect.SupportsIncludedColumns` to false.

A provider package implements the dialect: an `IQueryRenderer` that writes each query, an `ISchemaDialect` that writes DDL and maps column types, an `ICatalogReader` over the engine's catalog and an `ISchemaStore` that ties them together. It derives its provider and context from the SDK base classes, recognises its engine's unique violations, and exposes only its registration and configuration. Unit tests enforce the boundary: one scans the compiled strings of the neutral and store assemblies for SQL and for provider names, one checks they reference no provider, and one holds each provider's public surface to its registration and configuration.
