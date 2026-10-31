using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data.SQL
{
    internal static class SQLParameterExtensions
    {
        public static void TryAddRange(this SqlParameterCollection coll, SqlParameter[]? values)
        {
            if (values != null)
                coll.AddRange(values);
        }

        


    }
}
