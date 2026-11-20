using System.Collections.Generic;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The checks every save path runs before it builds or writes anything: each object's rules,
    /// with those of the dependents it reaches, then its save hooks
    /// (<see cref="ICoreObject.PrepareForSave"/>). The manager's saves and the context's bulk saves
    /// run them through this, and the context's single-object save calls the object's directly, so
    /// no path writes an object that has not passed them. Each path runs them once per object, so
    /// the rules run once per save.
    /// </summary>
    internal static class SaveChecks
    {
        /// <summary>Runs the checks on each object to be saved, in order; nulls are skipped. Throws the first rule that fails.</summary>
        public static void Run<T>(IEnumerable<T?>? objs) where T : ICoreObject
        {
            if (objs == null)
                return;
            foreach (var obj in objs)
                obj?.PrepareForSave();
        }
    }
}
