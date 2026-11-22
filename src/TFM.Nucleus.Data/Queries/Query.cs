using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// The neutral query model every provider shares: the table, columns, conditions, order,
    /// page and linked queries of one statement. It holds no dialect; <see cref="BuildQuery"/>
    /// asks the provider's <see cref="IQueryRenderer"/>, which the provider attached when it
    /// built the query.
    /// </summary>
    public abstract class Query : IQuery
    {
        public string? TableName;
        public Type? ObjectType;
        public QueryKind Type = QueryKind.Select;
        public int Take = 0;
        public int Skip = 0;
        public List<string> Columns = [];
        public OrderByCollection OrderBy = new();
        public WhereCollection Where = new();
        public ParameterCollection SetParameters { get; set; } = [];
        /// <summary>
        /// Lets a select read rows other transactions have not committed yet. Off by default, so
        /// a select reads committed data. SQL Server only: PostgreSQL never reads uncommitted rows
        /// and ignores it.
        /// </summary>
        /// <summary>The old name of <see cref="AllowDirtyRead"/>, and still where its value is kept.</summary>
        [Obsolete("Use AllowDirtyRead, which says what the flag means rather than how one engine spells it.")]
        public bool WithNoLock = false;

        /// <summary>
        /// The read does not wait for writers and may see rows a transaction has not committed.
        /// An engine whose readers never wait for writers ignores it.
        /// </summary>
        public bool AllowDirtyRead
        {
#pragma warning disable CS0618 // the obsolete field is the storage, so code compiled against it still works
            get => WithNoLock;
            set => WithNoLock = value;
#pragma warning restore CS0618
        }

        /// <summary>
        /// The read waits for rows other transactions are writing and then reads them, rather
        /// than reading the last committed version of each. For a check whose answer must count
        /// a row another transaction is about to commit, such as a delete's check for linked
        /// collection members. An engine whose readers always read the last committed version, and
        /// cannot wait for an uncommitted insert, ignores it.
        /// </summary>
        public bool WaitForPendingWrites { get; set; }

        public string[] Constraints = [];
        public List<Query> LinkedQueries => _linkedQueries;
        private readonly List<Query> _linkedQueries = [];

        /// <summary>The provider's renderer, which writes this query in its dialect.</summary>
        public IQueryRenderer Renderer { get; }

        /// <summary>The table this query reads or writes, when it was built from a schema.</summary>
        public IDataTableSchema? TableSchema;

        public void AddLinkedQuery(IQuery? query)
        {
            if (query == null) return;
            if (query is not Query q)
                throw new InvalidQueryException("Child query must be of type Query");
            _linkedQueries.Add(q);
        }

        /// <summary>
        /// The SET terms that write <paramref name="obj"/>: each changed property as its value,
        /// then each column [DatabaseColumn(SetValueOverride)] has the server compute, as that
        /// server function, whatever the client set. A column marked ReturnOutputToProperty
        /// becomes an <see cref="OutputParameter"/>, so the write reads the stored value back
        /// into the entity.
        /// </summary>
        protected void AddWriteTerms(IDataTableSchema tableSchema, ICoreObject obj)
        {
            foreach (var prop in obj.ChangedProperties)
                if (tableSchema.FindByPropertyName(prop.Key) is { } column && !column.PropertySchema.IsOverridden)
                    SetParameters.Add(WriteTerm(column, prop.Value, obj));
            foreach (var column in tableSchema.Columns.Where(c => c.PropertySchema.IsOverridden))
                SetParameters.Add(WriteTerm(column, ServerFunction.Get(column.PropertySchema.DatabaseColumnAttribute!.SetValueOverride), obj));
        }

        private static Parameter WriteTerm(IDataColumnSchema column, object? value, ICoreObject obj) =>
            ReadsBack(column)
                ? new OutputParameter(column, value, (_, args) => obj.AcceptServerValue(column.PropertySchema.Name, args.Value))
                : new Parameter(column, value);

        private static bool ReadsBack(IDataColumnSchema column) =>
            column.PropertySchema.DatabaseColumnAttribute?.ReturnOutputToProperty == true;
        public IEnumerable<IDataTableSchema> DistinctTableSchemas()
        {
            var schemas = new HashSet<IDataTableSchema>();
            var stack = new Stack<Query>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                if (q.TableSchema != null)
                    schemas.Add(q.TableSchema);
                foreach (var child in q.LinkedQueries)
                    stack.Push(child);
            }
            return schemas;
        }
        public int CountTableDistinctSchemas()
        {
            return DistinctTableSchemas().Count();
        }

        public IEnumerable<Query> FlattenLinkedQueries()
        {
            yield return this;
            foreach (var q in LinkedQueries)
                foreach (var child in q.FlattenLinkedQueries())
                    yield return child;
        }
        public List<Query> FlattenAndDetach()
        {
            var all = new List<Query>(CountLinkedQueries());
            var stack = new Stack<Query>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                foreach (var child in q.LinkedQueries)
                    stack.Push(child);
                q.LinkedQueries.Clear();
                q.ParentQuery = null;
                all.Add(q);
            }
            return all;
        }
        public int CountLinkedQueries()
        {
            int count = 1; // this
            foreach (var q in LinkedQueries)
                count += q!.CountLinkedQueries();
            return count;
        }
        public List<Query> GroupByTableSchema()
        {
            var detached = FlattenAndDetach();
            var groups = new Dictionary<IDataTableSchema, Query>();
            var result = new List<Query>();

            foreach (var q in detached)
            {
                if (q.TableSchema == null)
                    continue;

                if (groups.TryGetValue(q.TableSchema, out var root))
                {
                    root.LinkedQueries.Add(q);
                    q.ParentQuery = root;
                }
                else
                {
                    groups[q.TableSchema] = q;
                    result.Add(q);
                }
            }
            return result;
        }
        public string? LinkedColumn;
        public Query? ParentQuery;

        /// <summary>For a child collection query, the parent's collection property it fills.</summary>
        public string? ChildCollectionProperty;
        string? IQuery.ChildCollectionProperty => ChildCollectionProperty;

        /// <summary>
        /// For an included reference, the parent's <c>[LinkedObject]</c> property whose entities
        /// this query loads.
        /// </summary>
        public string? LinkedObjectProperty;
        string? IQuery.LinkedObjectProperty => LinkedObjectProperty;

        /// <summary>For an included linked collection, the parent's collection property whose members this query loads.</summary>
        public string? LinkedCollectionProperty;
        string? IQuery.LinkedCollectionProperty => LinkedCollectionProperty;

        /// <summary>
        /// The parent's column that <see cref="LinkedColumn"/> is matched against. Null for a
        /// child collection, whose rows match the parent's ID; an included reference matches its
        /// ID against the parent's reference column.
        /// </summary>
        public string? ParentKeyColumn;
        public bool UseConstraints => Constraints.Length > 0;


        #region IQuery Implementation
        IEnumerable<IQueryParameter?>? IQuery.AllQueryParameters => AllQueryParameters;
        IEnumerable<IQueryParameter?>? IQuery.AllQueryOutputParameters => AllQueryOutputParameters;
        IEnumerable<IQueryParameter?>? IQuery.SetParameters => SetParameters;
        IEnumerable<IQuery> IQuery.Split(int maxParameters) => Split(maxParameters);
        IEnumerable<IQuery> IQuery.LinkedQueries => LinkedQueries;
        IEnumerable<IQuery> IQuery.FlattenLinkedQueries() => FlattenLinkedQueries();
        IDataTableSchema? IQuery.DataTableSchema => TableSchema;
        #endregion

        protected Query(IQueryRenderer renderer)
        {
            Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        }
        public bool TryAddWhere(Parameter parameter)
        {
            if (Constraints.Contains(parameter.Name))
            {
                Where.And(parameter);
                return true;
            }
            return false;
        }
        public void AddWhere(Parameter parameter)
        {
            if (UseConstraints && !Constraints.Contains(parameter.Name))
                throw new Exception($"Parameter {parameter.Name} is not a valid constraint for this query.");
            Where.And(parameter);
        }
        public void AddWhere(WhereCollection whereCollection)
        {
            if (UseConstraints)
                foreach (var p in whereCollection.AllParameters())
                    if (p is not null && !Constraints.Contains(p.Name))
                        throw new Exception($"Parameter {p.Name} is not a valid constraint for this query.");

            if (whereCollection.ExpressionType != WhereCollection.NodeKind.Empty)
                Where.And(whereCollection);
        }
        public void AddWhereOr(WhereCollection whereCollection)
        {
            if (UseConstraints)
                foreach (var p in whereCollection.AllParameters())
                    if (p is not null && !Constraints.Contains(p.Name))
                        throw new Exception($"Parameter {p.Name} is not a valid constraint for this query.");

            if (whereCollection.ExpressionType != WhereCollection.NodeKind.Empty)
                Where.Or(whereCollection);
        }
        public void AddWhereOr(Parameter parameter)
        {
            if (UseConstraints && !Constraints.Contains(parameter.Name))
                throw new Exception($"Parameter {parameter.Name} is not a valid constraint for this query.");
            Where.Or(parameter);
        }

        public void AddChildQuery(Query childQuery, string linkedColumn)
        {
            childQuery.ParentQuery = this;
            childQuery.LinkedColumn = linkedColumn;
            LinkedQueries.Add(childQuery);
        }

        public override string ToString() => BuildQuery();

        /// <summary>The command text of this query in the attached renderer's dialect.</summary>
        public abstract string BuildQuery();

        public List<Query> Split(int paramLimit)
        {
            if (NeedsSplitting(paramLimit, out var splitQueries))
                return splitQueries!;
            else
                return [this];
        }

        /// <summary>
        /// Whether the chain needs more than <paramref name="paramLimit"/> parameters, and if so
        /// the chunks it splits into, each of at most that many. Detaches the linked queries.
        /// </summary>
        public bool NeedsSplitting(int paramLimit, out List<Query>? splitQueries)
        {
            if (GetParameterCount() <= paramLimit)
            {
                splitQueries = null;
                return false;
            }
            // Every statement of the chain in the order the unsplit command would run them, each
            // detached, then packed into chunks. A nested chain (an update chain linked from an
            // insert, a child's chain under its parent's) used to move as one unit, so a chunk
            // could still be over the limit; now only a single statement over it on its own can be.
            splitQueries = [];
            Query? current = null;
            var count = 0;
            foreach (var query in FlattenInOrder())
            {
                var own = query.GetParameterCount();
                if (current is null || count + own > paramLimit)
                {
                    splitQueries.Add(query);
                    current = query;
                    count = own;
                }
                else
                {
                    current.LinkedQueries.Add(query);
                    count += own;
                }
            }
            return true;
        }

        /// <summary>
        /// The chain's statements in the order it renders them (each query, then its linked
        /// queries in turn), each detached from its links and its parent query.
        /// </summary>
        private List<Query> FlattenInOrder()
        {
            var all = new List<Query>(CountLinkedQueries());
            Collect(this);
            return all;

            void Collect(Query query)
            {
                var linked = query.LinkedQueries.ToArray();
                query.LinkedQueries.Clear();
                query.ParentQuery = null;
                all.Add(query);
                foreach (var child in linked)
                    Collect(child);
            }
        }

        public ParameterCollection AllQueryParameters
        {
            get
            {
                var all = new ParameterCollection(Parameters);

                // Walk up to collect ancestor parameters
                var ancestor = ParentQuery;
                while (ancestor is not null)
                {
                    all.AddRange(ancestor.Parameters);
                    ancestor = ancestor.ParentQuery;
                }

                // Walk down to collect descendant parameters
                var stack = new Stack<Query>(LinkedQueries);
                while (stack.Count > 0)
                {
                    var q = stack.Pop();
                    all.AddRange(q.Parameters);
                    foreach (var child in q.LinkedQueries)
                        stack.Push(child);
                }
                return all;
            }
        }
        public ParameterCollection Parameters => SetParameters + Where.AllParameters();
        public IEnumerable<OutputParameter> AllQueryOutputParameters =>
            AllQueryParameters.Where(x => x != null && x.IsOutputParameter).Cast<OutputParameter>();

        public int GetParameterCount()
        {
            int count = Parameters.Count;
            foreach (var q in LinkedQueries)
                count += q.GetParameterCount();
            return count;
        }

        public Type?[] GetTypes()
        {
            var types = new List<Type?> { ObjectType };
            var stack = new Stack<Query>(LinkedQueries);
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                types.Add(q.ObjectType);
                foreach (var child in q.LinkedQueries)
                    stack.Push(child);
            }
            return [.. types];
        }

        IWhereCollection IQuery.NewWhereCollection() => new WhereCollection(Constraints);
        bool IQuery.TryAddWhere(IQueryParameter parameter) => TryAddWhere((Parameter)parameter);
        void IQuery.AddWhere(IQueryParameter parameter) => AddWhere((Parameter)parameter);
        void IQuery.AddWhere(IWhereCollection whereCollection) => AddWhere((WhereCollection)whereCollection);
        void IQuery.AddWhereOr(IQueryParameter parameter) => AddWhereOr((Parameter)parameter);
        void IQuery.AddWhereOr(IWhereCollection whereCollection) => AddWhereOr((WhereCollection)whereCollection);
        void IQuery.AddChildQuery(IQuery childQuery, string linkedColumn)
        {
            if (childQuery is not Query)
                throw new InvalidQueryException("Child query must be of type Query");
            AddChildQuery((Query)childQuery, linkedColumn);
        }

    }

    public class InvalidQueryException : TFM.Nucleus.NucleusException
    {
        public InvalidQueryException()
        {
        }

        public InvalidQueryException(string message)
            : base(message)
        {
        }

        public InvalidQueryException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>What a query does to its table.</summary>
    public enum QueryKind
    {
        Select = 0,
        Update = 1,
        Insert = 2,
        Delete = 3,
    }

    /// <summary>The direction one column of an order sorts in.</summary>
    public enum SortDirection
    {
        Ascending = 0,
        Descending = 1,
    }

    /// <summary>One column of an order, by name and direction. Renderers write it.</summary>
    public class OrderBy
    {
        public readonly SortDirection Type = SortDirection.Ascending;
        public readonly string Column;
        public bool IsEmpty { get => (Column == null || Column == string.Empty); }
        public bool IsDescending => Type == SortDirection.Descending;

        public OrderBy(System.Reflection.PropertyInfo property) : this(property, SortDirection.Ascending)
        { }
        public OrderBy(System.Reflection.PropertyInfo property, SortDirection type) : this(property.Name, type)
        { }
        public OrderBy(string column) : this(column, SortDirection.Ascending)
        { }
        public OrderBy(string column, SortDirection type)
        {
            Column = column;
            Type = type;
        }
        public override string ToString() => $"{Column} {Type}";
    }

    /// <summary>The columns a query orders by, in order.</summary>
    public class OrderByCollection : IReadOnlyCollection<OrderBy>
    {
        private readonly List<OrderBy> _properties = new List<OrderBy>();
        public OrderByCollection()
        { }

        public bool IsEmpty => _properties.Count == 0;
        public int Count => _properties.Count;

        public void Add(OrderBy property) => _properties.Add(property);
        public void Add(string column) => _properties.Add(new OrderBy(column));
        public void Add(string column, SortDirection type) => _properties.Add(new OrderBy(column, type));
        public void Add(PropertyInfo propertyInfo) => _properties.Add(new OrderBy(propertyInfo));
        public void Add(PropertyInfo propertyInfo, SortDirection type) => _properties.Add(new OrderBy(propertyInfo, type));

        public IEnumerator<OrderBy> GetEnumerator() => _properties.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join(", ", _properties);
    }

    /// <summary>
    /// A condition tree: empty, one term, or an AND or OR group of subtrees. Renderers walk it
    /// through <see cref="WhereRenderer"/>.
    /// </summary>
    public class WhereCollection : IWhereCollection
    {
        private List<WhereCollection> _andCollection = [];
        private List<WhereCollection> _orCollection = [];
        private Parameter? _parameter;
        private string[] _constraints = [];

        public bool IsEmpty => ExpressionType == NodeKind.Empty;
        public bool UseConstraints => _constraints.Length > 0;

        public NodeKind ExpressionType => (_andCollection.Count > 0, _orCollection.Count > 0) switch
        {
            _ when _parameter != null => NodeKind.Parameter,
            (true, false) => NodeKind.And,
            (false, true) => NodeKind.Or,
            (false, false) => NodeKind.Empty,
            _ => throw new InvalidOperationException(
                $"WhereCollection has both and-terms ({_andCollection.Count}) and or-terms ({_orCollection.Count})")
        };

        /// <summary>The term of a <see cref="NodeKind.Parameter"/> node; null otherwise.</summary>
        public Parameter? Term => _parameter;

        /// <summary>The operands of an <see cref="NodeKind.And"/> or <see cref="NodeKind.Or"/> node; empty otherwise.</summary>
        public IReadOnlyList<WhereCollection> Terms => ExpressionType switch
        {
            NodeKind.And => _andCollection,
            NodeKind.Or => _orCollection,
            _ => [],
        };

        public WhereCollection() { }

        internal WhereCollection(string[] constraints)
        {
            _constraints = constraints;
        }

        public WhereCollection(Parameter parameter)
        {
            _parameter = parameter;
        }

        public WhereCollection(ParameterCollection parameters, bool setTypeAsOr = false)
        {
            var target = setTypeAsOr ? _orCollection : _andCollection;
            parameters.ForEach(p => { if (p is not null) target.Add(p); });
        }

        public WhereCollection(WhereCollection parameters, bool setTypeAsOr = false)
        {
            if (setTypeAsOr)
                _orCollection.Add(parameters);
            else
                _andCollection.Add(parameters);
        }

        public void And(string name, object value) => And(new Parameter(name, value));

        public void And(Parameter parameter)
        {
            switch (ExpressionType)
            {
                case NodeKind.Empty:
                    _parameter = parameter;
                    break;
                case NodeKind.Parameter:
                    _andCollection.Add(_parameter!);
                    _parameter = null;
                    _andCollection.Add(parameter);
                    break;
                case NodeKind.And:
                    _andCollection.Add(parameter);
                    break;
                case NodeKind.Or:
                    WrapCurrentAs(isOr: true);
                    _andCollection.Add(parameter);
                    break;
            }
        }

        public void And(WhereCollection collection)
        {
            switch (ExpressionType)
            {
                case NodeKind.Parameter:
                    _andCollection.Add(_parameter!);
                    _parameter = null;
                    break;
                case NodeKind.Or:
                    WrapCurrentAs(isOr: true);
                    break;
            }
            _andCollection.Add(collection);
        }

        public void Or(Parameter parameter)
        {
            switch (ExpressionType)
            {
                case NodeKind.Empty:
                    _parameter = parameter;
                    break;
                case NodeKind.Parameter:
                    _orCollection.Add(_parameter!);
                    _parameter = null;
                    _orCollection.Add(parameter);
                    break;
                case NodeKind.And:
                    WrapCurrentAs(isOr: false);
                    _orCollection.Add(parameter);
                    break;
                case NodeKind.Or:
                    _orCollection.Add(parameter);
                    break;
            }
        }

        public void Or(WhereCollection collection)
        {
            switch (ExpressionType)
            {
                case NodeKind.Parameter:
                    _orCollection.Add(_parameter!);
                    _parameter = null;
                    break;
                case NodeKind.And:
                    WrapCurrentAs(isOr: false);
                    break;
            }
            _orCollection.Add(collection);
        }

        public ParameterCollection AllParameters() => ExpressionType switch
        {
            NodeKind.Parameter => (ParameterCollection?)_parameter ?? [],
            NodeKind.And => new(_andCollection.SelectMany(c => c.AllParameters())),
            NodeKind.Or => new(_orCollection.SelectMany(c => c.AllParameters())),
            NodeKind.Empty => [],
            _ => throw new InvalidOperationException("Bad WhereParameter")
        };

        /// <summary>Diagnostic only, in C# notation; renderers write the dialect.</summary>
        public override string ToString() => ExpressionType switch
        {
            NodeKind.Parameter => _parameter!.ToString(),
            NodeKind.And => string.Join(" && ", _andCollection.Select(c => $"({c})")),
            NodeKind.Or => string.Join(" || ", _orCollection.Select(c => $"({c})")),
            _ => "",
        };

        private void WrapCurrentAs(bool isOr)
        {
            var wrapped = Clone();
            _parameter = null;
            _andCollection = [];
            _orCollection = [];
            if (isOr)
                _andCollection.Add(wrapped);
            else
                _orCollection.Add(wrapped);
        }

        private WhereCollection Clone()
        {
            var clone = (WhereCollection)MemberwiseClone();
            clone._andCollection = new List<WhereCollection>(_andCollection);
            clone._orCollection = new List<WhereCollection>(_orCollection);
            return clone;
        }

        public static implicit operator WhereCollection(Parameter v) => new(v);
        public static implicit operator WhereCollection(ParameterCollection v) => new(v);

        /// <summary>What a node of a condition tree is: nothing, one term, or a group joined by AND or by OR.</summary>
        public enum NodeKind
        {
            Empty = 99,
            Parameter = 0,
            And = 1,
            Or = 2,
        }

        void IWhereCollection.AddAnd(IQueryParameter parameter) => And((Parameter)parameter);
        void IWhereCollection.AddOr(IQueryParameter parameter) => Or((Parameter)parameter);
        void IWhereCollection.AddWhereAnd(IWhereCollection whereCollection) => And((WhereCollection)whereCollection);
        void IWhereCollection.AddWhereOr(IWhereCollection whereCollection) => Or((WhereCollection)whereCollection);
    }
}
