using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SqlBulkCopyHelper;

public static class SqlBulkCopyHelperExtensions
{
    public static SqlBulkCopyHelper<T> MapAllPublicProperties<T>(this SqlBulkCopyHelper<T> helper) where T : class
    {
        var properties = typeof(T).GetProperties();
        if (properties.Length == 0)
        {
            throw new InvalidOperationException("No public properties found for type " + typeof(T).Name);
        }
        
        return MapProperties(helper, properties);
    }
    
    public static SqlBulkCopyHelper<T> MapProperties<T>(this SqlBulkCopyHelper<T> helper, IEnumerable<PropertyInfo> properties) where T : class
    {
        var props = properties.ToList();
        if (props.Count == 0)
        {
            throw new InvalidOperationException("At least one property is required");
        }
        
        var type = typeof(T);
        var instance = Expression.Parameter(type, type.Name);
        foreach (var property in props)
        {
            var prop = Expression.Property(instance, property);
            var convert = Expression.Convert(prop, typeof(object));
            var lambda = Expression.Lambda<Func<T, object>>(convert, instance).Compile();
            
            helper.Map(property.Name, lambda, property.PropertyType);
        }

        return helper;
    }

    public static SqlBulkCopyHelper<T> Map<T>(this SqlBulkCopyHelper<T> helper, string columnName) where T : struct
    {
        helper.Map(columnName, x => x);
        return helper;
    }
}