using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    public abstract class Query : IQuery
    {
        public string? TableName;
        public Type? ObjectType;
        public QueryType Type = QueryType.SELECT;
        public int Take = 0;
        public int Skip = 0;
        public List<string> Columns = [];
        public OrderByCollection OrderBy = new();
        public WhereCollection Where = new();
        //public string? WhereClause;
        public ParameterCollection SetParameters { get; set; } = [];
        public bool WithNoLock = false;
        public string[] Constraints = [];
        public List<Query> LinkedQueries => _linkedQueries;
        private readonly List<Query> _linkedQueries = [];
        public void AddLinkedQuery(IQuery? query)
        {
            if (query == null) return;
            if (query is not Query q)
                throw new InvalidQueryException("Child query must be of type Query");
            _linkedQueries.Add(q);
        }
        public IEnumerable<SqlTableSchema> DistinctTableSchemas()
        {
            var schemas = new HashSet<SqlTableSchema>();
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
            var groups = new Dictionary<SqlTableSchema, Query>();
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
        public bool UseConstraints => Constraints.Length > 0;
        internal SqlTableSchema? TableSchema;


        #region IQuery Implementation
        IEnumerable<IQueryParameter?>? IQuery.AllQueryParameters => AllQueryParameters;
        IEnumerable<IQueryParameter?>? IQuery.AllQueryOutputParameters => AllQueryOutputParameters;
        IEnumerable<IQueryParameter?>? IQuery.SetParameters => SetParameters;
        IEnumerable<IQuery> IQuery.Split(int maxParameters) => Split(maxParameters);
        IEnumerable<IQuery> IQuery.LinkedQueries => LinkedQueries;
        IEnumerable<IQuery> IQuery.FlattenLinkedQueries() => FlattenLinkedQueries();
        IDataTableSchema? IQuery.DataTableSchema => TableSchema;
        #endregion

        protected internal Query()
        {

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

            if (whereCollection.ExpressionType != WhereCollection.Type.Empty)
                Where.And(whereCollection);
        }
        public void AddWhereOr(WhereCollection whereCollection)
        {
            if (UseConstraints)
                foreach (var p in whereCollection.AllParameters())
                    if (p is not null && !Constraints.Contains(p.Name))
                        throw new Exception($"Parameter {p.Name} is not a valid constraint for this query.");

            if (whereCollection.ExpressionType != WhereCollection.Type.Empty)
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

        //public virtual Task ExecuteAsync()
        //{
        //    throw new NotImplementedException();
        //    //using(var dc = await DataContext.InitializeAsync(true)) 
        //    //    await ExecuteAsync(dc);
        //}
        //public virtual Task ExecuteAsync(Context dataContext) => ExecuteAsync(dataContext, CancellationToken.None);
        //public virtual Task ExecuteAsync(Context dataContext, CancellationToken token) => _ExecuteAsync(dataContext, token);

        //internal virtual async Task _ExecuteAsync(Context dataContext, CancellationToken token)
        //{
        //    await dataContext.SetObjectDataAsync(this, token);
        //    foreach (var op in AllQueryOutputParameters)
        //        op.SetReceivedOutput(op.ReceivedObject);
        //}

        public override string ToString() => BuildQuery();

        public abstract string BuildQuery();

        public List<Query> Split(int paramLimit)
        {
            if (NeedsSplitting(paramLimit, out var splitQueries))
                return splitQueries!;
            else
                return [this];
        }

        public bool NeedsSplitting(int paramLimit, out List<Query>? splitQueries)
        {
            if (GetParameterCount() < paramLimit)
            {
                splitQueries = null;
                return false;
            }
            else
            {
                splitQueries = new List<Query>();
                var tempList = new List<Query>();
                tempList.AddRange(LinkedQueries);
                LinkedQueries.Clear();
                var paramCount = GetParameterCount();
                var currentQuery = this;
                splitQueries.Add(this);
                foreach (var qry in tempList)
                {
                    var paramCountForQry = qry.GetParameterCount();
                    if (paramCount + paramCountForQry < paramLimit)
                    {
                        currentQuery.LinkedQueries.Add(qry);
                        paramCount += paramCountForQry;
                    }
                    else
                    {
                        splitQueries.Add(qry);
                        currentQuery = qry;
                        paramCount = paramCountForQry;
                    }
                }
                return true;
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
        //public ParameterCollection ChildQueryParameters => new(LinkedQueries.SelectMany(p => p.AllQueryParameters));
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

    public class InvalidQueryException : Exception
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

    public enum QueryType
    {
        SELECT = 0,
        UPDATE = 1,
        INSERT = 2,
        DELETE = 3,
    }
    public enum OrderByType
    {
        ASCENDING = 0,
        DESCENDING = 1,
    }
    public class OrderBy
    {
        public readonly OrderByType Type = OrderByType.ASCENDING;
        public readonly string Column;
        public bool IsEmpty { get => (Column == null || Column == string.Empty); }

        public OrderBy(System.Reflection.PropertyInfo property) : this(property, OrderByType.ASCENDING)
        { }
        public OrderBy(System.Reflection.PropertyInfo property, OrderByType type) : this(property.Name, type)
        { }
        public OrderBy(string column) : this(column, OrderByType.ASCENDING)
        { }
        public OrderBy(string column, OrderByType type)
        {
            Column = column;
            Type = type;
        }
        public string GetOrderByClause()
        {
            if (IsEmpty)
                return string.Empty;
            if (Type == OrderByType.ASCENDING)
                return $"{Column} ASC";
            else
                return $"{Column} DESC";
        }
        public override string ToString()
        {
            return GetOrderByClause();
        }
    }
    public class OrderByCollection
    {
        private List<OrderBy> _properties = new List<OrderBy>();
        public OrderByCollection()
        { }

        public bool IsEmpty => _properties.Count == 0;

        public StringBuilder? BuildOrderByStatement()
        {
            if (IsEmpty)
                return null;
            else
            {
                var sb = new StringBuilder("ORDER BY ");
                for (int i = 0; i < _properties.Count; i++)
                {
                    sb.Append($"{(i == 0 ? string.Empty : " ,")}{_properties[i]}");
                }
                return sb;
            }
        }

        public void Add(OrderBy property) => _properties.Add(property);
        public void Add(string column) => _properties.Add(new OrderBy(column));
        public void Add(string column, OrderByType type) => _properties.Add(new OrderBy(column, type));
        public void Add(PropertyInfo propertyInfo) => _properties.Add(new OrderBy(propertyInfo));
        public void Add(PropertyInfo propertyInfo, OrderByType type) => _properties.Add(new OrderBy(propertyInfo, type));

        public override string ToString() => $"ORDER BY {BuildOrderByStatement()}";
    }

    public class WhereCollection : IWhereCollection
    {
        private List<WhereCollection> _andCollection = [];
        private List<WhereCollection> _orCollection = [];
        private Parameter? _parameter;
        private string[] _constraints = [];

        public bool IsEmpty => ExpressionType == Type.Empty;
        public bool UseConstraints => _constraints.Length > 0;

        public Type ExpressionType => (_andCollection.Count > 0, _orCollection.Count > 0) switch
        {
            _ when _parameter != null => Type.Parameter,
            (true, false) => Type.And,
            (false, true) => Type.Or,
            (false, false) => Type.Empty,
            _ => throw new InvalidOperationException(
                $"WhereCollection has both AND ({_andCollection.Count}) and OR ({_orCollection.Count}) collections")
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
                case Type.Empty:
                    _parameter = parameter;
                    break;
                case Type.Parameter:
                    _andCollection.Add(_parameter!);
                    _parameter = null;
                    _andCollection.Add(parameter);
                    break;
                case Type.And:
                    _andCollection.Add(parameter);
                    break;
                case Type.Or:
                    WrapCurrentAs(isOr: true);
                    _andCollection.Add(parameter);
                    break;
            }
        }

        public void And(WhereCollection collection)
        {
            switch (ExpressionType)
            {
                case Type.Parameter:
                    _andCollection.Add(_parameter!);
                    _parameter = null;
                    break;
                case Type.Or:
                    WrapCurrentAs(isOr: true);
                    break;
            }
            _andCollection.Add(collection);
        }

        public void Or(Parameter parameter)
        {
            switch (ExpressionType)
            {
                case Type.Empty:
                    _parameter = parameter;
                    break;
                case Type.Parameter:
                    _orCollection.Add(_parameter!);
                    _parameter = null;
                    _orCollection.Add(parameter);
                    break;
                case Type.And:
                    WrapCurrentAs(isOr: false);
                    _orCollection.Add(parameter);
                    break;
                case Type.Or:
                    _orCollection.Add(parameter);
                    break;
            }
        }

        public void Or(WhereCollection collection)
        {
            switch (ExpressionType)
            {
                case Type.Parameter:
                    _orCollection.Add(_parameter!);
                    _parameter = null;
                    break;
                case Type.And:
                    WrapCurrentAs(isOr: false);
                    break;
            }
            _orCollection.Add(collection);
        }

        public string WhereClause() => ExpressionType switch
        {
            Type.Parameter => _parameter!.SQLWhereCondition(),
            Type.And => string.Join(" AND ", _andCollection.Select(c => c.WhereClause())),
            Type.Or => string.Join(" OR ", _orCollection.Select(c => $"({c.WhereClause()})")),
            Type.Empty => "",
            _ => throw new InvalidOperationException("Bad WhereParameter")
        };

        public ParameterCollection AllParameters() => ExpressionType switch
        {
            Type.Parameter => (ParameterCollection?)_parameter ?? [],
            Type.And => new(_andCollection.SelectMany(c => c.AllParameters())),
            Type.Or => new(_orCollection.SelectMany(c => c.AllParameters())),
            Type.Empty => [],
            _ => throw new InvalidOperationException("Bad WhereParameter")
        };

        public (ParameterCollection Parameters, string WhereClause) GetWhereClause()
            => (AllParameters(), WhereClause());

        public override string ToString() => WhereClause();

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

        public enum Type
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
