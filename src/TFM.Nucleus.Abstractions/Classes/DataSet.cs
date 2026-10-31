using System;
using System.Data;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public class DataSet : IDisposable
    {
        private object?[][][] _primaryArray = new object?[10000][][];
        private int _nextRow = 0;
        private bool _disposedValue;

        public readonly string Name;
        public readonly int ColumnCount;

        /// <summary>
        /// The name each value binds to on the entity. A data set read through a table schema is
        /// labelled by property name, which is the column name unless [DatabaseColumn] names the
        /// column otherwise.
        /// </summary>
        public readonly string[] ColumnNames;
        public readonly IDataTableSchema? DataTableSchema = null;


        private ref object?[] ArrayRef(int i)
        {
            var (left, right) = ArrayIndex(i);
            return ref _primaryArray[left][right];
        }
        private ref object?[] CheckedArrayRef(int i)
        {
            if (i > RowCount)
                throw new IndexOutOfRangeException($"{i} is greater than the number of rows - {RowCount}");
            var (left, right) = ArrayIndex(i);
            return ref _primaryArray[left][right];
        }
        private object?[] ArrayAt(int i)
        {
            var (left, right) = ArrayIndex(i);
            return _primaryArray[left][right];
        }
        private (int left, int right) ArrayIndex(int i) => i.DivRem(10000);

        public int RowCount => _nextRow;
        public bool HasData => RowCount > 0;

        /// <summary>The unpaged total row count when the query requested WithTotalCount; otherwise null.</summary>
        public long? TotalRowCount { get; set; }

        /// <summary>
        /// The rows of the query's child collection queries: entry i holds the rows of the
        /// query's LinkedQueries[i], and carries that query's own children the same way.
        /// </summary>
        public List<DataSet> LinkedDataSets { get; } = [];

        public ref object?[] GetNewRow()
        {
            var (left, right) = ArrayIndex(_nextRow);
            _ = right > 0 ? _nextRow++ : NewArray(left);
            _primaryArray[left][right] = new object?[ColumnCount];
            return ref _primaryArray[left][right];
        }
        public void FillNext(object?[] objs)
        {
            if (objs == null || objs.Length != ColumnCount)
                throw new ArgumentException($"Object array length should equal {ColumnCount}");
            var (left, right) = ArrayIndex(_nextRow);
            _ = right > 0 ? _nextRow++ : NewArray(left);
            _primaryArray[left][right] = objs;
        }
        private int NewArray(int i)
        {
            _primaryArray[i] = new object?[10000][];
            return _nextRow++;
        }

        public static DataSet Initialize(IDataTableSchema? dataTableSchema) => new(dataTableSchema);

        public static DataSet Initialize(string name, string[] columns) => new(name, columns);

        private DataSet(string name, string[] columns, IDataTableSchema? dataTableSchema = null)
        {
            Name = name;
            ColumnNames = columns;
            ColumnCount = ColumnNames.Length;
            _primaryArray[0] = new object?[10000][];
            DataTableSchema = dataTableSchema;
        }

        private DataSet(IDataTableSchema? dataTableSchema)
        {
            if (dataTableSchema == null)
                throw new ArgumentNullException(nameof(dataTableSchema), "DataTableSchema cannot be null in this constructor");
            Name = dataTableSchema!.TableName;
            ColumnNames = [.. dataTableSchema!.Columns.Select(x => x.PropertySchema.Name)];
            ColumnCount = ColumnNames.Length;
            _primaryArray[0] = new object?[10000][];
            DataTableSchema = dataTableSchema;
        }

        public NucleusCollection<T> BuildCollection<T>() where T : ICoreObject<T>, new()
        {
            if (RowCount == 0)
                return new NucleusCollection<T>();
            if (DataTableSchema == null)
                throw new InvalidOperationException("DataSet does not have a DataTableSchema associated with it.");
            return [.. BindRows<T>()];

        }

        public IEnumerable<T> BindRows<T>() where T : ICoreObject<T>, new()
        {
            if (RowCount == 0)
                return [];
            var objList = new List<T>();
            for (int i = 0; i < RowCount; i++)
            {
                T obj = new();
                var row = GetRow(i); 
                foreach(var col in row.Values)
                {
                    obj.BindValue(col.ColumnName, col.Value);
                }
                
                objList.Add(obj);
            }
            return objList;
        }

        public IEnumerable<T> Bind<T>() where T : ICoreObject<T>, new()
        {
            if (RowCount == 0)
                return [];
            var objList = new List<T>();
            for (int i = 0; i < RowCount; i++)
            {
                T obj = new();
                var row = this[i];
                for (int ii = 0; ii < ColumnCount; ii++)
                {
                    obj.BindValue(ColumnNames[ii], row[ii] ?? null);
                }
                objList.Add(obj);
            }
            return objList;
        }


        public ref struct Column
        {
            private ref DataSet DataSet;
            private readonly int ColumnIndex;
            public string Name => DataSet.ColumnNames[ColumnIndex];

            internal Column(int columnIndex, ref DataSet dataSet)
            {
                DataSet = ref dataSet;
                ColumnIndex = columnIndex;
            }

            public override string ToString()
            {
                return $"{Name}";
            }
        }
        public override string ToString()
        {
            return $"{Name} - {RowCount} Rows";
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    for (int i = 0; i < _primaryArray.Length; i++)
                    {
                        if (_primaryArray[i] == null)
                            break;
                        for (int j = 0; j < _primaryArray[i].Length; j++)
                            _primaryArray[i][j] = null!;
                        _primaryArray[i] = null!;
                    }
                    _primaryArray = null!;
                    _nextRow = 0;
                }
                _disposedValue = true;
            }
        }
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public ref object?[] this[int i] => ref CheckedArrayRef(i);
        public ref object? GetColumn(int row, string column) =>
            ref CheckedArrayRef(row)[ColumnNames.IndexOf(column)];
        public DataSetRow GetRow(int rowIndex) => new DataSetRow(rowIndex, this);
    }

    public class DataSetRow
    {
        //private ref object[] _data;
        private DataSet DataSet;
        private readonly int RowIndex;

        internal DataSetRow(int rowIndex, DataSet dataSet)
        {
            DataSet =  dataSet;
            RowIndex = rowIndex;
        }
        //protected internal Row(int rowIndex, in TFMDataSet dataSet, int columnCount)
        //{
        //    RowIndex = rowIndex;
        //    DataSet = dataSet;
        //    var columns = new Column[columnCount];
        //    for (int i = 0; i < columnCount; i++)
        //        columns[i] = new Column(i, this);
        //    Columns = columns;
        //}
        //protected internal void AddToArray(Row[] rowArray, int rowIndex)
        //{
        //    RowIndex = rowIndex;
        //    rowArray[rowIndex] = this;
        //}
        public DataSetValue[] Values
        {
            get
            {
                var values = new DataSetValue[DataSet.ColumnCount];
                for (int i = 0; i < DataSet.ColumnCount; i++)
                    values[i] = new DataSetValue(RowIndex, i, DataSet);
                return values;
            }
        }

        public DataSetValue this[int i] => new DataSetValue(RowIndex, i, DataSet);
        //public ref object? this[string columnName] => ref DataSet.GetColumn(RowIndex, columnName);

        public override string ToString()
        {
            return $"{DataSet.Name} - Row {RowIndex}";
        }
    }

    public class DataSetValue
    {
        private readonly int RowIndex;
        private readonly int ColumnIndex;
        private DataSet DataSet;

        public ref object? Value => ref DataSet[RowIndex][ColumnIndex];
        public ref string ColumnName => ref DataSet.ColumnNames[ColumnIndex];

        internal DataSetValue(int rowIndex, int columnIndex,DataSet dataSet)
        {
            RowIndex = rowIndex;
            ColumnIndex = columnIndex;
            DataSet = dataSet;
        }

        public override string ToString()
        {
            return $"({ColumnName}){Value ?? "null"}";
        }

    }
}
