using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class NucleusTypeConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            if (sourceType == typeof(Guid))
                return true;
            else
                return false;
        }
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (value != null && destinationType == typeof(Guid))
                return ((CoreObject)value).ID;
            else
                return null;
        }
        private object? LoadObject(Type t, Guid id)
        {
            var method = t.GetMethod("Load");
            var invokeable = method?.MakeGenericMethod(t) ?? throw new Exception($"Unable to locate 'Load' method for object {t.Name}");
            return invokeable.Invoke(null, [id]);
        }
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value.GetType() == typeof(Guid))
            {
                Guid id = (Guid)value;
                return LoadObject(context?.GetType() ?? throw new NullReferenceException("Null TypeDescriptor context"), id);
            }
            return null;
        }
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        {
            if (destinationType == typeof(Guid))
                return true;
            else
                return false;
        }

    }
}
