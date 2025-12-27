using System.Collections.Generic;
using System.Threading.Tasks;
using DbToEntity.Core.Models;

namespace DbToEntity.Core
{
    public interface IPostgresMetadataProvider
    {
        Task<List<TableMetadata>> GetTablesAsync(string connectionString, string schema, IEnumerable<string>? tableNames = null);
    }
}
