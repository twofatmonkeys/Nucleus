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

#pragma warning disable CS0618 // The neutral members read the obsolete ones by default, for implementers written before them.
        /// <summary>The column gets an index of its own.</summary>
        public bool Indexed => NonClusteredIndex;

        /// <summary>The column's text is indexed for full-text search where the provider supports it.</summary>
        public bool FullTextSearch => FullTextIndex;

        /// <summary>The column's index sorts descending.</summary>
        public bool IndexDescending => IndexSortDESC;

        /// <summary>The value is stored outside the row where the provider supports it.</summary>
        public bool ExternalStorage => Filestream;
#pragma warning restore CS0618

        [Obsolete("Use FullTextSearch.")]
        public bool FullTextIndex { get; }
        [Obsolete("Use Indexed.")]
        public bool NonClusteredIndex { get; }
        public bool Unique { get; }
        [Obsolete("Use IndexDescending.")]
        public bool IndexSortDESC { get; }
        public bool PrimaryKey { get; }
        [Obsolete("Use ExternalStorage.")]
        public bool Filestream { get; }
        public int? DecimalPlaces { get; }
        public eServerFunction SetValueOverride { get; }
        public bool ReturnOutputToProperty { get; }
    }
}
