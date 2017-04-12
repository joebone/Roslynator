﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Roslynator.CSharp.Extensions;
using Roslynator.CSharp.Syntax;
using Roslynator.Diagnostics.Extensions;
using Roslynator.Extensions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Roslynator.CSharp.Refactorings
{
    internal static class MergeLocalDeclarationWithAssignmentRefactoring
    {
        public static void AnalyzeLocalDeclarationStatement(SyntaxNodeAnalysisContext context)
        {
            var localDeclaration = (LocalDeclarationStatementSyntax)context.Node;

            if (!localDeclaration.IsConst
                && !localDeclaration.ContainsDiagnostics
                && !localDeclaration.SpanOrTrailingTriviaContainsDirectives())
            {
                VariableDeclarationSyntax declaration = localDeclaration.Declaration;

                if (declaration != null)
                {
                    SeparatedSyntaxList<VariableDeclaratorSyntax> variables = declaration.Variables;

                    if (variables.Any())
                    {
                        StatementSyntax nextStatement = localDeclaration.NextStatement();

                        if (!nextStatement.ContainsDiagnostics
                            && nextStatement?.SpanOrLeadingTriviaContainsDirectives() == false)
                        {
                            SimpleAssignmentStatement assignment;
                            if (SimpleAssignmentStatement.TryCreate(nextStatement, out assignment)
                                && assignment.Left.IsKind(SyntaxKind.IdentifierName))
                            {
                                SemanticModel semanticModel = context.SemanticModel;
                                CancellationToken cancellationToken = context.CancellationToken;

                                LocalInfo localInfo = FindInitializedVariable((IdentifierNameSyntax)assignment.Left, variables, semanticModel, cancellationToken);

                                if (localInfo.IsValid)
                                {
                                    EqualsValueClauseSyntax initializer = localInfo.Declarator.Initializer;
                                    ExpressionSyntax value = initializer?.Value;

                                    if (value == null
                                        || (IsDefaultValue(declaration.Type, value, semanticModel, cancellationToken)
                                            && !IsReferenced(localInfo.Symbol, assignment.Right, semanticModel, cancellationToken)))
                                    {
                                        context.ReportDiagnostic(DiagnosticDescriptors.MergeLocalDeclarationWithAssignment, localInfo.Declarator.Identifier);

                                        if (value != null)
                                        {
                                            context.ReportNode(DiagnosticDescriptors.MergeLocalDeclarationWithAssignmentFadeOut, initializer);
                                            context.ReportToken(DiagnosticDescriptors.MergeLocalDeclarationWithAssignmentFadeOut, assignment.AssignmentExpression.OperatorToken);
                                        }

                                        context.ReportToken(DiagnosticDescriptors.MergeLocalDeclarationWithAssignmentFadeOut, localDeclaration.SemicolonToken);
                                        context.ReportNode(DiagnosticDescriptors.MergeLocalDeclarationWithAssignmentFadeOut, assignment.Left);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool IsReferenced(
            ILocalSymbol localSymbol,
            SyntaxNode node,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            foreach (SyntaxNode descendantOrSelf in node.DescendantNodesAndSelf())
            {
                if (descendantOrSelf.IsKind(SyntaxKind.IdentifierName)
                    && semanticModel
                        .GetSymbol((IdentifierNameSyntax)descendantOrSelf, cancellationToken)?
                        .Equals(localSymbol) == true)
                {
                    return true;
                }
            }

            return false;
        }

        private static LocalInfo FindInitializedVariable(
            IdentifierNameSyntax identifierName,
            SeparatedSyntaxList<VariableDeclaratorSyntax> declarators,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            string name = identifierName.Identifier.ValueText;

            ILocalSymbol localSymbol = null;

            foreach (VariableDeclaratorSyntax declarator in declarators)
            {
                if (string.Equals(declarator.Identifier.ValueText, name, StringComparison.Ordinal))
                {
                    if (localSymbol == null)
                    {
                        localSymbol = semanticModel.GetSymbol(identifierName, cancellationToken) as ILocalSymbol;

                        if (localSymbol == null)
                            return default(LocalInfo);
                    }

                    if (localSymbol.Equals(semanticModel.GetDeclaredSymbol(declarator, cancellationToken)))
                        return new LocalInfo(declarator, localSymbol);
                }
            }

            return default(LocalInfo);
        }

        private static bool IsDefaultValue(TypeSyntax type, ExpressionSyntax value, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            ITypeSymbol typeSymbol = semanticModel.GetTypeSymbol(type, cancellationToken);

            if (typeSymbol != null)
            {
                return semanticModel.IsDefaultValue(typeSymbol, value, cancellationToken);
            }
            else
            {
                return false;
            }
        }

        public static Task<Document> RefactorAsync(
            Document document,
            VariableDeclaratorSyntax declarator,
            CancellationToken cancellationToken)
        {
            var declaration = (VariableDeclarationSyntax)declarator.Parent;

            var localDeclaration = (LocalDeclarationStatementSyntax)declaration.Parent;

            StatementContainer container = StatementContainer.Create(localDeclaration);

            SyntaxList<StatementSyntax> statements = container.Statements;

            int index = statements.IndexOf(localDeclaration);

            StatementSyntax nextStatement = statements[index + 1];

            var expressionStatement = (ExpressionStatementSyntax)nextStatement;

            var assignment = (AssignmentExpressionSyntax)expressionStatement.Expression;

            ExpressionSyntax right = assignment.Right;

            EqualsValueClauseSyntax initializer = declarator.Initializer;

            ExpressionSyntax value = initializer?.Value;

            VariableDeclaratorSyntax newDeclarator = (value != null)
                ? declarator.ReplaceNode(value, right)
                : declarator.WithInitializer(EqualsValueClause(right));

            LocalDeclarationStatementSyntax newLocalDeclaration = localDeclaration.ReplaceNode(declarator, newDeclarator);

            SyntaxTriviaList trailingTrivia = nextStatement.GetTrailingTrivia();

            IEnumerable<SyntaxTrivia> trivia = container
                .Node
                .DescendantTrivia(TextSpan.FromBounds(localDeclaration.Span.End, right.SpanStart));

            if (!trivia.All(f => f.IsWhitespaceOrEndOfLineTrivia()))
            {
                newLocalDeclaration = newLocalDeclaration.WithTrailingTrivia(trivia.Concat(trailingTrivia));
            }
            else
            {
                newLocalDeclaration = newLocalDeclaration.WithTrailingTrivia(trailingTrivia);
            }

            SyntaxList<StatementSyntax> newStatements = statements
                .Replace(localDeclaration, newLocalDeclaration)
                .RemoveAt(index + 1);

            return document.ReplaceNodeAsync(container.Node, container.NodeWithStatements(newStatements), cancellationToken);
        }

        private struct LocalInfo
        {
            public LocalInfo(VariableDeclaratorSyntax declarator, ILocalSymbol symbol)
            {
                Declarator = declarator;
                Symbol = symbol;
            }

            public bool IsValid
            {
                get
                {
                    return Declarator != null
                        && Symbol != null;
                }
            }

            public VariableDeclaratorSyntax Declarator { get; }
            public ILocalSymbol Symbol { get; }
        }
    }
}