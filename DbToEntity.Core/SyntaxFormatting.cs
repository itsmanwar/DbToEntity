using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbToEntity.Core
{
    internal static class SyntaxFormatting
    {
        /// <summary>
        /// NormalizeWhitespace emits the null-forgiving operator as <c>null !</c>; remove the space so the output
        /// reads <c>null!</c>. Call after NormalizeWhitespace.
        /// </summary>
        public static T TightenNullForgiving<T>(this T root) where T : SyntaxNode
        {
            var operandEnds = root.DescendantNodes()
                .OfType<PostfixUnaryExpressionSyntax>()
                .Where(e => e.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                .Select(e => e.Operand.GetLastToken())
                .ToList();

            return operandEnds.Count == 0 ? root : root.ReplaceTokens(operandEnds, (original, _) => original.WithTrailingTrivia());
        }
    }
}
