using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SqlBulkCopyHelper;

/// <summary>
/// Extension methods for mapping to a SqlBulkCopyHelper.
/// </summary>
public static class SqlBulkCopyHelperExtensions
{
    /// <param name="helper">The SqlBulkCopyHelper</param>
    extension<T>(SqlBulkCopyHelper<T> helper) where T : class
    {
        /// <summary>
        /// Map all the public properties of the class.
        /// Only instance properties with a public getter are mapped, static properties, indexers and write-only properties are skipped.
        /// Mapping assumes that the property names have the same names as the target database column names.
        /// </summary>
        /// <param name="columnNameFunc">How to map from PropertyInfo to the desired column name. Default just PropertyInfo.Name</param>
        /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
        /// <exception cref="InvalidOperationException">If the class have no public properties, or if it's a simple value like string or byte[]</exception>
        public SqlBulkCopyHelper<T> MapAllPublicProperties(Func<PropertyInfo, string>? columnNameFunc = null)
        {
            // Without this check a string would be mapped as its Length property
            if (helper.SchemaDefinitionMapping.ContainsKey(typeof(T)))
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} is a simple value and can't be mapped by its properties. " +
                    "Use .Map(\"ColumnName\") or connection.BulkInsertAsync(tableName, columnName, values) instead.");
            }

            var properties = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(IsMappable)
                .ToList();

            if (properties.Count == 0)
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
        /// <exception cref="ArgumentException">If any property is static, an indexer or has no public getter</exception>
        public SqlBulkCopyHelper<T> MapProperties(IEnumerable<PropertyInfo> properties, Func<PropertyInfo, string>? columnNameFunc = null)
        {
            var props = properties.ToList();
            if (props.Count == 0)
            {
                throw new InvalidOperationException("At least one property is required");
            }

            // Validate all properties first, so the helper is not left half mapped
            var notMappable = props.FirstOrDefault(x => !IsMappable(x));
            if (notMappable is not null)
            {
                throw new ArgumentException($"Property '{notMappable.Name}' can't be mapped. It must be an instance property with a public getter and not an indexer.", nameof(properties));
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
        /// <exception cref="ArgumentException">If the property is static, an indexer or has no public getter</exception>
        public SqlBulkCopyHelper<T> MapProperty(PropertyInfo property, string? columnName = null)
        {
            var type = typeof(T);
            var instance = Expression.Parameter(type, type.Name);
            return helper.MapProperty(instance, property, columnName);
        }

        private SqlBulkCopyHelper<T> MapProperty(ParameterExpression instance, PropertyInfo property, string? columnName = null)
        {
            if (!IsMappable(property))
            {
                throw new ArgumentException($"Property '{property.Name}' can't be mapped. It must be an instance property with a public getter and not an indexer.", nameof(property));
            }

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

    private static bool IsMappable(PropertyInfo property) =>
        property.GetMethod is { IsPublic: true, IsStatic: false }
        && property.GetIndexParameters().Length == 0;

    /// <summary>
    /// Map a single value. Just a shorthand for: .Map("ColumnName", x => x);
    /// Meant for lists of simple values like int, int?, string, Guid or byte[].
    /// </summary>
    /// <param name="helper">The SqlBulkCopyHelper</param>
    /// <param name="columnName">Database column name</param>
    /// <returns>The SqlBulkCopyHelper so you can continue with the builder pattern</returns>
    public static SqlBulkCopyHelper<T> Map<T>(this SqlBulkCopyHelper<T> helper, string columnName)
    {
        helper.Map(columnName, x => x);
        return helper;
    }
}