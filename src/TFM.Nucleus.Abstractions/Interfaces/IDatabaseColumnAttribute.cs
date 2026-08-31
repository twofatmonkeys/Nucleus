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
        public DataType DataType { get; }
        public string ColumnName { get; }
        public int? DataLength { get; }
        public bool FullTextIndex { get; }
        public bool NonClusteredIndex { get; }
        public bool Unique { get; }
        public bool IndexSortDESC { get; }
        public bool PrimaryKey { get; }
        public bool Filestream { get; }
        public int? DecimalPlaces { get; }
        public eServerFunction SetValueOverride { get; }
        public bool ReturnOutputToProperty { get; }
    }

    //public enum DataType
    //{
    //    NotSpecified,
    //    Unicode,
    //    Date,
    //    Integer,
    //    Binary,
    //    LargeBinary,
    //}
}
