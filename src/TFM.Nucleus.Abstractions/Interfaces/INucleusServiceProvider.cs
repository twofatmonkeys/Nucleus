using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public interface INucleusServiceProvider
    {
        public ILogger? Logger { get; }

        public IDataProvider? DataProvider { get; }

        public IObjectSchemaProvider? SchemaProvider { get; }
    }
}
