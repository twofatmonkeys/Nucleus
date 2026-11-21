namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// Lets the manager keep marking the objects it saves through a context itself. A context
    /// marks the objects it saves as saved, and puts them back if its transaction does not commit;
    /// the manager instead marks them once its context has committed, with the audit logger, which
    /// a context does not have. Only the manager's batch save of mixed types needs it, because it
    /// is the one manager save that goes through a context's own save.
    /// </summary>
    internal interface ISaveAcceptance
    {
        /// <summary>False leaves the saved objects as they were for the caller to mark.</summary>
        bool AcceptsSavedObjects { set; }
    }
}
