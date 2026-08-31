using Microsoft.Data.SqlClient;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    public static class IQueryExtensions
    {
        internal static SqlParameter[]? GetSQLParams(this IQueryParameterCollection? coll)
        {
            var l = new List<SqlParameter>();
            if (coll == null)
                return [.. l];
            foreach (var p in coll.Where(x => x.UsesVariable))
                AddSqlParams(l, p);
            return [.. l];
        }
        internal static SqlParameter[]? GetSQLParams(this IEnumerable<IQueryParameter?>? queries)
        {
            var l = new List<SqlParameter>();
            if (queries == null)
                return [.. l];
            foreach (var p in queries.Where(x => x != null && x.UsesVariable))
                AddSqlParams(l, p!);
            return [.. l];
        }

        internal static SqlParameter[]? GetSQLParams(this IQuery qry)
        {
            var l = new List<SqlParameter>();
            if (qry.AllQueryParameters == null)
                return l.ToArray();
            foreach (var p in qry.AllQueryParameters.Where(x => x != null && x.UsesVariable))
                AddSqlParams(l, p!);
            return [.. l];
        }

        /// <summary>
        /// Binds a term's value; a multi-valued (IN) term expands to one parameter per element,
        /// matching the {variable}_{index} references the renderer emits.
        /// </summary>
        private static void AddSqlParams(List<SqlParameter> list, IQueryParameter p)
        {
            if (p is Parameter parameter && parameter.IsMultiValued())
            {
                var values = parameter.MultiValues();
                for (int i = 0; i < values.Count; i++)
                    list.Add(new SqlParameter($"@{p.Variable}_{i}", values[i] ?? DBNull.Value));
            }
            else
            {
                list.Add(new SqlParameter($"@{p.Variable}", p.Value));
            }
        }


        internal static bool NeedsSplitting(this IQuery qry, int paramLimit, out IQuery[]? splitQueries)
        {
            if (qry.AllQueryParameters == null || qry.AllQueryParameters.Count() < paramLimit)
            {
                splitQueries = null;
                return false;
            }
            else
            {
                splitQueries = qry.Split(paramLimit).ToArray();
                return true;
            }
        }

    }
}
