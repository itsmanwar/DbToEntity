using System.Collections.Generic;

namespace DbToEntity.Core.Models
{
    public class TableMetadata
    {
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty; // Resolved class name to avoid collisions
        public string Namespace { get; set; } = string.Empty; // Generated namespace
        public List<ColumnMetadata> Columns { get; set; } = new();
        public List<string> PrimaryKeys { get; set; } = new();
        public string? PrimaryKeyName { get; set; }
        public List<ForeignKeyMetadata> ForeignKeys { get; set; } = new();
        public List<ForeignKeyMetadata> ReferencingForeignKeys { get; set; } = new();
        public bool IsPartitioned { get; set; }
        public List<IndexMetadata> Indexes { get; set; } = new();
        public ObjectType Type { get; set; } = ObjectType.Table;
    }

    public enum ObjectType
    {
        Table,
        View,
        MaterializedView
    }

    public class ColumnMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty; // Postgres type, e.g. "text", "int4"
        public bool IsNullable { get; set; }
        public int? MaxLength { get; set; }
        public string? DefaultValue { get; set; }
    }

    public class ForeignKeyMetadata
    {
        public string ConstraintName { get; set; } = string.Empty;
        public string SourceTable { get; set; } = string.Empty;
        public string SourceClassName { get; set; } = string.Empty; // Resolved class name of source
        public List<string> SourceColumns { get; set; } = new();
        public string TargetSchema { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public string TargetClassName { get; set; } = string.Empty; // Resolved class name
        public string TargetClassNamePlural { get; set; } = string.Empty; // Resolved plural name
        public List<string> TargetColumns { get; set; } = new();
    }

    public class IndexMetadata
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public bool IsUnique { get; set; }
    }
}
