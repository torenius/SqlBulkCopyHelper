using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SqlBulkCopyHelper;

public static class SqlBulkCopyHelperExtensions
{
    /// <param name="helper">The SqlBulkCopyHelper</param>
    extension<T>(SqlBulkCopyHelper<T> helper) where T : class
    {
        /// <summary>
        /// Map all the public properties of the class.
        /// Mapping assumes that the property names have the same names as the target database column names.
        /// </summary>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
        /// <exception cref="InvalidOperationException">If the class have no public properties</exception>
        public SqlBulkCopyHelper<T> MapAllPublicProperties(Func<PropertyInfo, string>? columnNameFunc = null)
        {
            var properties = typeof(T).GetProperties();
            if (properties.Length == 0)
            {
                throw new InvalidOperationException("No public properties found for type " + typeof(T).Name);
            }
        
            return helper.MapProperties(properties, columnNameFunc);
        }

        /// <summary>
        /// Map the specified properties of the class.
        /// Mapping assumes that the property names have the same names as the target database column names.
        /// </summary>
        /// <param name="properties">The properties to map</param>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
        /// <exception cref="InvalidOperationException">If no properties were supplied</exception>
        public SqlBulkCopyHelper<T> MapProperties(IEnumerable<PropertyInfo> properties, Func<PropertyInfo, string>? columnNameFunc = null)
        {
            var props = properties.ToList();
            if (props.Count == 0)
            {
                throw new InvalidOperationException("At least one property is required");
            }

            columnNameFunc ??= propertyInfo => propertyInfo.Name;
        
            var type = typeof(T);
            var instance = Expression.Parameter(type, type.Name);
            foreach (var property in props)
            {
                helper.MapProperty(instance, property, columnNameFunc(property));
            }

            return helper;
        }

        /// <summary>
        /// Map a single property.
        /// If no columnName is provided. The code assumes it can use the property name.
        /// </summary>
        /// <param name="property">The property to map</param>
        /// <param name="columnName">The database column name</param>
        /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
        public SqlBulkCopyHelper<T> MapProperty(PropertyInfo property, string? columnName = null)
        {
            var type = typeof(T);
            var instance = Expression.Parameter(type, type.Name);
            return helper.MapProperty(instance, property, columnName);
        }

        private SqlBulkCopyHelper<T> MapProperty(ParameterExpression instance, PropertyInfo property, string? columnName = null)
        {
            if (string.IsNullOrWhiteSpace(columnName))
            {
                columnName = property.Name;
            }
        
            var prop = Expression.Property(instance, property);
            var convert = Expression.Convert(prop, typeof(object));
            var lambda = Expression.Lambda<Func<T, object>>(convert, instance).Compile();

            // Respects nullable reference type annotations, "string" is not null and "string?" is nullable.
            // If the property is not annotated (nullable context disabled) it's considered nullable.
            var nullable = new NullabilityInfoContext().Create(property).ReadState != NullabilityState.NotNull;

            helper.Map(columnName!, lambda, property.PropertyType, nullable);
        
            return helper;
        }
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