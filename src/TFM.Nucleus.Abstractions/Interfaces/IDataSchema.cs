using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public interface IDataSchema
    {
        public IEnumerable<IDataTableSchema> GetTables();
        public bool IsCompatible(IEnumerable<IObjectSchema> objSchemas);
        public EDataSchemaStatus Initialize();
        public Task<EDataSchemaStatus> InitializeAsync(CancellationToken? cancellationToken);
        public EDataSchemaStatus Status { get; }
        public IDataTableSchema? GetTableSchema<T>();
        public IDataTableSchema? GetTableSchema(Type objectType);
    }

    public interface IDataTableSchema
    {
        public string TableName { get; }
        public IEnumerable<IDataColumnSchema> Columns { get; }
        public IDataColumnSchema? IdentityColumn { get; }
        public IObjectSchema ObjectSchema { get; }
        public IDataColumnSchema? FindByPropertyName(string propertyName);
        public IDataColumnSchema? FindByColumnName(string columnName);
        public IDataColumnSchema this[string columnName] { get; }

        public bool TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema);
        public bool TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema);

        //public bool IsCompatible(IObjectSchema objSchema);
    }

    public interface IDataColumnSchema
    {
        public string ColumnName { get; }
        public bool IsNullable { get; }
        public Type DataType { get; }
        public IPropertySchema PropertySchema { get; }
        public object? GetClrValue(object? value);

        //public bool IsCompatible(IPropertySchema propertySchema);

    }

    public enum EDataSchemaStatus
    {
        Ok,
        Error,
        NotCurrent,
        DatasourceNotFound,
        NotInitialized,
    }

}
