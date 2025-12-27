using System.Collections.Generic;
using System.Threading.Tasks;
using DbEntityGenerator.Core.Models;

namespace DbEntityGenerator.Core
{
    public interface IPostgresMetadataProvider
    {
        Task<List<TableMetadata>> GetTablesAsync(string connectionString, string schema, IEnumerable<string>? tableNames = null);
    }
}
