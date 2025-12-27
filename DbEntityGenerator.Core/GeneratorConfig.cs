using System.Collections.Generic;

namespace DbEntityGenerator.Core
{
    public class GeneratorConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string Schema { get; set; } = "public";
        public string Namespace { get; set; } = "MyNamespace";
        public string DbContextName { get; set; } = "AppDbContext";
        public string OutputDirectory { get; set; } = "./Output";
        public List<string> IncludedTables { get; set; } = new();
    }
}
