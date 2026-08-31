using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{

    /// <summary>An IQueryParameterCollection view over a parameter list (nulls filtered).</summary>
    public sealed class QueryParameterCollection : IQueryParameterCollection
    {
        private readonly List<IQueryParameter> _items;

        public QueryParameterCollection(IEnumerable<Parameter?> parameters) =>
            _items = [.. parameters.Where(p => p != null).Cast<IQueryParameter>()];

        public IQueryParameter this[int index] => _items[index];
        public int Count => _items.Count;
        public IEnumerator<IQueryParameter> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    public class ParameterCollection : List<Parameter?>, IEnumerable<Parameter?>
    {
        public bool HasOutputParameters => this.Any(x => x != null && x.IsOutputParameter);
        public List<OutputParameter> OutputParameters => this.Where(x => x != null && x.IsOutputParameter).Cast<OutputParameter>().ToList();

        /// <summary>The interface-typed view used where an IQueryParameterCollection is required.</summary>
        public IQueryParameterCollection AsQueryParameters() => new QueryParameterCollection(this);

        new public void Add(Parameter parameter) => base.Add(parameter);
        public void Add(string name, object value) => base.Add(new Parameter(name, value));

        public void Join(ParameterCollection items)
        {
            this.AddRange(items);
        }

        public ParameterCollection CopyAndCombine(ParameterCollection? parameters)
        {
            var l = new ParameterCollection(this);
            if (parameters != null)
                l.AddRange(parameters);
            return l;
        }
        public ParameterCollection()
        { }
        public ParameterCollection(ParameterCollection? parameters)
        {
            if (parameters != null)
                this.AddRange(parameters);
        }
        public ParameterCollection(IEnumerable<Parameter?> parameters)
        {
            if (parameters != null)
                this.AddRange(parameters);
        }
        public ParameterCollection(Parameter? param1, Parameter? param2 = null, Parameter? param3 = null, Parameter? param4 = null)
        {
            if(param1 != null)
                this.Add(param1);
            if (param2 != null)
                this.Add(param2);
            if (param3 != null)
                this.Add(param3);
            if (param4 != null)
                this.Add(param4);
        }
        public static ParameterCollection operator +(ParameterCollection? a, ParameterCollection? b)
        {
            a ??= new ParameterCollection();
            b ??= new ParameterCollection();
            var n = new ParameterCollection(a);
            n.AddRange(b);
            return n;
        }
        public static explicit operator ParameterCollection?(Parameter? v) => v?.CreateCollection();
        //public static explicit operator ParameterCollection(List<Parameter> p) => FromIEnumerable(p);

    }


}
