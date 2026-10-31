using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    public class UpdateQuery : Query, IQuery, IUpdateQuery
    {
        public UpdateQuery(IQueryRenderer renderer, string tablename, string idColumn, Guid idValue) : base(renderer)
        {
            Type = QueryType.UPDATE;
            TableName = tablename;
            Where.And(idColumn, idValue);
        }
        public UpdateQuery(IQueryRenderer renderer, string tablename, string idColumn, object idValue) : base(renderer)
        {
            Type = QueryType.UPDATE;
            TableName = tablename;
            Where.And(idColumn, idValue);
        }

        public UpdateQuery(IQueryRenderer renderer, IDataTableSchema? tableSchema, ICoreObject? obj) : base(renderer)
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

        public static UpdateQuery? Create<T>(IQueryRenderer renderer, IDataTableSchema tableSchema, T obj) where T : ICoreObject<T>, new() => Create(renderer, tableSchema, [obj]);
        public static UpdateQuery? Create<T>(IQueryRenderer renderer, IDataTableSchema tableSchema, IEnumerable<T>? objs) where T : ICoreObject<T>, new()
        {
            if (objs == null || !objs.Any())
                return null;
            var l = new ConcurrentBag<UpdateQuery>();
            if (objs.Count() > 100)
            {
                Parallel.ForEach(objs, iCoreObj =>
                {
                    l.Add(new UpdateQuery(renderer, tableSchema, iCoreObj));
                });
            }
            else
            {
                foreach (var iq in objs)
                    l.Add(new UpdateQuery(renderer, tableSchema, iq));
            }
            var rootQuery = l.First();
            foreach (var iq in l.Skip(1))
                rootQuery.LinkedQueries.Add(iq);
            return rootQuery;
        }

        public override string BuildQuery() => Renderer.Update(this);

        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, Guid idValue) => new UpdateQuery(Renderer, tablename, idColumn, idValue);

        IUpdateQuery IUpdateQuery.Create(string tablename, string idColumn, int idValue) => new UpdateQuery(Renderer, tablename, idColumn, idValue);
    }
}
