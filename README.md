# TFM.Nucleus

The core of the Nucleus entity framework: `CoreObject` entity base classes with dictionary-based change tracking, attribute-driven schema reflection (`ObjectSchema`/`PropertySchema` with IL-emitted materializers), the static entity API (`LoadByID`, `LoadWhere`, `Save`, `Delete` — thin forwarders over the DI-registered runtime), audit logging, the opt-in `NucleusLoggerProvider` for persisting host logs, and DI registration (`AddNucleus()`, `AddNucleusLogging()`).

Pair with `TFM.Nucleus.Data` (provider-neutral data services) and a provider such as `TFM.Nucleus.Data.SQL`. See the Nucleus.v2 solution README for the full picture.
