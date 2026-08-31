using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public enum Operator
    {
        Equals = 0,
        Like = 1,
        LikeCaseSensitive = 2,
        GreaterThan = 3,
        LessThan = 4,
        GreaterThanOrEqualTo = 5,
        LessThanOrEqualTo = 6,
        NotEquals = 7,
        Contains = 8,
        ContainsCaseSensitive = 9,
        In = 10,              // for List.Contains() -> IN clause
        StartsWith = 11,      // for string.StartsWith()
        EndsWith = 12,        // for string.EndsWith()
        EqualsIgnoreCase = 13,
        Empty = 99,
    }
}
