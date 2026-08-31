using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    public class UpdateQuery : Query, IQuery, IUpdateQuery
    {
        public UpdateQuery(string tablename, string idColumn, Guid idValue)
        {
            Type = QueryType.UPDATE;
            TableName = tablename;
            Where.And(idColumn, idValue);
        }
        public UpdateQuery(string tablename, string idColumn, object idValue)
        {
            Type = QueryType.UPDATE;
            TableName = tablename;
            Where.And(idColumn, idValue);
        }

        public UpdateQuery(SqlTableSchema? tableSchema, ICoreObject? obj)
        {
            ArgumentNullException.ThrowIfNull(tableSchema, nameof(tableSchema));
            ArgumentNullException.ThrowIfNull(obj, nameof(obj));
            Type = QueryType.UPDATE;
            TableName = tableSchema.TableName;
            TableSchema = tableSchema;
            var idCol = tableSchema.IdentityColumn ?? throw new Exception("Table schema does not have an identity column defined");
            var id = obj.GetPropertyValue(idCol.PropertySchema.Name) ?? throw new Exception("Unable to retrieve identity value from ICoreObject<>");
            Where.And(idCol.ColumnName, id);
            foreach (var prop in obj.ChangedProperties)
            {
                var col = tableSchema?.FindByPropertyName(prop.Key);
                if (col != null)
                {
                    var val = prop.Value;
                    SetParameters.Add(new Parameter(col, val));
                }
            }
            //add timestamp
            SetParameters.Add(new Parameter("Timestamp", ServerFunction.SysDateTimeOffset));
            //add child collection queries if applicable
            if (tableSchema!.ObjectSchema.HasNucleusChildCollections)
            {
                foreach (var childColl in obj.NucleusChildCollections)
                    AddLinkedQuery(childColl.GetInsertUpdateQuery());
            }
        }

        public static UpdateQuery? Create<T>(SqlTableSchema tableSchema, T obj) where T : ICoreObject<T>, new() => Create(tableSchema, [obj]);
        public static UpdateQuery? Create<T>(SqlTableSchema tableSchema, IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null || !objs.Any())
                return null;
            var l = new ConcurrentBag<UpdateQuery>();
            if (objs.Count() > 100)
            {
                Parallel.ForEach(objs, iCoreObj =>
                {
                    l.Add(new UpdateQuery(tableSchema, iCoreObj));
                });
            }
            else
            {
                foreach (var iq in objs)
                    l.Add(new UpdateQuery(tableSchema, iq));
            }
            var rootQuery = l.First();
            foreach (var iq in l.Skip(1))
                rootQuery.LinkedQueries.Add(iq);
            return rootQuery;
            //for (int i = 0; i < objs.Count(); i++)
            //{
            //    if (first)
            //    {
            //        rootQuery = new UpdateQuery(tableSchema, objs.ElementAt(i));
            //        first = false;
            //    }
            //    else
            //    {
            //        rootQuery!.LinkedQueries.Add(new UpdateQuery(tableSchema, objs.ElementAt(i)));
            //    }
            //}
            //return rootQuery;
        }

        public override string BuildQuery()
        {
            var sb = new StringBuilder($"UPDATE {TableName} WITH (ROWLOCK) SET ");
            sb.Append(string.Join(",", SetParameters.Select(x => x!.SQLSetCondition())));
            if (SetParameters.HasOutputParameters)
            {
                var outputParams = SetParameters.OutputParameters;
                sb.Append(" OUTPUT ");
                for (int i = 0; i < outputParams.Count; i++)
                {
                    sb.Append(i < 1 ? $"({outputParams[i].LeftOperand()}" : $"{outputParams[i].SQLSetValue()}")
                        .Append(i < SetParameters.Count - 1 ? "," : ")");
                }
            }
            if (!Where.IsEmpty)
            {
                sb.Append($" WHERE {Where.WhereClause()}");
            }
            foreach (var q in LinkedQueries)
                sb.Append($" {q.BuildQuery()}");
            return sb.ToString();
        }

        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, Guid idValue) => new UpdateQuery(tablename, idColumn, idValue);

        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, int idValue) => new UpdateQuery(tablename, idColumn, idValue);

        //public override Task ExecuteAsync(Context dataContext, CancellationToken token)
        //{
        //    if (NeedsSplitting(2100, out var splitQueries))
        //    {
        //        var tasks = new List<Task>();
        //        foreach (var query in splitQueries)
        //        {
        //            tasks.Add(new Task(async () =>
        //            {
        //                using Context dc = await Context.InitializeAsync(dataContext, token);
        //                await query._ExecuteAsync(dc, token);
        //            }));     
        //        }
        //        return Task.WhenAll(tasks);
        //    }
        //    else
        //       return _ExecuteAsync(dataContext, token);
        //}
        //internal override async Task _ExecuteAsync(Context dataContext, CancellationToken token)
        //{
        //    var data = await dataContext.GetObjectDataAsync(this, token);
        //    if (SetParameters.HasOutputParameters)
        //        foreach (var dt in data)
        //            foreach (var col in dt.ColumnNames)
        //                AllQueryOutputParameters.FirstOrDefault(x => x.Variable == col)?
        //                    .SetReceivedOutput(dt.GetColumn(0, col));
        //}
    }
}
