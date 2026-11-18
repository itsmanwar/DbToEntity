using DbToEntity.Core.Models;

namespace DbToEntity.Tests.Infrastructure;

/// <summary>
/// Builds <see cref="TableMetadata"/> graphs the same way <c>PostgresMetadataProvider</c> does,
/// including sharing one <see cref="ForeignKeyMetadata"/> instance between the source's
/// ForeignKeys and the target's ReferencingForeignKeys.
/// </summary>
public sealed class SchemaBuilder
{
    public List<TableMetadata> Tables { get; } = new();

    public TableMetadata Table(string name, string className, params string[] columns) =>
        TableInSchema("public", name, className, columns);

    public List<EnumMetadata> Enums { get; } = new();

    public EnumMetadata Enum(string schema, string name, params string[] labels)
    {
        var e = new EnumMetadata { Schema = schema, Name = name, Labels = labels.ToList() };
        Enums.Add(e);
        return e;
    }

    /// <param name="columns">
    /// Column specs: <c>"name"</c> (int4, NOT NULL) or <c>"name:type"</c>. Type is a Postgres udt_name, optionally
    /// schema-qualified for user types (<c>"public.mood"</c>), with an optional length (<c>"varchar(50)"</c>) and a
    /// leading <c>_</c> for arrays. Append <c>?</c> for nullable, prefix <c>*</c> for primary key.
    /// Examples: <c>"*id"</c>, <c>"email:varchar(255)?"</c>, <c>"moods:public._mood"</c>.
    /// </param>
    public TableMetadata TableInSchema(string schema, string name, string className, params string[] columns)
    {
        var table = new TableMetadata { Schema = schema, Name = name, ClassName = className };
        foreach (var spec in columns)
        {
            var s = spec;
            var isPk = s.StartsWith('*');
            if (isPk) s = s[1..];
            var nullable = s.EndsWith('?');
            if (nullable) s = s[..^1];
            var parts = s.Split(':', 2);

            var type = parts.Length > 1 ? parts[1] : "int4";
            int? maxLength = null;
            var paren = type.IndexOf('(');
            if (paren >= 0)
            {
                maxLength = int.Parse(type[(paren + 1)..^1]);
                type = type[..paren];
            }
            var typeSchema = "pg_catalog";
            var dot = type.IndexOf('.');
            if (dot >= 0)
            {
                typeSchema = type[..dot];
                type = type[(dot + 1)..];
            }

            table.Columns.Add(new ColumnMetadata
            {
                Name = parts[0],
                DataType = type,
                TypeSchema = typeSchema,
                MaxLength = maxLength,
                IsNullable = nullable,
            });
            if (isPk) table.PrimaryKeys.Add(parts[0]);
        }
        if (table.PrimaryKeys.Count > 0) table.PrimaryKeyName = $"{name}_pkey";
        Tables.Add(table);
        return table;
    }

    public TableMetadata View(string name, string className, params string[] columns)
    {
        var view = Table(name, className, columns);
        view.Type = ObjectType.View;
        return view;
    }

    public ForeignKeyMetadata ForeignKey(string constraintName, TableMetadata source, string[] sourceColumns, TableMetadata target, string[] targetColumns)
    {
        var fk = new ForeignKeyMetadata
        {
            ConstraintName = constraintName,
            SourceTable = source.Name,
            SourceClassName = source.ClassName,
            SourceColumns = sourceColumns.ToList(),
            TargetSchema = target.Schema,
            TargetTable = target.Name,
            TargetClassName = target.ClassName,
            TargetClassNamePlural = target.ClassName,
            TargetColumns = targetColumns.ToList(),
        };
        source.ForeignKeys.Add(fk);
        target.ReferencingForeignKeys.Add(fk);
        return fk;
    }

    public ForeignKeyMetadata ForeignKey(string constraintName, TableMetadata source, string sourceColumn, TableMetadata target, string targetColumn = "id") =>
        ForeignKey(constraintName, source, new[] { sourceColumn }, target, new[] { targetColumn });
}
