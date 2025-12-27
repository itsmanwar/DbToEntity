using System.Collections.Generic;
using System.Text;
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
            var className = table.Name.Singularize().Pascalize();
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
                    indexArgs.Add(AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Pascalize()))));
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
                var propName = col.Name.Pascalize();
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

                // [Required]
                // Only for non-nullable reference types (strings) or if generally desired for validation
                // In C# 8+ nullable context, string? implies optional, string implies required, but explicit Attribute helps EF
                if (!col.IsNullable && typeName == "string")
                {
                    attributes.Add(Attribute(IdentifierName("Required")));
                }

                // [StringLength(n)]
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

                // Add { get; set; }
                property = property.AddAccessorListAccessors(
                    AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                    AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(property);
            }

            // Navigation Properties
            foreach (var fk in table.ForeignKeys)
            {
                var targetClassName = fk.TargetTable.Singularize().Pascalize();
                var sourcePropName = fk.SourceColumn.Pascalize();
                var navPropName = targetClassName;

                if (navPropName == sourcePropName) navPropName += "Nav";

                // [ForeignKey("RoleId")]
                var fkAttr = Attribute(IdentifierName("ForeignKey"),
                    AttributeArgumentList(SingletonSeparatedList(
                        AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(sourcePropName))))));

                var navProperty = PropertyDeclaration(ParseTypeName(targetClassName), navPropName)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.VirtualKeyword))
                    .AddAttributeLists(AttributeList(SingletonSeparatedList(fkAttr)))
                    .AddAccessorListAccessors(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

                properties.Add(navProperty);
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
                var className = table.Name.Singularize().Pascalize();
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

            // Add OnModelCreating
            // Add OnModelCreating
            var statements = new List<StatementSyntax>();
            statements.Add(ExpressionStatement(
                InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, BaseExpression(), IdentifierName("OnModelCreating")))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("modelBuilder")))))));

            foreach (var table in tables)
            {
                var className = table.Name.Singularize().Pascalize();

                // entity => { ... }
                var lambdaStatements = new List<StatementSyntax>();

                // .ToTable("name", "schema")
                var toTableArgs = new List<ArgumentSyntax> { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Name))) };
                if (!string.IsNullOrEmpty(table.Schema) && table.Schema != "public")
                {
                    toTableArgs.Add(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(table.Schema))));
                }

                lambdaStatements.Add(ExpressionStatement(
                    InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("ToTable")))
                    .WithArgumentList(ArgumentList(SeparatedList(toTableArgs)))));

                // .HasKey(e => e.Id).HasName("pk_name")
                if (table.PrimaryKeys.Any())
                {
                    var keyProps = table.PrimaryKeys.Select(k => k.Pascalize());
                    // e => e.Id or e => new { e.Id1, e.Id2 }
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

                // Properties
                foreach (var col in table.Columns)
                {
                    var propName = col.Name.Pascalize();
                    // entity.Property(e => e.Prop)
                    var propertyAccess = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("Property")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                            SimpleLambdaExpression(Parameter(Identifier("e")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("e"), IdentifierName(propName)))))));

                    ExpressionSyntax currentExpression = propertyAccess;

                    // .HasColumnName("name")
                    currentExpression = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasColumnName")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.Name))))));

                    // .HasDefaultValueSql("...")
                    if (!string.IsNullOrEmpty(col.DefaultValue))
                    {
                        currentExpression = InvocationExpression(
                           MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("HasDefaultValueSql")))
                           .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(col.DefaultValue))))));
                    }

                    // .IsRequired() (if not nullable and no default value, usually)
                    if (!col.IsNullable)
                    {
                        currentExpression = InvocationExpression(
                            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, currentExpression, IdentifierName("IsRequired")));
                    }

                    // .HasMaxLength(n)
                    if (col.MaxLength.HasValue && col.DataType.Contains("char") || col.DataType == "text" || col.DataType == "character varying")
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
                    // entity.HasOne(d => d.Nav).WithMany().HasForeignKey(d => d.FK).HasConstraintName("name")
                    var navPropName = fk.TargetTable.Singularize().Pascalize();
                    var fkPropName = fk.SourceColumn.Pascalize();
                    if (navPropName == fkPropName) navPropName += "Nav";

                    var hasOne = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("entity"), IdentifierName("HasOne")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(navPropName)))))));

                    var withMany = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasOne, IdentifierName("WithMany"))); // Unidirectional 1:N

                    var hasForeignKey = InvocationExpression(
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, withMany, IdentifierName("HasForeignKey")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                             SimpleLambdaExpression(Parameter(Identifier("d")),
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("d"), IdentifierName(fkPropName)))))));

                    var hasConstraint = InvocationExpression(
                       MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, hasForeignKey, IdentifierName("HasConstraintName")))
                       .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(fk.ConstraintName))))));

                    lambdaStatements.Add(ExpressionStatement(hasConstraint));
                }

                // modelBuilder.Entity<T>(entity => ... )
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

            if (separateBySchema)
            {
                var schemas = tables
                    .Select(t => t.Schema)
                    .Where(s => !string.IsNullOrEmpty(s) && s != "public")
                    .Distinct()
                    .OrderBy(s => s);

                foreach (var schema in schemas)
                {
                    usings.Add(UsingDirective(ParseName($"{namespaceName}.{schema.Pascalize()}")));
                }
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
                var className = table.Name.Singularize().Pascalize();
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
    }
}
