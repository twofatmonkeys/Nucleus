using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Data
{
    public interface IDatabaseColumnAttribute
    {
        public ColumnDataType DataType { get; }
        public string ColumnName { get; }
        public int? DataLength { get; }

        /// <summary>The column gets an index of its own.</summary>
        public bool Indexed { get; }

        /// <summary>The column's text is indexed for full-text search where the provider supports it.</summary>
        public bool FullTextSearch { get; }

        /// <summary>The column's index sorts descending.</summary>
        public bool IndexDescending { get; }

        /// <summary>The value is stored outside the row where the provider supports it.</summary>
        public bool ExternalStorage { get; }

        public bool Unique { get; }
        public bool PrimaryKey { get; }
        public int? DecimalPlaces { get; }
        public ServerFunctionKind SetValueOverride { get; }
        public bool ReturnOutputToProperty { get; }
    }
}
