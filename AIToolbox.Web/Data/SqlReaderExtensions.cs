using Microsoft.Data.SqlClient;

namespace AIToolbox.Web.Data;

internal static class SqlReaderExtensions
{
    public static string GetRequiredString(this SqlDataReader reader, string name)
    {
        var value = reader[name];
        return value is DBNull ? string.Empty : (string)value;
    }

    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var value = reader[name];
        return value is DBNull ? null : (string)value;
    }

    public static int GetRequiredInt32(this SqlDataReader reader, string name)
    {
        return (int)reader[name];
    }

    public static int? GetNullableInt32(this SqlDataReader reader, string name)
    {
        var value = reader[name];
        return value is DBNull ? null : (int)value;
    }

    public static bool GetRequiredBoolean(this SqlDataReader reader, string name)
    {
        return (bool)reader[name];
    }

    public static bool? GetNullableBoolean(this SqlDataReader reader, string name)
    {
        var value = reader[name];
        return value is DBNull ? null : (bool)value;
    }

    public static DateTime GetRequiredDateTime(this SqlDataReader reader, string name)
    {
        return (DateTime)reader[name];
    }

    public static DateTime? GetNullableDateTime(this SqlDataReader reader, string name)
    {
        var value = reader[name];
        return value is DBNull ? null : (DateTime)value;
    }
}
