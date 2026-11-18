using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DbToEntity.Core.Models;
using Humanizer;

namespace DbToEntity.Core
{
    /// <summary>
    /// Single source of truth for generated member names. Entity and DbContext generation
    /// must both read names from here so the emitted code agrees with itself.
    /// </summary>
    public static class EntityNaming
    {
        public static string SanitizeIdentifier(string name)
        {
            // 1. Replace invalid chars with underscore (keep alphanumeric)
            var clean = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");

            // 2. Pascalize (handles underscores by Capitalizing next char)
            var pascal = clean.Pascalize();

            // 3. Ensure valid start
            if (string.IsNullOrEmpty(pascal)) return "Property_" + System.Guid.NewGuid().ToString("N").Substring(0, 4); // Fallback
            if (char.IsDigit(pascal[0])) return "_" + pascal;

            return pascal;
        }

        /// <summary>Name of the scalar property generated for a column of the given class.</summary>
        public static string GetPropertyName(string columnName, string className)
        {
            var propName = SanitizeIdentifier(columnName);
            return propName == className ? propName + "Member" : propName;
        }

        /// <summary>
        /// Assigns <see cref="ForeignKeyMetadata.NavigationName"/> (reference navigation on the dependent)
        /// and <see cref="ForeignKeyMetadata.InverseNavigationName"/> (collection on the principal) for every FK.
        /// Requires ClassName, TargetClassName and SourceClassName to be resolved first.
        /// </summary>
        public static void ResolveNavigationNames(IEnumerable<TableMetadata> tables, bool pluralize = true)
        {
            string Plural(string name) => pluralize ? name.Pluralize() : name;

            var tableList = tables.ToList();
            var usedNames = tableList.ToDictionary(
                t => t,
                t => new HashSet<string>(t.Columns.Select(c => GetPropertyName(c.Name, t.ClassName))) { t.ClassName });

            // Pass 1: reference navigations (on the table that owns the FK).
            foreach (var table in tableList)
            {
                var used = usedNames[table];
                var perTarget = table.ForeignKeys.GroupBy(fk => fk.TargetClassName).ToDictionary(g => g.Key, g => g.Count());

                foreach (var fk in table.ForeignKeys)
                {
                    fk.IsOneToOne = HasUniqueColumns(table, fk.SourceColumns);
                    var name = perTarget[fk.TargetClassName] > 1 ? FkColumnBase(fk) : fk.TargetClassName;
                    fk.NavigationName = MakeUnique(name, "Nav", used);
                }
            }

            // Pass 2: inverse collections (on the referenced table). Runs after pass 1 so that
            // collisions with the table's own reference navigations are detected.
            foreach (var table in tableList)
            {
                var used = usedNames[table];
                var perSource = table.ReferencingForeignKeys.GroupBy(fk => fk.SourceClassName).ToDictionary(g => g.Key, g => g.Count());

                foreach (var fk in table.ReferencingForeignKeys)
                {
                    // One-to-one: a single reference, so the name stays singular (User.UserProfile).
                    var name = perSource[fk.SourceClassName] > 1
                        ? fk.SourceClassName + (fk.IsOneToOne ? FkColumnBase(fk) : Plural(FkColumnBase(fk)))
                        : (fk.IsOneToOne ? fk.SourceClassName : Plural(fk.SourceClassName));
                    fk.InverseNavigationName = MakeUnique(name, fk.IsOneToOne ? "Inverse" : "Collection", used);
                }
            }
        }

        /// <summary>
        /// Sets <see cref="EnumMetadata.ClassName"/>. Enums whose names clash across schemas get a schema prefix;
        /// a clash with an entity class name gets an <c>Enum</c> suffix. Run before <see cref="TypeMapper.ResolveColumnTypes"/>.
        /// </summary>
        public static void ResolveEnumNames(IEnumerable<EnumMetadata> enums, IEnumerable<TableMetadata> tables)
        {
            var enumList = enums.ToList();
            var used = new HashSet<string>(tables.Select(t => t.ClassName));
            var duplicates = enumList.GroupBy(e => SanitizeIdentifier(e.Name)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

            foreach (var e in enumList.OrderBy(e => e.Schema).ThenBy(e => e.Name))
            {
                var name = SanitizeIdentifier(e.Name);
                if (duplicates.Contains(name)) name = SanitizeIdentifier(e.Schema) + name;
                e.ClassName = MakeUnique(name, "Enum", used);
            }
        }

        /// <summary>C# member names for an enum's labels, in label order (deduplicated).</summary>
        public static List<string> GetEnumMemberNames(EnumMetadata e)
        {
            var used = new HashSet<string>();
            return e.Labels.Select(label => MakeUnique(SanitizeIdentifier(label), "_", used)).ToList();
        }

        /// <summary>
        /// True when the columns are, as a set, exactly the primary key or the columns of a unique index that is
        /// neither partial nor has expression parts. Only then can each principal have at most one dependent.
        /// </summary>
        public static bool HasUniqueColumns(TableMetadata table, IReadOnlyCollection<string> columns)
        {
            var set = new HashSet<string>(columns);
            if (table.PrimaryKeys.Count > 0 && set.SetEquals(table.PrimaryKeys)) return true;
            return table.Indexes.Any(i => i.IsUnique && !i.IsPartial && !i.HasExpressions && set.SetEquals(i.Columns));
        }

        // e.g. photo_file_id -> PhotoFile
        private static string FkColumnBase(ForeignKeyMetadata fk)
        {
            var baseName = SanitizeIdentifier(fk.SourceColumns.First());
            if (baseName.Length > 2 && baseName.EndsWith("Id", System.StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - 2);
            return baseName;
        }

        private static string MakeUnique(string name, string suffix, HashSet<string> used)
        {
            var candidate = name;
            if (used.Contains(candidate)) candidate = name + suffix;
            for (var i = 2; used.Contains(candidate); i++) candidate = name + suffix + i;
            used.Add(candidate);
            return candidate;
        }
    }
}
