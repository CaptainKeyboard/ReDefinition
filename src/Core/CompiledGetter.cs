using System;
using System.Linq.Expressions;
using System.Reflection;

namespace ReDefinition.Core
{
    // Another mod's field or property, read through a compiled getter where it is read
    // every frame: FieldInfo.GetValue and PropertyInfo.GetValue box a value type each
    // call, and cost a reflection call besides.
    internal static class CompiledGetter
    {
        // A getter of the field on an object of its declaring type (any object for a
        // static field), or null where there is no such field or it is not a T.
        internal static Func<object, T> Of<T>(FieldInfo field)
        {
            if (field == null || !typeof(T).IsAssignableFrom(field.FieldType)) return null;
            ParameterExpression target = Expression.Parameter(typeof(object), "target");
            Expression owner = field.IsStatic ? null : Expression.Convert(target, field.DeclaringType);
            Expression value = Expression.Field(owner, field);
            if (field.FieldType != typeof(T)) value = Expression.Convert(value, typeof(T));
            return Expression.Lambda<Func<object, T>>(value, target).Compile();
        }

        // The same for a property with a getter.
        internal static Func<object, T> Of<T>(PropertyInfo property)
        {
            MethodInfo getter = property != null ? property.GetGetMethod(true) : null;
            if (getter == null || getter.GetParameters().Length != 0 || !typeof(T).IsAssignableFrom(property.PropertyType))
                return null;
            ParameterExpression target = Expression.Parameter(typeof(object), "target");
            Expression owner = getter.IsStatic ? null : Expression.Convert(target, property.DeclaringType);
            Expression value = Expression.Property(owner, property);
            if (property.PropertyType != typeof(T)) value = Expression.Convert(value, typeof(T));
            return Expression.Lambda<Func<object, T>>(value, target).Compile();
        }
    }
}
