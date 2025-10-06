using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SqlBulkCopyHelper;

public static class SqlBulkCopyHelperExtensions
{
    /// <summary>
    /// Map all the public properties of the class.
    /// Mapping assumes that the property names have the same names as the target database column names.
    /// </summary>
    /// <param name="helper">The SqlBulkCopyHelper</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    /// <exception cref="InvalidOperationException">If the class have no public properties</exception>
    public static SqlBulkCopyHelper<T> MapAllPublicProperties<T>(this SqlBulkCopyHelper<T> helper) where T : class
    {
        var properties = typeof(T).GetProperties();
        if (properties.Length == 0)
        {
            throw new InvalidOperationException("No public properties found for type " + typeof(T).Name);
        }
        
        return MapProperties(helper, properties);
    }

    /// <summary>
    /// Map the specified properties of the class.
    /// Mapping assumes that the property names have the same names as the target database column names.
    /// </summary>
    /// <param name="helper">The SqlBulkCopyHelper</param>
    /// <param name="properties">The properties to map</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    /// <exception cref="InvalidOperationException">If no properties were supplied</exception>
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
            MapProperty(helper, instance, property);
        }

        return helper;
    }

    /// <summary>
    /// Map a single property.
    /// If no columnName is provided. The code assumes it can use the property name.
    /// </summary>
    /// <param name="helper">The SqlBulkCopyHelper</param>
    /// <param name="property">The property to map</param>
    /// <param name="columnName">The database column name</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public static SqlBulkCopyHelper<T> MapProperty<T>(this SqlBulkCopyHelper<T> helper, PropertyInfo property, string? columnName = null) where T : class
    {
        var type = typeof(T);
        var instance = Expression.Parameter(type, type.Name);
        return MapProperty(helper, instance, property);
    }
    
    private static SqlBulkCopyHelper<T> MapProperty<T>(SqlBulkCopyHelper<T> helper, ParameterExpression instance, PropertyInfo property, string? columnName = null) where T : class
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = property.Name;
        }
        
        var prop = Expression.Property(instance, property);
        var convert = Expression.Convert(prop, typeof(object));
        var lambda = Expression.Lambda<Func<T, object>>(convert, instance).Compile();

        helper.Map(columnName!, lambda, property.PropertyType);
        
        return helper;
    }

    /// <summary>
    /// Map a single value. Just a shorthand for: .Map("ColumnName", x => x);
    /// </summary>
    /// <param name="helper">The SqlBulkCopyHelper</param>
    /// <param name="columnName">Database column name</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public static SqlBulkCopyHelper<T> Map<T>(this SqlBulkCopyHelper<T> helper, string columnName) where T : struct
    {
        helper.Map(columnName, x => x);
        return helper;
    }
}