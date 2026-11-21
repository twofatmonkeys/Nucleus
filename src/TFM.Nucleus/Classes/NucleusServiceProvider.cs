using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public class NucleusServiceProvider : INucleusServiceProvider
    {
        public ILogger? Logger { get; private set; }
        [Obsolete("One provider cannot route a type registered under a rooted provider. Resolve IObjectDataManager, whose members route by type.")]
        public IDataProvider? DataProvider => _dataProvider;
        private readonly IDataProvider? _dataProvider;
        public IObjectSchemaProvider? SchemaProvider { get; private set; }
        public NucleusOptions Options { get; private set; }


        public NucleusServiceProvider(
            IOptions<NucleusOptions>? options = null,
            IDataProvider? dataProvider = null,
            ILogger<NucleusServiceProvider>? logger = null,
            IObjectSchemaProvider? schemaProvider = null)
        {
            _dataProvider = dataProvider;
            Logger = logger;
            SchemaProvider = schemaProvider;
            Options = options?.Value ?? new NucleusOptions();
        }

    }
}
