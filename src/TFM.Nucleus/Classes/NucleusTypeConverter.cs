using System.ComponentModel;
using System.Globalization;

namespace TFM.Nucleus
{
    /// <summary>
    /// Converts an entity to its <see cref="CoreObject.ID"/>, which is how a linked entity is
    /// represented in a query parameter or a serialised value.
    /// </summary>
    /// <remarks>
    /// Conversion the other way is deliberately not supported. Turning an ID back into an entity is
    /// a database load, which needs the runtime and the target entity type; a TypeConverter is
    /// given neither reliably. Use <see cref="CoreObject.LoadByID{T}(Guid)"/> instead. The previous
    /// implementation attempted it by reflecting for a nonexistent static "Load" method on the
    /// descriptor context's own type, and threw for every input.
    /// </remarks>
    internal sealed class NucleusTypeConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
            destinationType == typeof(Guid);

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType != typeof(Guid))
                throw GetConvertToException(value, destinationType);
            return value is CoreObject entity ? entity.ID : null;
        }

        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => false;

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
            throw new NotSupportedException(
                $"Converting a {value?.GetType().Name ?? "null"} to an entity is not supported; " +
                $"use CoreObject.LoadByID<T>(Guid) to load an entity by ID.");
    }
}
