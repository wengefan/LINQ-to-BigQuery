﻿using BigQuery.Linq.Functions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BigQuery.Linq
{
    // Expression to BigQuery Query Translate
    internal class BigQueryTranslateVisitor : ExpressionVisitor
    {
        readonly int depth = 1;
        readonly int indentSize = 2;
        readonly FormatOption option;

        StringBuilder sb = new StringBuilder();

        public BigQueryTranslateVisitor(int depth, int indentSize, FormatOption option)
        {
            this.depth = depth;
            this.indentSize = indentSize;
            this.option = option;
        }

        // EntryPoint
        public static string BuildQuery(string command, int depth, int indentSize, FormatOption option, Expression expression, bool forceIndent = false)
        {
            var visitor = new BigQueryTranslateVisitor(depth, indentSize, option);

            visitor.sb.Append(command);
            if (option == FormatOption.Indent)
            {
                visitor.sb.Append(Environment.NewLine);
                if (forceIndent)
                {
                    visitor.AppendIndent();
                }
            }
            else
            {
                visitor.sb.Append(" ");
            }

            visitor.Visit(expression);
            return visitor.sb.ToString();
        }

        void AppendIndent()
        {
            if (option == FormatOption.Indent)
            {
                sb.Append(new string(' ', indentSize * depth));
            }
        }

        internal string VisitAndClearBuffer(Expression node)
        {
            Visit(node);
            var result = sb.ToString();
            sb.Clear();
            return result;
        }

        protected override Expression VisitNew(NewExpression node)
        {
            var indent = new string(' ', depth * indentSize);
            var innerTranslator = new BigQueryTranslateVisitor(0, 0, FormatOption.Flat);

            var merge = node.Members.Zip(node.Arguments, (x, y) =>
            {
                var rightValue = innerTranslator.VisitAndClearBuffer(y);

                if (x.Name == rightValue.Trim('[', ']')) return "[" + x.Name + "]";

                return rightValue + " AS " + "[" + x.Name + "]";
            });

            var command = string.Join("," + Environment.NewLine,
                merge.Select(x => indent + x));

            sb.Append(command);

            return node;
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            string expr;
            bool isNull = false;
            switch (node.NodeType)
            {
                // Logical operators
                case ExpressionType.AndAlso:
                    expr = "AND";
                    break;
                case ExpressionType.OrElse:
                    expr = "OR";
                    break;
                // Comparison functions
                case ExpressionType.LessThan:
                    expr = "<";
                    break;
                case ExpressionType.LessThanOrEqual:
                    expr = "<=";
                    break;
                case ExpressionType.GreaterThan:
                    expr = ">";
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    expr = ">=";
                    break;
                case ExpressionType.Equal:
                    {
                        var right = node.Right;
                        isNull = (right is ConstantExpression) && ((ConstantExpression)right).Value == null;
                        expr = (isNull) ? "IS NULL" : "=";
                    }
                    break;
                case ExpressionType.NotEqual:
                    {
                        var right = node.Right;
                        isNull = (right is ConstantExpression) && ((ConstantExpression)right).Value == null;
                        expr = (isNull) ? "IS NOT NULL" : "!=";
                    }
                    break;
                // Arithmetic operators
                case ExpressionType.Add:
                    expr = "+";
                    break;
                case ExpressionType.Subtract:
                    expr = "-";
                    break;
                case ExpressionType.Multiply:
                    expr = "*";
                    break;
                case ExpressionType.Divide:
                    expr = "/";
                    break;
                case ExpressionType.Modulo:
                    expr = "%";
                    break;
                default:
                    throw new InvalidOperationException("Invalid node type:" + node.NodeType);
            }

            base.Visit(node.Left); // run to left

            sb.Append(" " + expr + " ");

            if (!isNull)
            {
                base.Visit(node.Right); // run to right
            }

            return node;
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            // Casting functions(lack of HexString convert)
            if (node.NodeType == ExpressionType.Convert)
            {
                var typeCode = Type.GetTypeCode(node.Type);
                switch (typeCode)
                {
                    case TypeCode.Boolean:
                        sb.Append("BOOLEAN(");
                        break;
                    case TypeCode.Int16:
                    case TypeCode.Int32:
                    case TypeCode.Int64:
                        sb.Append("INTEGER(");
                        break;
                    case TypeCode.Single:
                    case TypeCode.Double:
                        sb.Append("FLOAT(");
                        break;
                    case TypeCode.String:
                        sb.Append("STRING(");
                        break;
                    default:
                        throw new InvalidOperationException("Not supported cast type:" + node.Type);
                }

                var expr = base.VisitUnary(node);

                sb.Append(")");

                return expr;
            }
            else if (node.NodeType == ExpressionType.Not) // Logical operator
            {
                sb.Append("NOT ");
                return base.VisitUnary(node);
            }

            throw new InvalidOperationException("Not supported unary expression:" + node);
        }

        // append field access
        protected override Expression VisitMember(MemberExpression node)
        {
            var nodes = new List<MemberExpression>();
            var next = node;
            while (next != null)
            {
                nodes.Add(next);
                next = next.Expression as MemberExpression;
            }

            // for record type
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                sb.Append("[" + nodes[i].Member.Name + "]");
                if (nodes.Count != 1 && i != 0)
                {
                    sb.Append(".");
                }
            }

            return node;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            string expr = "";
            switch (Type.GetTypeCode(node.Value.GetType()))
            {
                case TypeCode.Boolean:
                    var b = (bool)node.Value;
                    expr = (b == true) ? "true" : "false";
                    break;
                case TypeCode.Char:
                case TypeCode.String:
                    expr = "\'" + node.Value + "\'";
                    break;
                case TypeCode.DateTime:
                    // TODO:if value is DateTime, need format!
                    expr = node.Value.ToString();
                    break;
                case TypeCode.DBNull:
                case TypeCode.Empty:
                    sb.Append("NULL");
                    break;
                case TypeCode.Decimal:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    sb.Append(node.Value);
                    break;
                case TypeCode.Object:
                    // TODO:it's record?
                    break;
                default:
                    throw new InvalidOperationException();
            }

            sb.Append(expr);
            return node;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var attr = node.Method.GetCustomAttributes<FunctionNameAttribute>().FirstOrDefault();
            if (attr == null) throw new InvalidOperationException("Not support method:" + node.Method.DeclaringType.Name + "." + node.Method.Name + " Method can only call BigQuery.Linq.Functions.*");

            if (attr.SpecifiedFormatterType != null)
            {
                var formatter = Activator.CreateInstance(attr.SpecifiedFormatterType, true) as ISpeficiedFormatter;
                sb.Append(formatter.Format(node));
            }
            else
            {
                sb.Append(attr.Name + "(");

                var innerTranslator = new BigQueryTranslateVisitor(0, 0, FormatOption.Flat);
                var args = string.Join(", ", node.Arguments.Select(x => innerTranslator.VisitAndClearBuffer(x)));

                sb.Append(args);

                sb.Append(")");
            }

            return node;
        }

        protected override Expression VisitConditional(ConditionalExpression node)
        {
            var innerTranslator = new BigQueryTranslateVisitor(0, 0, FormatOption.Flat);

            // case when ... then ... ... else .. end
            // TODO:need more clean format
            if (node.IfFalse is ConditionalExpression)
            {
                sb.Append("CASE");

                sb.Append(Environment.NewLine);

                Expression right = node;
                while (right is ConditionalExpression)
                {
                    var rightNode = right as ConditionalExpression;

                    // left
                    sb.Append(" WHEN ");
                    {
                        sb.Append(innerTranslator.VisitAndClearBuffer(rightNode.Test));
                    }

                    sb.Append(" THEN ");
                    {
                        sb.Append(innerTranslator.VisitAndClearBuffer(rightNode.IfTrue));
                    }

                    right = rightNode.IfFalse;
                }

                sb.Append(" ELSE ");
                sb.Append(innerTranslator.VisitAndClearBuffer(right));

                sb.Append(" END");
            }
            else
            {
                sb.Append("IF(");
                {
                    sb.Append(innerTranslator.VisitAndClearBuffer(node.Test));
                }
                sb.Append(", ");
                {
                    sb.Append(innerTranslator.VisitAndClearBuffer(node.IfTrue));
                }
                sb.Append(", ");
                {
                    sb.Append(innerTranslator.VisitAndClearBuffer(node.IfFalse));
                }
                sb.Append(")");
            }
            return node;
        }
    }
}