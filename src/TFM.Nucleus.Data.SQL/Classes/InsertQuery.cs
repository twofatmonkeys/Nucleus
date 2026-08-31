using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    public class InsertQuery : Query, IQuery, IInsertQuery
    {
        public InsertQuery(string? tablename)
        {
            ArgumentNullException.ThrowIfNull(tablename, nameof(tablename));
            Type = QueryType.INSERT;
            TableName = tablename;

        }
        public InsertQuery(SqlTableSchema? tableSchema) : this(tableSchema?.TableName)
        {
            TableSchema = tableSchema;
        }
        public InsertQuery(SqlTableSchema? tableSchema, ICoreObject? obj) : this(tableSchema)
        {
            ArgumentNullException.ThrowIfNull(tableSchema, nameof(tableSchema));
            if (obj != null)
            {
                foreach (var prop in obj.ChangedProperties)
                    if (tableSchema.TryGetValueByProperty(prop.Key, out var col))
                        SetParameters.Add(new Parameter(col!, prop.Value));

                //add timestamp
                SetParameters.Add(new Parameter("Timestamp", ServerFunction.SysDateTimeOffset));
                //add child collection queries if applicable
                if (tableSchema!.ObjectSchema.HasNucleusChildCollections)
                {
                    foreach(var childColl in obj.NucleusChildCollections)
                        AddLinkedQuery(childColl.GetInsertUpdateQuery());
                }
            }
            else
            {
                throw new ArgumentNullException(nameof(obj), "Object cannot be null");
            }
        }

        public static InsertQuery Create<T>(IDataTableSchema tableSchema) where T : ICoreObject<T>, new()
        {
            var query = new InsertQuery(tableSchema.TableName);
            return query;
        }
        public static InsertQuery? Create<T>(SqlTableSchema tableSchema, T obj) where T : ICoreObject<T>, new() => Create(tableSchema, [obj]);
        public static InsertQuery? Create<T>(SqlTableSchema tableSchema, IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null || !objs.Any())
                return null;
            var first = true;
            InsertQuery? rootQuery = null;
            if (objs.Count() > 100)
            {
                var l = new ConcurrentBag<InsertQuery>();
                Parallel.ForEach(objs, iCoreObj =>
                {
                    var iq = new InsertQuery(tableSchema, iCoreObj);
                    l.Add(iq);
                });
                rootQuery = l.First();
                foreach (var iq in l.Skip(1))
                    rootQuery.LinkedQueries.Add(iq);
                return rootQuery;
            }
            else
            {
                for (int i = 0; i < objs.Count(); i++)
                {
                    var iCoreObj = objs.ElementAt(i);
                    if (first)
                    {
                        rootQuery = new InsertQuery(tableSchema, iCoreObj);
                        first = false;
                    }
                    else
                    {
                        rootQuery!.LinkedQueries.Add(new InsertQuery(tableSchema, iCoreObj));
                    }
                }
                return rootQuery;
            }
        }

        public override string BuildQuery()
        {
            var sb = new StringBuilder($"INSERT INTO {TableName}");
            sb.Append($"({string.Join(",", SetParameters.Select(x => x!.LeftOperand()))})");
            if (SetParameters.HasOutputParameters)
            {
                var outputParams = SetParameters.OutputParameters;
                sb.Append(" OUTPUT ");
                for (int i = 0; i < outputParams.Count; i++)
                {
                    sb.Append($"INSERTED.{outputParams[i].LeftOperand()} AS [{outputParams[i].Variable}]")
                        .Append(i < outputParams.Count - 1 ? " ," : "");
                }
            }
            sb.Append(" VALUES ");
            sb.Append($"({string.Join(",", SetParameters.Select(x => x!.SQLSetValue()))})");
            foreach (var q in LinkedQueries)
                sb.Append($";{Environment.NewLine}{q.BuildQuery()}");
            return sb.ToString();
        }

        IInsertQuery IInsertQuery.Create(string tablename) => new InsertQuery(tablename);

        //public override async Task ExecuteAsync(Context dataContext, CancellationToken token)
        //{
        //    var data = await dataContext.GetObjectDataAsync(this);
        //    if (SetParameters.HasOutputParameters)
        //        foreach (var dt in data)
        //            foreach (var col in dt.ColumnNames)
        //                AllQueryOutputParameters.FirstOrDefault(x => x.Variable == col)?
        //                    .SetReceivedOutput(dt.GetColumn(0, col));
        //}
    }
}
