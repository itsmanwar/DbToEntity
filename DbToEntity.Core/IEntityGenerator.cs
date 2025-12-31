using System.Collections.Generic;
using DbToEntity.Core.Models;

namespace DbToEntity.Core
{
    public struct GeneratedFile
    {
        public string FileName { get; set; }
        public string Content { get; set; }
    }

    public interface IEntityGenerator
    {
        GeneratedFile GenerateEntity(TableMetadata table, string namespaceName);
        GeneratedFile GenerateDbContext(List<TableMetadata> tables, string namespaceName, string dbContextName, bool separateBySchema = false);
        GeneratedFile UpdateDbContext(string existingCode, List<TableMetadata> tables, string dbContextName);
    }
}
