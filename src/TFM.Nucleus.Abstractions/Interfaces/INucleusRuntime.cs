using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    /// <summary>
    /// The runtime surface behind the CoreObject static convenience API. Implemented by the
    /// object data manager and published through <see cref="NucleusRuntime.Current"/> when the
    /// application registers Nucleus in DI. All operations route to the provider owning each
    /// entity's registered root type.
    /// </summary>
    public interface INucleusRuntime
    {
        void Save(ICoreObject obj);
        Task SaveAsync(ICoreObject obj, CancellationToken token = default);
        void Delete(ICoreObject obj);
        Task DeleteAsync(ICoreObject obj, CancellationToken token = default);
        void BulkSave(IEnumerable<ICoreObject?> objs);

        NucleusCollection<T> LoadAll<T>() where T : ICoreObject<T>, new();
        T? LoadByID<T>(Guid id) where T : ICoreObject<T>, new();
        NucleusCollection<T> LoadWhere<T>(Expression expression) where T : ICoreObject<T>, new();
        Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression expression, CancellationToken token = default) where T : ICoreObject<T>, new();
    }

    /// <summary>
    /// Ambient access point for the runtime, set when the DI container first resolves the object
    /// data manager (or explicitly via assignment). Enables the static CoreObject API.
    /// </summary>
    public static class NucleusRuntime
    {
        private static INucleusRuntime? _current;

        public static INucleusRuntime Current
        {
            get => _current ?? throw new InvalidOperationException(
                "Nucleus is not initialized. Call services.AddNucleus().AddNucleusData() plus a provider registration, " +
                "and resolve IObjectDataManager (e.g. via InitializeDataProviderAsync) before using the static entity API.");
            set => _current = value;
        }

        public static bool IsInitialized => _current != null;
    }
}
