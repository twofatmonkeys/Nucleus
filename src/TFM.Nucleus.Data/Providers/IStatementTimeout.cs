namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// Lets the provider give a context the statement time limit its <see cref="ContextOptions"/>
    /// asked for, once the context is open. The limit is read as each statement is prepared, so
    /// setting it after opening covers every statement the context runs.
    /// </summary>
    internal interface IStatementTimeout
    {
        /// <summary>The context's own limit in seconds, 0 for none, in place of its parent's or the provider's.</summary>
        int StatementTimeoutSeconds { set; }
    }
}
