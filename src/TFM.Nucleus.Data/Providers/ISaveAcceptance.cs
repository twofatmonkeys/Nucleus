namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// Lets the manager keep the per-object work of a save it runs through a context to itself. A
    /// context checks the objects it saves (their rules and save hooks) and marks them saved, putting
    /// them back if its transaction does not commit. The manager instead checks them once before its
    /// first attempt, so a retry does not run a save hook twice, and marks them once its context has
    /// committed, with the audit logger, which a context does not have. Only the manager's batch save
    /// of mixed types needs it, because it is the one manager save that goes through a context's own
    /// save.
    /// </summary>
    internal interface ISaveAcceptance
    {
        /// <summary>True leaves checking the saved objects before and marking them after to the caller.</summary>
        bool CallerManagesObjects { set; }
    }
}
