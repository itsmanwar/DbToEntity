using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using DbToEntity.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

using Humanizer;

namespace DbToEntity.Core
{
    public class RoslynEntityGenerator : IEntityGenerator
    {
        public GeneratedFile GenerateEntity(TableMetadata table, string namespaceName)
        {
            var className = table.ClassName; // Use resolved name
            var properties = new List<MemberDeclarationSyntax>();

            // [Table("Name", Schema = "Schema")]
            var tableAttributeArgs = new List<AttributeArgumentSyntax>
            {
                AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Name)))
            };

            if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
            {
                tableAttributeArgs.Add(AttributeArgument(
                    NameEquals(IdentifierName("Schema")),
                    null,
                    LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Schema))));
            }

            var classAttributes = new List<AttributeListSyntax>
            {
                AttributeList(SingletonSeparatedList(
                    Attribute(IdentifierName("Table"), ToAttributeArgumentList(tableAttributeArgs.ToArray()))))
            };

            // [Index("Col1", "Col2", Name = "IndexName", IsUnique = true)]
            foreach (var index in table.Indexes)
            {
                var indexArgs = new List<AttributeArgumentSyntax>();

                // Columns
                foreach (var col in index.Columns)
                {
                    indexArgs.Add(AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(SanitizeIdentifier(col)))));
                }

                // Name
                indexArgs.Add(AttributeArgument(
                    NameEquals(IdentifierName("Name")),
                    null,
                    LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(index.Name))));

                // IsUnique
                if (index.IsUnique)
                {
                    indexArgs.Add(AttributeArgument(
                        NameEquals(IdentifierName("IsUnique")),
                        null,
                        LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                }

                classAttributes.Add(AttributeList(SingletonSeparatedList(
                    Attribute(IdentifierName("Index"), ToAttributeArgumentList(indexArgs.ToArray())))));
            }

            foreach (var col in table.Columns)
            {
                var typeName = TypeMapper.Map(col.DataType, col.IsNullable);
                var propName = SanitizeIdentifier(col.Name);
                if (propName == className) propName += "Member";
                var attributes = new List<AttributeSyntax>();

                // [Column("name")]
                attributes.Add(Attribute(IdentifierName("Column"),
                    AttributeArgumentList(SingletonSeparatedList(
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Name)))))));

                // [Key]
                if (table.PrimaryKeys.Contains(col.Name))
                {
                    attributes.Add(Attribute(IdentifierName("Key")));
                }

                if (!col.IsNullable && typeName == "string")
                {
                    attributes.Add(Attribute(IdentifierName("Required")));
                }

                if (col.MaxLength.HasValue && typeName == "string")
                {
                    attributes.Add(Attribute(IdentifierName("StringLength"),
                        AttributeArgumentList(SingletonSeparatedList(
                            AttributeArgument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(col.MaxLength.Value)))))));
                }

                var property = PropertyDeclaration(ParseTypeName(typeName), propName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword));

                if (attributes.Any())
                {
                    property = property.AddAttributeLists(AttributeList(SeparatedList(attributes)));
                }

                property = property.AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(property);
            }

            // Navigation Properties
            // Navigation Properties
            // Group FKs by target type to detect collisions (multiple FKs to same table)
            var fkGroups = table.ForeignKeys.GroupBy(fk => fk.TargetClassName).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var fk in table.ForeignKeys)
            {
                var targetClassName = fk.TargetClassName;
                var sourceProps = fk.SourceColumns.Select(c => SanitizeIdentifier(c)).ToList();
                var sourcePropNameString = string.Join(", ", sourceProps);

                string navPropName;
                bool isMultiple = fkGroups.ContainsKey(targetClassName) && fkGroups[targetClassName].Count > 1;

                if (isMultiple)
                {
                    // Disambiguate using FK column name
                    // e.g. PhotoFileId -> PhotoFile
                    var baseName = sourceProps.First();
                    if (baseName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                        baseName = baseName.Substring(0, baseName.Length - 2);

                    navPropName = baseName;

                    // Fallback if still ambiguous (very unlikely unless composite keys overlap weirdly) or if name matches Type name exactly?
                    // Actually, matching Type name is fine: public UploadedFile UploadedFile { get; set; }
                    // But here we want distinct names.
                }
                else
                {
                    navPropName = targetClassName;
                }

                // Avoid collision with property names (columns) or enclosing class name
                if (sourceProps.Contains(navPropName) || properties.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == navPropName) || navPropName == className)
                {
                    navPropName += "Nav";
                }

                var attributes = new List<AttributeSyntax>();

                // [ForeignKey("...")]
                attributes.Add(Attribute(IdentifierName("ForeignKey"),
                    AttributeArgumentList(SingletonSeparatedList(
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(sourcePropNameString)))))));

                // [InverseProperty("...")]
                // Only needed if there are multiple relationships between these two tables.
                // We check if the TARGET table has multiple FKs pointing back to US (Source).
                // But simplified: Always add InverseProperty if we are in a "Multiple" scenario to be safe and explicit.
                // We need to know what the Inverse Collection is named on Duplicate scenarios.
                // Inverse naming logic: SourceClassName + (Multiple ? FKColumnBase : "") + "s"

                // We need to simulate the Inverse naming logic here.
                // Note: The Inverse logic runs on the TARGET table generation.
                // Here we are generating the SOURCE table.
                // We assume the Target table generation follows the same deterministic logic.
                // Inverse Logic Ref:
                // Group referencing FKs by SourceClassName.
                // If count > 1: Name = SourceClassName + FKColumnBase + "s" (e.g. PensionerPhotoFiles)
                // Else: Name = SourceClassNamePlural (e.g. Pensioners)

                // So checking if THIS specific relationship is part of a "Multiple from Source" group on the Target.
                // Ideally we'd look at ReferencingForeignKeys of the Target table... but we don't have TargetTable metadata here fully populated with Referencing FKs of *other* tables?
                // Wait, `ReferencingForeignKeys` on table metadata objects are populated in `Program.cs`.
                // However, do we have access to `fk.TargetTable` metadata object?
                // `fk` is `ForeignKeyMetadata`. It has string `TargetTable`.
                // It does NOT have reference to the full `TableMetadata` object of the target.
                // This makes it hard to know if the Target table sees "multiple" from us.

                // Assumption: If WE have multiple FKs to Target, Target likely has multiple Inverse FKs from US.
                // So `isMultiple` here is a good proxy.
                if (isMultiple)
                {
                    var baseName = sourceProps.First();
                    if (baseName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                        baseName = baseName.Substring(0, baseName.Length - 2);

                    var inversePropName = table.ClassName + baseName.Pluralize(); // e.g. PensionerPhotoFiles
                    attributes.Add(Attribute(IdentifierName("InverseProperty"),
                       AttributeArgumentList(SingletonSeparatedList(
                           AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(inversePropName)))))));
                }

                var navProperty = PropertyDeclaration(ParseTypeName(targetClassName), navPropName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.VirtualKeyword))
                    .AddAttributeLists(AttributeList(SeparatedList(attributes)))
                    .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(navProperty);
            }

            // Inverse Navigation Properties (Collections)
            // Group FKs by source type to detect collisions
            var refFkGroups = table.ReferencingForeignKeys.GroupBy(fk => fk.SourceClassName).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var fk in table.ReferencingForeignKeys)
            {
                var sourceClassName = fk.SourceClassName;
                var sourceProps = fk.SourceColumns.Select(c => SanitizeIdentifier(c)).ToList();

                string propName;
                bool isMultiple = refFkGroups.ContainsKey(sourceClassName) && refFkGroups[sourceClassName].Count > 1;

                if (isMultiple)
                {
                    // e.g. PensionerPhotoFiles
                    var baseName = sourceProps.First();
                    if (baseName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                        baseName = baseName.Substring(0, baseName.Length - 2);

                    propName = sourceClassName + baseName.Pluralize();
                }
                else
                {
                    propName = sourceClassName.Pluralize();
                }

                // Avoid duplicate names if property already exists
                if (properties.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == propName))
                {
                    propName += "Collection";
                }

                var collectionType = $"ICollection<{sourceClassName}>";

                var attributes = new List<AttributeSyntax>();

                if (isMultiple)
                {
                    // Inverse needs to point to the Forward Nav name.
                    // Forward Nav Name Logic:
                    // If multiple: FKColumnBase (e.g. PhotoFile) (from source table perspective)
                    // If single: TargetClassName (which is US, so "UploadedFile")

                    // Here we are on Target (UploadedFile).
                    // The Source (Pensioner) has the FK.
                    // So Source used `isMultiple` logic on its side.
                    // Since we detected `isMultiple` here (multiple FKs from Pensioner), it implies Source also has multiple FKs to US.
                    // So Source used FKColumnBase naming.

                    var baseName = sourceProps.First();
                    if (baseName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                        baseName = baseName.Substring(0, baseName.Length - 2);

                    attributes.Add(Attribute(IdentifierName("InverseProperty"),
                       AttributeArgumentList(SingletonSeparatedList(
                           AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(baseName)))))));
                }

                var collectionProperty = PropertyDeclaration(ParseTypeName(collectionType), propName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.VirtualKeyword));

                if (attributes.Any())
                {
                    collectionProperty = collectionProperty.AddAttributeLists(AttributeList(SeparatedList(attributes)));
                }

                collectionProperty = collectionProperty.AddAccessorListAccessors(
                       AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                       AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(collectionProperty);
            }


            var classDeclaration = ClassDeclaration(className)
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword))
                .AddAttributeLists(classAttributes.ToArray())
                .AddMembers(properties.ToArray());

            var namespaceDeclaration = NamespaceDeclaration(ParseName(namespaceName))
                .AddMembers(classDeclaration);

            var cu = CompilationUnit()
                .AddUsings(
                    UsingDirective(ParseName("System")),
                    UsingDirective(ParseName("System.ComponentModel.DataAnnotations")),
                    UsingDirective(ParseName("System.ComponentModel.DataAnnotations.Schema")),
                    UsingDirective(ParseName("Microsoft.EntityFrameworkCore")))
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace();

            return new GeneratedFile
            {
                FileName = $"{className}.cs",
                Content = cu.ToFullString()
            };
        }

        private AttributeArgumentListSyntax ToAttributeArgumentList(AttributeArgumentSyntax[] args)
        {
            return AttributeArgumentList(SeparatedList(args));
        }

        public GeneratedFile GenerateDbContext(List<TableMetadata> tables, string namespaceName, string dbContextName, bool separateBySchema = false)
        {
            var dbSets = new List<MemberDeclarationSyntax>();

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name
                var dbSetType = $"DbSet<{className}>";
                var propName = className.Pluralize();

                var property = PropertyDeclaration(ParseTypeName(dbSetType), propName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                     .AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                dbSets.Add(property);
            }

            var baseClass = SimpleBaseType(ParseTypeName("DbContext"));

            var constructor = ConstructorDeclaration(dbContextName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    Parameter(Identifier("options"))
                    .WithType(ParseTypeName($"DbContextOptions<{dbContextName}>")))
                .WithInitializer(
                    ConstructorInitializer(SyntaxKind.BaseConstructorInitializer,
                    ArgumentList(SingletonSeparatedList(Argument(IdentifierName("options"))))))
                .WithBody(Block());

            var classDeclaration = ClassDeclaration(dbContextName)
                .AddModifiers(Token(SyntaxKind.PublicKeyword))
                .AddBaseListTypes(baseClass)
                .AddMembers(constructor)
                .AddMembers(dbSets.ToArray());

            var statements = new List<StatementSyntax>();
            statements.Add(ExpressionStatement(
                InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, BaseExpression(), IdentifierName("OnModelCreating")))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("modelBuilder")))))));

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name

                // entity => { ... }
                var lambdaStatements = new List<StatementSyntax>();

                // .ToTable("name", "schema") or .ToView("name", "schema")
                string mappingMethod = (table.Type == ObjectType.View || table.Type == ObjectType.MaterializedView) ? "ToView" : "ToTable";

                var toTableArgs = new List<ArgumentSyntax> { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Name))) };
                if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                {
                    toTableArgs.Add(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Schema))));
                }

                lambdaStatements.Add(ExpressionStatement(
                    InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName(mappingMethod)))
                    .WithArgumentList(ArgumentList(SeparatedList(toTableArgs)))));

                // .HasKey(e => e.Id) or .HasNoKey()
                if (table.PrimaryKeys.Any())
                {
                    var keyProps = table.PrimaryKeys.Select(k => SanitizeIdentifier(k));
                    ExpressionSyntax keyExpression;
                    if (keyProps.Count() == 1)
                    {
                        keyExpression = SimpleLambdaExpression(Parameter(Identifier("e")),
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(keyProps.First())));
                    }
                    else
                    {
                        var anonProps = keyProps.Select(p => AnonymousObjectMemberDeclarator(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(p))));
                        keyExpression = SimpleLambdaExpression(Parameter(Identifier("e")),
                            AnonymousObjectCreationExpression(SeparatedList(anonProps)));
                    }

                    var hasKeyInvocation = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasKey")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(keyExpression))));

                    if (!string.IsNullOrEmpty(table.PrimaryKeyName))
                    {
                        hasKeyInvocation = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasKeyInvocation, IdentifierName("HasName")))
                            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.PrimaryKeyName))))));
                    }

                    lambdaStatements.Add(ExpressionStatement(hasKeyInvocation));
                }
                else
                {
                    // Keyless Entity (View)
                    lambdaStatements.Add(ExpressionStatement(
                       InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasNoKey")))));
                }


                // Properties
                foreach (var col in table.Columns)
                {
                    var propName = SanitizeIdentifier(col.Name);
                    if (propName == className) propName += "Member";
                    var propertyAccess = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("Property")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                            SimpleLambdaExpression(Parameter(Identifier("e")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(propName)))))));

                    ExpressionSyntax currentExpression = propertyAccess;

                    currentExpression = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasColumnName")))
                                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Name))))));

                    if (!string.IsNullOrEmpty(col.DefaultValue))
                    {
                        currentExpression = InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasDefaultValueSql")))
                           .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.DefaultValue))))));
                    }

                    if (!col.IsNullable)
                    {
                        currentExpression = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("IsRequired")));
                    }

                    if (col.MaxLength.HasValue && (col.DataType.Contains("char") || col.DataType == "text" || col.DataType == "character varying"))
                    {
                        currentExpression = InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasMaxLength")))
                           .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(col.MaxLength.GetValueOrDefault()))))));
                    }

                    lambdaStatements.Add(ExpressionStatement(currentExpression));
                }

                // Relationships
                foreach (var fk in table.ForeignKeys)
                {
                    var navPropName = fk.TargetClassName; // Use resolved name
                    var sourceProps = fk.SourceColumns.Select(c => SanitizeIdentifier(c)).ToList();

                    if (sourceProps.Contains(navPropName)) navPropName += "Nav";

                    var hasOne = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasOne")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(navPropName)))))));

                    var withMany = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasOne, IdentifierName("WithMany")));

                    ExpressionSyntax fkLambdaExpr;
                    if (sourceProps.Count == 1)
                    {
                        fkLambdaExpr = MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(sourceProps[0]));
                    }
                    else
                    {
                        var anonProps = sourceProps.Select(p => AnonymousObjectMemberDeclarator(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(p))));
                        fkLambdaExpr = AnonymousObjectCreationExpression(SeparatedList(anonProps));
                    }

                    var hasForeignKey = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, withMany, IdentifierName("HasForeignKey")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")), fkLambdaExpr)))));

                    var hasConstraint = InvocationExpression(
                       MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasForeignKey, IdentifierName("HasConstraintName")))
                       .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(fk.ConstraintName))))));

                    lambdaStatements.Add(ExpressionStatement(hasConstraint));
                }

                var entityMethod = GenericName(Identifier("Entity"))
                    .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(ParseTypeName(className))));

                var entityConfig = InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("modelBuilder"), entityMethod))
                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                        ParenthesizedLambdaExpression(ParameterList(SingletonSeparatedList(Parameter(Identifier("entity")))), Block(lambdaStatements))))));

                statements.Add(ExpressionStatement(entityConfig));
            }

            var onModelCreating = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "OnModelCreating")
                .AddModifiers(Token(SyntaxKind.ProtectedKeyword), Token(SyntaxKind.OverrideKeyword))
                .AddParameterListParameters(Parameter(Identifier("modelBuilder")).WithType(ParseTypeName("ModelBuilder")))
                .WithBody(Block(statements));

            classDeclaration = classDeclaration.AddMembers(onModelCreating);

            var namespaceDeclaration = NamespaceDeclaration(ParseName(namespaceName))
                .AddMembers(classDeclaration);

            var usings = new List<UsingDirectiveSyntax>
            {
                UsingDirective(ParseName("System")),
                UsingDirective(ParseName("System.ComponentModel.DataAnnotations")),
                UsingDirective(ParseName("System.ComponentModel.DataAnnotations.Schema")),
                UsingDirective(ParseName("Microsoft.EntityFrameworkCore"))
            };

            var distinctNamespaces = tables
                .Select(t => t.Namespace)
                .Where(n => !string.IsNullOrEmpty(n) && n != namespaceName)
                .Distinct()
                .OrderBy(n => n);

            foreach (var ns in distinctNamespaces)
            {
                usings.Add(UsingDirective(ParseName(ns)));
            }

            var cu = CompilationUnit()
                .AddUsings(usings.ToArray())
                .AddMembers(namespaceDeclaration)
                .NormalizeWhitespace();

            return new GeneratedFile
            {
                FileName = $"{dbContextName}.cs",
                Content = cu.ToFullString()
            };
        }

        public GeneratedFile UpdateDbContext(string existingCode, List<TableMetadata> tables, string dbContextName)
        {
            var tree = CSharpSyntaxTree.ParseText(existingCode);
            var root = tree.GetRoot() as CompilationUnitSyntax;

            if (root == null) throw new Exception("Could not parse existing DbContext");

            var classNode = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == dbContextName);

            if (classNode == null) throw new Exception($"Could not find class {dbContextName} in existing code");

            var newMembers = new List<MemberDeclarationSyntax>();
            bool changed = false;

            foreach (var table in tables)
            {
                var className = table.ClassName; // Use resolved name
                var propName = className.Pluralize();

                // Check if property exists
                var exists = classNode.Members.OfType<PropertyDeclarationSyntax>()
                    .Any(p => p.Identifier.Text == propName);

                if (!exists)
                {
                    var dbSetType = $"DbSet<{className}>";
                    var property = PropertyDeclaration(ParseTypeName(dbSetType), propName)
                        .AddModifiers(Token(SyntaxKind.PublicKeyword))
                         .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                    newMembers.Add(property);
                    changed = true;
                }
            }

            if (changed)
            {
                var newClassNode = classNode.AddMembers(newMembers.ToArray());
                var newRoot = root.ReplaceNode(classNode, newClassNode).NormalizeWhitespace();

                return new GeneratedFile
                {
                    FileName = $"{dbContextName}.cs",
                    Content = newRoot.ToFullString()
                };
            }

            return new GeneratedFile
            {
                FileName = $"{dbContextName}.cs",
                Content = existingCode
            };
        }

        private string ToPascalCase(string original)
        {
            return original.Pascalize();
        }

        private string SanitizeIdentifier(string name)
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
    }
}
