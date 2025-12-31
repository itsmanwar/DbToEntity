using System.Collections.Generic;

namespace DbToEntity.Core.Models
{
    public class TableMetadata
    {
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<ColumnMetadata> Columns { get; set; } = new();
        public List<string> PrimaryKeys { get; set; } = new();
        public string? PrimaryKeyName { get; set; }
        public List<ForeignKeyMetadata> ForeignKeys { get; set; } = new();
        public List<ForeignKeyMetadata> ReferencingForeignKeys { get; set; } = new();
        public bool IsPartitioned { get; set; }
        public List<IndexMetadata> Indexes { get; set; } = new();
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
        public List<string> SourceColumns { get; set; } = new();
        public string TargetSchema { get; set; } = string.Empty;
        public string TargetTable { get; set; } = string.Empty;
        public List<string> TargetColumns { get; set; } = new();
    }

    public class IndexMetadata
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public bool IsUnique { get; set; }
    }
}
