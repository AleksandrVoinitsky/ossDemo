using Npgsql;

internal static class PostgresConnectionPolicy
{
    internal const int MaximumPoolSize = 6;

    public static string Apply(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, MaximumPoolSize);
        builder.MinPoolSize = 0;
        return builder.ConnectionString;
    }
}
