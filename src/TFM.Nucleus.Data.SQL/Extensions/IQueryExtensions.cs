using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQL
{
    internal static class IQueryExtensions
    {
        internal static SqlParameter[]? GetSQLParams(this IQueryParameterCollection? coll) =>
            ToSqlParameters(ParameterBinding.Bind(coll));

        internal static SqlParameter[]? GetSQLParams(this IEnumerable<IQueryParameter?>? queries) =>
            ToSqlParameters(ParameterBinding.Bind(queries));

        internal static SqlParameter[]? GetSQLParams(this IQuery qry) =>
            ToSqlParameters(ParameterBinding.Bind(qry.AllQueryParameters));

        /// <summary>
        /// One SqlParameter per bound name; a multi-valued (IN) term has already been expanded to
        /// the {variable}_{index} names the renderer references.
        /// </summary>
        private static SqlParameter[] ToSqlParameters(IEnumerable<KeyValuePair<string, object?>> bound) =>
            [.. bound.Select(b => new SqlParameter($"@{b.Key}", b.Value))];

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
