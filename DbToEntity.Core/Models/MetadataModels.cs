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
        public string? Comment { get; set; } // COMMENT ON TABLE/VIEW
        public string DbSetName { get; set; } = string.Empty; // set by ModelPipeline.Prepare
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
        public string DataType { get; set; } = string.Empty; // Postgres udt_name, e.g. "text", "int4", "_int4" (array), "mood" (enum)
        public string TypeSchema { get; set; } = "pg_catalog"; // Postgres udt_schema; identifies user-defined types such as enums
        public bool IsNullable { get; set; }
        public int? MaxLength { get; set; }
        public string? DefaultValue { get; set; }
        public string? Comment { get; set; } // COMMENT ON COLUMN

        // Set by TypeMapper.ResolveColumnTypes
        public string ClrType { get; set; } = string.Empty; // e.g. "int?", "string", "Mood[]"
        public string? ColumnType { get; set; } // explicit HasColumnType value when Npgsql's default store type would differ
    }

    public class EnumMetadata
    {
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new(); // in pg_enum.enumsortorder order
        public string ClassName { get; set; } = string.Empty; // set by EntityNaming.ResolveEnumNames
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
        public string NavigationName { get; set; } = string.Empty; // Reference navigation on the source entity (set by EntityNaming)
        public string InverseNavigationName { get; set; } = string.Empty; // Collection navigation on the target entity (set by EntityNaming)
        public ReferentialAction OnDelete { get; set; } = ReferentialAction.NoAction; // pg_constraint.confdeltype
        public bool IsOneToOne { get; set; } // set by EntityNaming.ResolveNavigationNames: source columns are unique
    }

    /// <summary>PostgreSQL ON DELETE action. NoAction is the database default.</summary>
    public enum ReferentialAction
    {
        NoAction,
        Restrict,
        Cascade,
        SetNull,
        SetDefault
    }

    public class IndexMetadata
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
        public bool IsUnique { get; set; }
        public bool IsPartial { get; set; } // has a WHERE predicate: uniqueness holds only for some rows
        public bool HasExpressions { get; set; } // some key parts are expressions, which Columns does not list
    }
}
