using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    public class InsertQuery : Query, IQuery, IInsertQuery
    {
        public InsertQuery(IQueryRenderer renderer, string? tablename) : base(renderer)
        {
            ArgumentNullException.ThrowIfNull(tablename, nameof(tablename));
            Type = QueryType.INSERT;
            TableName = tablename;

        }
        public InsertQuery(IQueryRenderer renderer, IDataTableSchema? tableSchema) : this(renderer, tableSchema?.TableName)
        {
            TableSchema = tableSchema;
        }
        public InsertQuery(IQueryRenderer renderer, IDataTableSchema? tableSchema, ICoreObject? obj) : this(renderer, tableSchema)
        {
            ArgumentNullException.ThrowIfNull(tableSchema, nameof(tableSchema));
            if (obj != null)
            {
                AddWriteTerms(tableSchema, obj);
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

        public static InsertQuery Create<T>(IQueryRenderer renderer, IDataTableSchema tableSchema) where T : ICoreObject<T>, new()
        {
            var query = new InsertQuery(renderer, tableSchema.TableName);
            return query;
        }
        public static InsertQuery? Create<T>(IQueryRenderer renderer, IDataTableSchema tableSchema, T obj) where T : ICoreObject<T>, new() => Create(renderer, tableSchema, [obj]);
        public static InsertQuery? Create<T>(IQueryRenderer renderer, IDataTableSchema tableSchema, IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            Create(renderer, tableSchema, objs?.Cast<ICoreObject>());

        /// <summary>One insert per object, the rest linked from the first. Null when there are none.</summary>
        public static InsertQuery? Create(IQueryRenderer renderer, IDataTableSchema tableSchema, IEnumerable<ICoreObject>? objs)
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
                    var iq = new InsertQuery(renderer, tableSchema, iCoreObj);
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
                        rootQuery = new InsertQuery(renderer, tableSchema, iCoreObj);
                        first = false;
                    }
                    else
                    {
                        rootQuery!.LinkedQueries.Add(new InsertQuery(renderer, tableSchema, iCoreObj));
                    }
                }
                return rootQuery;
            }
        }

        public override string BuildQuery() => Renderer.Insert(this);

        IInsertQuery IInsertQuery.Create(string tablename) => new InsertQuery(Renderer, tablename);
    }
}
