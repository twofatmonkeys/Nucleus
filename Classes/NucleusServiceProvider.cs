using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public class NucleusServiceProvider : INucleusServiceProvider
    {
        public ILogger? Logger { get; private set; }
        public IDataProvider? DataProvider { get; private set; }
        public IObjectSchemaProvider? SchemaProvider { get; private set; }
        public NucleusOptions Options { get; private set; }


        public NucleusServiceProvider(
            IOptions<NucleusOptions>? options = null,
            IDataProvider? dataProvider = null,
            ILogger<NucleusServiceProvider>? logger = null,
            IObjectSchemaProvider? schemaProvider = null)
        {
            DataProvider = dataProvider;
            Logger = logger;
            SchemaProvider = schemaProvider;
            Options = options?.Value ?? new NucleusOptions();
        }

    }
}
