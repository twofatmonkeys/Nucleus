using System.Runtime.CompilerServices;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.S3Library;

/// <summary>
/// A <see cref="DatabaseColumnAttribute"/> with an explicit length. Nucleus declares its length
/// constructor with an <c>int?</c> parameter, which C# does not allow at an attribute call site
/// (CS0181), so this wrapper takes a plain <c>int</c> and forwards it. Use -1 for nvarchar(max).
/// Always Unicode: Nucleus defaults a string to varchar(255), which would mangle anything outside
/// the database collation's code page, and alt text and variant JSON are exactly where every
/// character turns up. The same internal wrapper TFM.Nucleus.Bindery carries; it is internal there.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class TextColumnAttribute : DatabaseColumnAttribute
{
    public TextColumnAttribute(int length, [CallerMemberName] string? columnName = null)
        : base(DataType.Unicode, length, columnName)
    {
    }
}
