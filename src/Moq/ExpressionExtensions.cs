// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

using Moq.Properties;
using Moq.Protected;

namespace Moq
{
	internal static partial class ExpressionExtensions
	{
		internal static Delegate CompileUsingExpressionCompiler(this LambdaExpression expression)
		{
			// Expression trees are not compiled directly.
			// The indirection via an ExpressionCompiler allows users to plug a different expression compiler.
			return ExpressionCompiler.Instance.Compile(expression);
		}

		internal static TDelegate CompileUsingExpressionCompiler<TDelegate>(this Expression<TDelegate> expression) where TDelegate : Delegate
		{
			// Expression trees are not compiled directly.
			// The indirection via an ExpressionCompiler allows users to plug a different expression compiler.
			return ExpressionCompiler.Instance.Compile(expression);
		}

		public static bool IsMatch(this Expression expression, out Match match)
		{
			if (expression is MatchExpression matchExpression)
			{
				match = matchExpression.Match;
				return true;
			}

			using (var observer = MatcherObserver.Activate())
			{
				Expression.Lambda<Action>(expression).CompileUsingExpressionCompiler().Invoke();
				return observer.TryGetLastMatch(out match);
			}
		}

		/// <summary>
		///   Splits an expression such as `<c>m => m.A.B(x).C[y] = z</c>` into a chain of parts
		///   that can be set up one at a time:
		///   <list>
		///     <item>`<c>m => m.A</c>`</item>,
		///     <item>`<c>... => ....B(x)</c>`</item>,
		///     <item>`<c>... => ....C</c>`</item>,
		///     <item>`<c>... => ...[y] = z</c>`</item>.
		///   </list>
		///   <para>
		///     The split points are chosen such that each part has exactly one associated
		///     <see cref="MethodInfo"/> and optionally some argument expressions.
		///   </para>
		/// </summary>
		/// <exception cref="ArgumentException">
		///   It was not possible to completely split up the expression.
		/// </exception>
		internal static Stack<LambdaExpressionPart> Split(this LambdaExpression expression)
		{
			Debug.Assert(expression != null);

			var parts = new Stack<LambdaExpressionPart>();

			Expression remainder = expression.Body;
			while (CanSplit(remainder))
			{
				Split(remainder, out remainder, out var part);
				parts.Push(part);
			}

			if (parts.Count > 0 && remainder is ParameterExpression)
			{
				return parts;
			}
			else
			{
				throw new ArgumentException(
					string.Format(
						CultureInfo.CurrentCulture,
						Resources.UnsupportedExpression,
						remainder.ToStringFixed()));
			}

			bool CanSplit(Expression e)
			{
				switch (e.NodeType)
				{
					case ExpressionType.Assign:
					case ExpressionType.AddAssign:
					case ExpressionType.SubtractAssign:
					{
						var assignmentExpression = (BinaryExpression)e;
						return CanSplit(assignmentExpression.Left);
					}

					case ExpressionType.Call:
					case ExpressionType.Index:
					{
						return true;
					}

					case ExpressionType.Invoke:
					{
						var invocationExpression = (InvocationExpression)e;
						return typeof(Delegate).IsAssignableFrom(invocationExpression.Expression.Type);
					}

					case ExpressionType.MemberAccess:
					{
						var memberAccessExpression = (MemberExpression)e;
						return memberAccessExpression.Member is PropertyInfo;
					}

					case ExpressionType.Parameter:
					default:
					{
						return false;
					}
				}
			}

			void Split(Expression e, out Expression r /* remainder */, out LambdaExpressionPart p /* part */)
			{
				const string ParameterName = "...";

				switch (e.NodeType)
				{
					case ExpressionType.Assign:          // assignment to a property or indexer
					case ExpressionType.AddAssign:       // subscription of event handler to event
					case ExpressionType.SubtractAssign:  // unsubscription of event handler from event
					{
						var assignmentExpression = (BinaryExpression)e;
						Split(assignmentExpression.Left, out r, out var lhs);
						PropertyInfo property;
						if (lhs.Expression.Body is MemberExpression me)
						{
							Debug.Assert(me.Member is PropertyInfo);
							property = (PropertyInfo)me.Member;
						}
						else
						{
							Debug.Assert(lhs.Expression.Body is IndexExpression);
							property = ((IndexExpression)lhs.Expression.Body).Indexer;
						}
						var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
						var arguments = new Expression[lhs.Arguments.Count + 1];
						for (var ai = 0; ai < arguments.Length - 1; ++ai)
						{
							arguments[ai] = lhs.Arguments[ai];
						}
						arguments[arguments.Length - 1] = assignmentExpression.Right;
						p = new LambdaExpressionPart(
							expression: Expression.Lambda(
								Expression.MakeBinary(e.NodeType, lhs.Expression.Body, assignmentExpression.Right),
								parameter),
							method: property.GetSetMethod(true),
							arguments);
						return;
					}

					case ExpressionType.Call:  // regular method call
					{
						var methodCallExpression = (MethodCallExpression)e;
						if (!methodCallExpression.Method.IsStatic)
						{
							r = methodCallExpression.Object;
							var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
							var method = methodCallExpression.Method;
							var arguments = methodCallExpression.Arguments;
							p = new LambdaExpressionPart(
										expression: Expression.Lambda(
											Expression.Call(parameter, method, arguments),
											parameter),
										method,
										arguments);
						}
						else
						{
							Debug.Assert(methodCallExpression.Method.IsExtensionMethod());
							Debug.Assert(methodCallExpression.Arguments.Count > 0);
							r = methodCallExpression.Arguments[0];
							var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
							var method = methodCallExpression.Method;
							var arguments = methodCallExpression.Arguments.ToArray();
							arguments[0] = parameter;
							p = new LambdaExpressionPart(
										expression: Expression.Lambda(
											Expression.Call(method, arguments),
											parameter),
										method,
										arguments);
						}
						return;
					}

					case ExpressionType.Index:  // indexer query
					{
						var indexExpression = (IndexExpression)e;
						r = indexExpression.Object;
						var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
						var indexer = indexExpression.Indexer;
						var arguments = indexExpression.Arguments;
						p = new LambdaExpressionPart(
									expression: Expression.Lambda(
										Expression.MakeIndex(parameter, indexer, arguments),
										parameter),
									method: indexer.GetGetMethod(true),
									arguments);
						return;
					}

					case ExpressionType.Invoke:  // delegate invocation
					{
						var invocationExpression = (InvocationExpression)e;
						Debug.Assert(invocationExpression.Expression.Type.IsDelegate());
						r = invocationExpression.Expression;
						var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
						var arguments = invocationExpression.Arguments;
						p = new LambdaExpressionPart(
									expression: Expression.Lambda(
										Expression.Invoke(parameter, arguments),
										parameter),
									method: r.Type.GetMethod("Invoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
									arguments);
						return;
					}

					case ExpressionType.MemberAccess:  // property query
					{
						var memberAccessExpression = (MemberExpression)e;
						Debug.Assert(memberAccessExpression.Member is PropertyInfo);
						r = memberAccessExpression.Expression;
						var parameter = Expression.Parameter(r.Type, r is ParameterExpression ope ? ope.Name : ParameterName);
						var property = memberAccessExpression.GetReboundProperty();
						var method = property.CanRead ? property.GetGetMethod(true) : property.GetSetMethod(true);
						//                    ^^^^^^^                               ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
						// We're in the switch case block for property read access, therefore we prefer the
						// getter. When a read-write property is being assigned to, we end up here, too, and
						// select the wrong accessor. However, that doesn't matter because it will be over-
						// ridden in the above `Assign` case. Finally, if a write-only property is being
						// assigned to, we fall back to the setter here in order to not end up without a
						// method at all.
						p = new LambdaExpressionPart(
									expression: Expression.Lambda(
										Expression.MakeMemberAccess(parameter, property),
										parameter),
									method);
						return;
					}

					default:
						Debug.Assert(!CanSplit(e));
						throw new InvalidOperationException();  // this should be unreachable
				}
			}
		}

		internal static PropertyInfo GetReboundProperty(this MemberExpression expression)
		{
			Debug.Assert(expression.Member is PropertyInfo);

			var property = (PropertyInfo)expression.Member;

			// the following block is required because .NET compilers put the wrong PropertyInfo into MemberExpression
			// for properties originally declared in base classes; they will put the base class' PropertyInfo into
			// the expression. we attempt to correct this here by checking whether the type of the accessed object
			// has a property by the same name whose base definition equals the property in the expression; if so,
			// we "upgrade" to the derived property.
			if (property.DeclaringType != expression.Expression.Type)
			{
				var derivedProperty = expression.Expression.Type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (derivedProperty != null && derivedProperty.GetMethod.GetBaseDefinition() == property.GetMethod)
				{
					return derivedProperty;
				}
			}

			return property;
		}

		/// <summary>
		/// Converts the body of the lambda expression into the <see cref="PropertyInfo"/> referenced by it.
		/// </summary>
		public static PropertyInfo ToPropertyInfo(this LambdaExpression expression)
		{
			if (expression.Body is MemberExpression prop)
			{
				return prop.GetReboundProperty();
			}

			throw new ArgumentException(string.Format(
				CultureInfo.CurrentCulture,
				Resources.SetupNotProperty,
				expression.ToStringFixed()));
		}

		/// <summary>
		/// Checks whether the body of the lambda expression is a property access.
		/// </summary>
		public static bool IsProperty(this LambdaExpression expression)
		{
			Guard.NotNull(expression, nameof(expression));

			return expression.Body is MemberExpression memberExpression && memberExpression.Member is PropertyInfo;
		}

		/// <summary>
		///   Checks whether the body of the lambda expression is a indexer access.
		/// </summary>
		public static bool IsPropertyIndexer(this LambdaExpression expression)
		{
			Guard.NotNull(expression, nameof(expression));

			return expression.Body is IndexExpression
				|| expression.Body is MethodCallExpression methodCallExpression && methodCallExpression.Method.IsSpecialName;
		}

		public static Expression StripQuotes(this Expression expression)
		{
			while (expression.NodeType == ExpressionType.Quote)
			{
				expression = ((UnaryExpression)expression).Operand;
			}

			return expression;
		}

		public static Expression<Action<TMock>> AssignItIsAny<TMock, T>(this Expression<Func<TMock, T>> expression)
		{
			Debug.Assert(expression != null);
			Debug.Assert(expression.Body is MemberExpression || expression.Body is IndexExpression);

			return Expression.Lambda<Action<TMock>>(
					Expression.Assign(
							expression.Body,
							ItExpr.IsAny<T>()),
					expression.Parameters[0]);
		}

		public static Expression PartialEval(this Expression expression)
		{
			return Evaluator.PartialEval(expression);
		}

		public static LambdaExpression PartialMatcherAwareEval(this LambdaExpression expression)
		{
			return (LambdaExpression)Evaluator.PartialEval(
				expression,
				PartialMatcherAwareEval_ShouldEvaluate);
		}

		private static bool PartialMatcherAwareEval_ShouldEvaluate(Expression expression)
		{
			switch (expression.NodeType)
			{
				case ExpressionType.Parameter:
					return false;

				case ExpressionType.Extension:
					return !(expression is MatchExpression);

				case ExpressionType.Call:
					return !((MethodCallExpression)expression).Method.IsDefined(typeof(MatcherAttribute), true);

				case ExpressionType.MemberAccess:
					return !((MemberExpression)expression).Member.IsDefined(typeof(MatcherAttribute), true);

				default:
					return true;
			}
		}

		/// <devdoc>
		/// TODO: remove this code when https://connect.microsoft.com/VisualStudio/feedback/ViewFeedback.aspx?FeedbackID=331583 
		/// is fixed.
		/// </devdoc>
		public static string ToStringFixed(this Expression expression)
		{
			return new ExpressionStringBuilder().Append(expression).ToString();
		}
	}
}
