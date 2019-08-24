// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

using Moq.Properties;

namespace Moq
{
	/// <summary>
	///   This visitor translates multi-dot expressions such as `r.A.B.C` to an executable expression
	///   `FluentMock(FluentMock(FluentMock(root, m => m.A), a => a.B), b => b.C)` that, when executed,
	///   will cause all "inner mocks" towards the rightmost sub-expression to be automatically mocked.
	///   The original expression can be added to the final, rightmost inner mock as a setup.
	/// </summary>
	internal sealed class FluentMockVisitor : ExpressionVisitor
	{
		internal static readonly MethodInfo FluentMockMethod =
			typeof(FluentMockVisitor).GetMethod(nameof(FluentMock), BindingFlags.NonPublic | BindingFlags.Static);

		private bool isAtRightmost;
		private readonly Func<ParameterExpression, Expression> resolveRoot;
		private readonly bool setupRightmost;

		public FluentMockVisitor(Func<ParameterExpression, Expression> resolveRoot, bool setupRightmost)
		{
			Debug.Assert(resolveRoot != null);

			this.isAtRightmost = true;
			this.resolveRoot = resolveRoot;
			this.setupRightmost = setupRightmost;
		}

		protected override Expression VisitMember(MemberExpression node)
		{
			Debug.Assert(node != null);

			// Translate differently member accesses over transparent
			// compiler-generated types as they are typically the
			// anonymous types generated to build up the query expressions.
			if (node.Expression.NodeType == ExpressionType.Parameter &&
				node.Expression.Type.IsDefined(typeof(CompilerGeneratedAttribute), false))
			{
				var memberType = node.Member is FieldInfo ?
					((FieldInfo)node.Member).FieldType :
					((PropertyInfo)node.Member).PropertyType;

				// Generate a Mock.Get over the entire member access rather.
				// <anonymous_type>.foo => Mock.Get(<anonymous_type>.foo)
				return Expression.Call(null,
					Mock.GetMethod.MakeGenericMethod(memberType), node);
			}

			// If member is not mock-able, actually, including being a sealed class, etc.?
			if (node.Member is FieldInfo)
				throw new NotSupportedException();

			var lambdaParam = Expression.Parameter(node.Expression.Type, "mock");
			Expression lambdaBody = Expression.MakeMemberAccess(lambdaParam, node.Member);
			var targetMethod = GetTargetMethod(node.Expression.Type, ((PropertyInfo)node.Member).PropertyType);
			if (isAtRightmost)
			{
				isAtRightmost = false;
			}

			return TranslateFluent(node.Expression.Type, ((PropertyInfo)node.Member).PropertyType, targetMethod, Visit(node.Expression), lambdaParam, lambdaBody);
		}

		protected override Expression VisitMethodCall(MethodCallExpression node)
		{
			Debug.Assert(node != null);

			var lambdaParam = Expression.Parameter(node.Object.Type, "mock");
			var lambdaBody = Expression.Call(lambdaParam, node.Method, node.Arguments);
			var targetMethod = GetTargetMethod(node.Object.Type, node.Method.ReturnType);
			if (isAtRightmost)
			{
				isAtRightmost = false;
			}

			return TranslateFluent(node.Object.Type, node.Method.ReturnType, targetMethod, this.Visit(node.Object), lambdaParam, lambdaBody);
		}

		protected override Expression VisitParameter(ParameterExpression node)
		{
			Debug.Assert(node != null);

			return this.resolveRoot(node);
		}

		private MethodInfo GetTargetMethod(Type objectType, Type returnType)
		{
			// dte.Solution =>
			if (this.setupRightmost && this.isAtRightmost)
			{
				//.Setup(mock => mock.Solution)
				return typeof(Mock<>)
				       .MakeGenericType(objectType)
				       .GetMethods("Setup")
				       .First(m => m.IsGenericMethod)
				       .MakeGenericMethod(returnType);
			}
			else
			{
				//.FluentMock(mock => mock.Solution)
				Guard.Mockable(returnType);
				return FluentMockMethod.MakeGenericMethod(objectType, returnType);
			}
		}

		// Args like: string IFoo (mock => mock.Value)
		private Expression TranslateFluent(Type objectType,
		                                   Type returnType,
		                                   MethodInfo targetMethod,
		                                   Expression instance,
		                                   ParameterExpression lambdaParam,
		                                   Expression lambdaBody)
		{
			var funcType = typeof(Func<,>).MakeGenericType(objectType, returnType);

			if (targetMethod.IsStatic)
			{
				// This is the fluent extension method one, so pass the instance as one more arg.
				return Expression.Call(targetMethod, instance, Expression.Lambda(funcType, lambdaBody, lambdaParam));
			}
			else
			{
				return Expression.Call(instance, targetMethod, Expression.Lambda(funcType, lambdaBody, lambdaParam));
			}
		}

		/// <summary>
		/// Retrieves a fluent mock from the given setup expression.
		/// </summary>
		private static Mock<TResult> FluentMock<T, TResult>(Mock<T> mock, Expression<Func<T, TResult>> setup)
			where T : class
			where TResult : class
		{
			Guard.NotNull(mock, nameof(mock));
			Guard.NotNull(setup, nameof(setup));
			Guard.Mockable(typeof(TResult));

			MethodInfo info;
			IReadOnlyList<Expression> arguments;
			if (setup.Body.NodeType == ExpressionType.MemberAccess)
			{
				var memberExpr = ((MemberExpression)setup.Body);
				Guard.NotField(memberExpr);

				info = ((PropertyInfo)memberExpr.Member).GetGetMethod();
				arguments = new Expression[0];
			}
			else if (setup.Body.NodeType == ExpressionType.Call)
			{
				var callExpr = (MethodCallExpression)setup.Body;

				info = callExpr.Method;
				arguments = callExpr.Arguments;
			}
			else
			{
				throw new NotSupportedException(string.Format(Resources.UnsupportedExpression, setup.ToStringFixed()));
			}

			Guard.Mockable(info.ReturnType);

			Mock fluentMock;
			object result;
			if (mock.Setups.GetInnerMockSetups().TryFind(new InvocationShape(setup, info, arguments), out var inner))
			{
				Debug.Assert(inner.TryGetReturnValue(out _));  // guaranteed by .GetInnerMockSetups()

				fluentMock = inner.GetInnerMock();
				_ = inner.TryGetReturnValue(out result);
			}
			else
			{
				result = mock.GetDefaultValue(info, out fluentMock, useAlternateProvider: DefaultValueProvider.Mock);
				Debug.Assert(fluentMock != null);

				Mock.SetupAllProperties(fluentMock);
			}

			mock.AddInnerMockSetup(info, arguments, setup, result);

			return (Mock<TResult>)fluentMock;
		}
	}
}
