using System;
using System.Data.Common;
using System.Reflection;

namespace SqlBulkCopyHelper;

internal class OutputColumnDefinition<TEntity>
{
    /// <summary>
    /// Name of the column in the target table.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// Reads the value at the ordinal from the reader and sets it on the entity.
    /// </summary>
    public Action<TEntity, DbDataReader, int> SetValue { get; }

    private OutputColumnDefinition(string columnName, Action<TEntity, DbDataReader, int> setValue)
    {
        ColumnName = columnName;
        SetValue = setValue;
    }

    public static OutputColumnDefinition<TEntity> Create<TValue>(string columnName, Action<TEntity, TValue> setter)
    {
        var readValue = OutputValueReader.Create<TValue>(columnName);
        return new OutputColumnDefinition<TEntity>(columnName, (entity, reader, ordinal) => setter(entity, readValue(reader, ordinal)));
    }
}

internal static class OutputValueReader
{
    /// <summary>
    /// Nullable types (e.g. int?) and enums are read as their underlying type, since GetFieldValue doesn't support them.
    /// </summary>
    public static Func<DbDataReader, int, TValue> Create<TValue>(string columnName)
    {
        var nullableUnderlyingType = Nullable.GetUnderlyingType(typeof(TValue));
        var canBeNull = !typeof(TValue).IsValueType || nullableUnderlyingType is not null;
        var type = nullableUnderlyingType ?? typeof(TValue);
        var enumType = type.IsEnum ? type : null;
        var readType = enumType is not null ? Enum.GetUnderlyingType(enumType) : type;

        var readFieldValue = typeof(OutputValueReader)
            .GetMethod(nameof(ReadFieldValue), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(readType)
            .CreateDelegate<Func<DbDataReader, int, object>>();

        return (reader, ordinal) =>
        {
            if (reader.IsDBNull(ordinal))
            {
                return canBeNull
                    ? default!
                    : throw new InvalidOperationException($"Output column '{columnName}' is NULL and can't be set as {typeof(TValue).Name}. Use {typeof(TValue).Name}? instead.");
            }

            var value = readFieldValue(reader, ordinal);
            return (TValue)(enumType is not null ? Enum.ToObject(enumType, value) : value);
        };
    }

    private static object ReadFieldValue<T>(DbDataReader reader, int ordinal) => reader.GetFieldValue<T>(ordinal)!;
}
