using System.Collections.Generic;

namespace DbToEntity.Core
{
    public class GeneratorConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string? Schema { get; set; } // null: all schemas
        public string Namespace { get; set; } = "MyNamespace";
        public string DbContextName { get; set; } = "AppDbContext";
        public string OutputDirectory { get; set; } = "./Output";
        public List<string> IncludedTables { get; set; } = new();
        public bool SeparateBySchema { get; set; }
        public bool Pluralize { get; set; } = true; // false: --no-pluralize
        public bool FluentOnly { get; set; } // --fluent-only
        public List<string> ExcludedTables { get; set; } = new(); // db2entity.json excludeTables (glob patterns)
        public Dictionary<string, string> ClassNames { get; set; } = new(); // db2entity.json classNames
    }
}
