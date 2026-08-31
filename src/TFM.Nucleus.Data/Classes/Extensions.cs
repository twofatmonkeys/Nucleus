using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    static class ArrayExtensions
    {
        public static int FindIndex<T>(this T[] array, Predicate<T> match)
        {
            return Array.FindIndex(array, match);
        }
        public static int IndexOf<T>(this T[] array, T value)
        {
            return Array.IndexOf(array, value);
        }
    }

    static class IntExtensions
    {
        public static (int quotient, int remainder) DivRem(this int value, int divisor)
        {
            if (divisor == 0)
                throw new DivideByZeroException("Divisor cannot be zero.");
            var quotient = Math.DivRem(value, divisor, out int remainder);
            return (quotient, remainder);
        }
    }
}
