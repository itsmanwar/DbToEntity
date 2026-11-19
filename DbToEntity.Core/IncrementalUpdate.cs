using System;
using System.Collections.Generic;
using System.Linq;
using DbToEntity.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbToEntity.Core
{
    /// <summary>Planning for the <c>update</c> command.</summary>
    public static class IncrementalUpdate
    {
        /// <summary>
        /// Names of the tables/views an existing generated DbContext maps (first argument of each
        /// <c>ToTable("…")</c> / <c>ToView("…")</c>). Together with the requested tables this is the project's
        /// table set: relationships to tables outside it are not generated, so navigations never point at
        /// classes that don't exist.
        /// </summary>
        public static List<string> ReadMappedTableNames(string dbContextCode)
        {
            var root = CSharpSyntaxTree.ParseText(dbContextCode).GetRoot();
            return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "ToTable" or "ToView" })
                .Select(i => i.ArgumentList.Arguments.FirstOrDefault()?.Expression)
                .OfType<LiteralExpressionSyntax>()
                .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
                .Select(l => l.Token.ValueText)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// The requested tables plus every table whose generated code depends on them: tables related to a
        /// requested table now (FKs in either direction) and tables it was related to before (navigation types
        /// in its previous entity file). Without the latter, a dropped FK would leave a stale navigation on the
        /// other side. <paramref name="readPreviousEntity"/> returns the old file content, or null if none.
        /// </summary>
        public static List<TableMetadata> SelectTablesToRegenerate(
            IReadOnlyList<TableMetadata> allTables,
            IEnumerable<string> requestedTableNames,
            Func<TableMetadata, string?> readPreviousEntity)
        {
            var requestedNames = new HashSet<string>(requestedTableNames);
            var requested = allTables.Where(t => requestedNames.Contains(t.Name)).ToList();
            var byClassName = allTables.GroupBy(t => t.ClassName).ToDictionary(g => g.Key, g => g.First());
            var principalByFk = allTables.SelectMany(t => t.ReferencingForeignKeys.Select(fk => (fk, t))).ToDictionary(x => x.fk, x => x.t);
            var dependentByFk = allTables.SelectMany(t => t.ForeignKeys.Select(fk => (fk, t))).ToDictionary(x => x.fk, x => x.t);

            var result = new HashSet<TableMetadata>(requested);
            foreach (var table in requested)
            {
                foreach (var fk in table.ForeignKeys)
                    if (principalByFk.TryGetValue(fk, out var principal)) result.Add(principal);
                foreach (var fk in table.ReferencingForeignKeys)
                    if (dependentByFk.TryGetValue(fk, out var dependent)) result.Add(dependent);

                var previous = readPreviousEntity(table);
                if (previous != null)
                {
                    foreach (var className in ReadNavigationTypes(previous))
                        if (byClassName.TryGetValue(className, out var related)) result.Add(related);
                }
            }

            return allTables.Where(result.Contains).ToList(); // stable, model order
        }

        // Class names used by navigation properties (generated as `public virtual T` / `ICollection<T>`).
        private static IEnumerable<string> ReadNavigationTypes(string entityCode)
        {
            var root = CSharpSyntaxTree.ParseText(entityCode).GetRoot();
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!property.Modifiers.Any(SyntaxKind.VirtualKeyword)) continue;
                yield return property.Type is GenericNameSyntax generic
                    ? generic.TypeArgumentList.Arguments.First().ToString()
                    : property.Type.ToString();
            }
        }
    }
}
