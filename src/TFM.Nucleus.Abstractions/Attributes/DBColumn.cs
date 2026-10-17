using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public class DatabaseColumnAttribute : Attribute, IDatabaseColumnAttribute
    {
        public DataType DataType { get; set; }

        #region IDatabaseColumnAttribute implementation
        DataType IDatabaseColumnAttribute.DataType => DataType;
        string IDatabaseColumnAttribute.ColumnName => ColumnName;
        int? IDatabaseColumnAttribute.DataLength => DataLength;
        bool IDatabaseColumnAttribute.FullTextIndex => FullTextIndex;
        bool IDatabaseColumnAttribute.NonClusteredIndex => NonClusteredIndex;
        bool IDatabaseColumnAttribute.Unique => Unique;
        bool IDatabaseColumnAttribute.IndexSortDESC => IndexSortDESC;
        bool IDatabaseColumnAttribute.PrimaryKey => PrimaryKey;
        bool IDatabaseColumnAttribute.Filestream => Filestream;
        int? IDatabaseColumnAttribute.DecimalPlaces => DecimalPlaces;
        eServerFunction IDatabaseColumnAttribute.SetValueOverride => SetValueOverride;
        bool IDatabaseColumnAttribute.ReturnOutputToProperty => ReturnOutputToProperty;
        #endregion
        
        public int? DataLength = null;
        public string ColumnName;
        public bool Filestream = false;
        public bool FullTextIndex = false;
        public bool NonClusteredIndex = false;
        public bool Unique = false;
        public bool IndexSortDESC = false;
        public bool PrimaryKey = false;
        public int? DecimalPlaces = null;

        /// <summary>
        /// The scale of a decimal column, settable in attribute syntax: <c>[DatabaseColumn(Scale = 6)]</c>.
        /// It writes <see cref="DecimalPlaces"/>, which C# cannot name in an attribute because it is
        /// nullable. Reads 0 while no scale is set, in which case the provider's default applies.
        /// </summary>
        public int Scale { get => DecimalPlaces ?? 0; set => DecimalPlaces = value; }

        public eServerFunction SetValueOverride = eServerFunction.NULL;
        public bool ReturnOutputToProperty = false;
        //public bool ForceTimestamp = false;
        public DatabaseColumnAttribute([CallerMemberName] string? columnName = null) : 
            this(DataType.NotSpecified, columnName) { }

        public DatabaseColumnAttribute(DataType datatype, [CallerMemberName] string? columnName = null) :
            this(datatype, null, columnName) { }

        public DatabaseColumnAttribute(int? datalength, [CallerMemberName] string? columnName = null) :
            this(DataType.NotSpecified, datalength, columnName) { }

        public DatabaseColumnAttribute(DataType datatype, int datalength, [CallerMemberName] string? columnName = null) :
            this(datatype, (int?)datalength, columnName){ }

        public DatabaseColumnAttribute(DataType datatype, int? datalength, [CallerMemberName] string? columnName = null)
        {
            DataType = datatype;
            DataLength = datalength;
            ColumnName = columnName ?? throw new ArgumentException("Column name cannot be null or empty.", nameof(columnName));
        }


        //public string MSSQLDataType(Type t)
        //{
        //    if (DataType == DataType.NotSpecified)
        //    {
        //        if (t == typeof(string))
        //            return "varchar";
        //        else if (t == typeof(int))
        //            return "int";
        //        else if (t == typeof(bool))
        //            return "bit";
        //        else if (t == typeof(DateTime))
        //            return "datetime2";
        //        else if (t == typeof(DateTimeOffset))
        //            return "datetimeoffset";
        //        else if (t == typeof(Guid))
        //            return "uniqueidentifier";
        //        else if (t == typeof(byte[]))
        //            return "varbinary";
        //        else if (t == typeof(decimal))
        //            return "decimal";
        //        else if (t == typeof(long))
        //            return "bigint";
        //        else if (t.Name.Contains("TFMJsonString"))
        //            return "varchar";
        //        else if (t.IsEnum)
        //            return "int";
        //        else if (typeof(ICoreObject).IsAssignableFrom(t))
        //            return "uniqueidentifier";
        //        else if (typeof(ISaveInDbAsString).IsAssignableFrom(t))
        //            return "varchar";


        //        throw new Exception("Unknown property type '" + t.Namespace + "." + t.Name + "' unable to select a  matching sql datatype.");
        //    }
        //    else if (DataType == DataType.Date)
        //        return "date";
        //    else if (DataType == DataType.Integer)
        //        return "int";
        //    else if (DataType == DataType.Unicode)
        //        return "nvarchar";
        //    throw new Exception("Unknown property type '" + DataType.ToString() + "' unable to select a  matching sql datatype.");
        //}
        //private string MSSQLDataSize(Type t)
        //{
        //    if (DataLength == -1)
        //        return "max";
        //    else if (DataLength > 0)
        //        return DataLength.ToString();
        //    else if (t.Name.Contains("TFMJsonString"))
        //        return "max";
        //    else
        //    {
        //        return MSSQLDefaultSize(MSSQLDataType(t));
        //    }
        //}
        //private string MSSQLDefaultSize(string datatype)
        //{
        //    if (datatype == "varchar")
        //        return "255";
        //    else if (datatype == "nvarchar")
        //        return "510";
        //    else if (datatype == "datetimeoffset")
        //        return "7";
        //    else if (datatype == "datetime2")
        //        return "7";
        //    else if (datatype == "int" || datatype == "date" || datatype == "bit" || datatype == "uniqueidentifier" || datatype == "datetime" || datatype == "bigint")
        //        return "";
        //    else if (datatype == "decimal")
        //        return $"19,{DecimalPlaces}";
        //    else if (datatype == "varbinary")
        //        return "max";
        //    throw new Exception("Unknown sql datatype '" + datatype + "' unable to select a  matching sql datatype.");

        //}

        //public string MSSQLConstructor(Type t)
        //{
        //    var constr = "[" + MSSQLDataType(t) + "]";
        //    if (!string.IsNullOrEmpty(MSSQLDataSize(t)))
        //        constr += "(" + MSSQLDataSize(t) + ")";
        //    if (Filestream && t == typeof(byte[]))
        //        constr += " FILESTREAM ";
        //    constr += " NULL, ";
        //    return constr;
        //}


    }
}
