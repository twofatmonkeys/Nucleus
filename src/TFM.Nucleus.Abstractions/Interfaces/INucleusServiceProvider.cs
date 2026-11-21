using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public interface INucleusServiceProvider
    {
        public ILogger? Logger { get; }

        /// <summary>A single provider, or null. It cannot say which provider stores a given type.</summary>
        [Obsolete("One provider cannot route a type registered under a rooted provider. Resolve IObjectDataManager, whose members route by type.")]
        public IDataProvider? DataProvider { get; }

        public IObjectSchemaProvider? SchemaProvider { get; }
    }
}
