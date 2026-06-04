using System.Data.Common;
using System.Text;

namespace DBMonitor.Services.Query;

/// <summary>
/// Builds a parameterised WHERE clause from validated ColumnFilters.
/// Column identifiers must already be validated by the caller — this class
/// quotes them but does not re-validate against the live schema.
/// </summary>
internal static class WhereClauseBuilder
{
    /// <param name="filters">Pre-validated filter list.</param>
    /// <param name="quoter">Provider-specific identifier quoter.</param>
    /// <param name="likeSuffix">SQL appended after each LIKE expression, e.g. " ESCAPE '!'" for PostgreSQL.</param>
    /// <param name="likeEscaper">Escapes % _ and the escape char inside LIKE values.</param>
    public static (string Sql, List<(string Name, object Value)> Params) Build(
        IReadOnlyList<ColumnFilter> filters,
        Func<string, string> quoter,
        Func<string, string> likeEscaper,
        string likeSuffix = "")
    {
        if (filters.Count == 0)
            return (string.Empty, new List<(string, object)>());

        var conditions = new StringBuilder();
        var parameters = new List<(string Name, object Value)>();
        int pIdx = 0;

        foreach (var f in filters)
        {
            if (conditions.Length > 0) conditions.Append("\n  AND ");

            var col = quoter(f.Column);
            var pName = $"@fp{pIdx++}";

            switch (f.Op)
            {
                case FilterOp.Equals:
                    conditions.Append($"{col} = {pName}");
                    parameters.Add((pName, (object?)f.Value ?? DBNull.Value));
                    break;

                case FilterOp.NotEquals:
                    conditions.Append($"{col} <> {pName}");
                    parameters.Add((pName, (object?)f.Value ?? DBNull.Value));
                    break;

                case FilterOp.Contains:
                    conditions.Append($"{col} LIKE {pName}{likeSuffix}");
                    parameters.Add((pName, "%" + likeEscaper(f.Value ?? "") + "%"));
                    break;

                case FilterOp.StartsWith:
                    conditions.Append($"{col} LIKE {pName}{likeSuffix}");
                    parameters.Add((pName, likeEscaper(f.Value ?? "") + "%"));
                    break;

                case FilterOp.GreaterThan:
                    conditions.Append($"{col} > {pName}");
                    parameters.Add((pName, (object?)f.Value ?? DBNull.Value));
                    break;

                case FilterOp.LessThan:
                    conditions.Append($"{col} < {pName}");
                    parameters.Add((pName, (object?)f.Value ?? DBNull.Value));
                    break;

                case FilterOp.IsNull:
                    conditions.Append($"{col} IS NULL");
                    break;

                case FilterOp.IsNotNull:
                    conditions.Append($"{col} IS NOT NULL");
                    break;
            }
        }

        return ($"\nWHERE {conditions}", parameters);
    }

    public static void ApplyParams(DbCommand cmd, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }

    public static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
