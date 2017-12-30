﻿//Copyright (c) 2007. Clarius Consulting, Manas Technology Solutions, InSTEDD
//https://github.com/moq/moq4
//All rights reserved.

//Redistribution and use in source and binary forms, 
//with or without modification, are permitted provided 
//that the following conditions are met:

//    * Redistributions of source code must retain the 
//    above copyright notice, this list of conditions and 
//    the following disclaimer.

//    * Redistributions in binary form must reproduce 
//    the above copyright notice, this list of conditions 
//    and the following disclaimer in the documentation 
//    and/or other materials provided with the distribution.

//    * Neither the name of Clarius Consulting, Manas Technology Solutions or InSTEDD nor the 
//    names of its contributors may be used to endorse 
//    or promote products derived from this software 
//    without specific prior written permission.

//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
//CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
//INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
//MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR 
//CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
//SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
//BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
//SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
//INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
//WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
//NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
//OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
//SUCH DAMAGE.

//[This is the BSD license, see
// http://www.opensource.org/licenses/bsd-license.php]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using Moq.Properties;

namespace Moq
{
	internal static class ExpressionExtensions
	{
		/// <summary>
		/// Casts the expression to a lambda expression, removing 
		/// a cast if there's any.
		/// </summary>
		public static LambdaExpression ToLambda(this Expression expression)
		{
			Guard.NotNull(expression, nameof(expression));

			LambdaExpression lambda = expression as LambdaExpression;
			if (lambda == null)
				throw new ArgumentException(String.Format(CultureInfo.CurrentCulture,
					Properties.Resources.UnsupportedExpression, expression));

			// Remove convert expressions which are passed-in by the MockProtectedExtensions.
			// They are passed because LambdaExpression constructor checks the type of 
			// the returned values, even if the return type is Object and everything 
			// is able to convert to it. It forces you to be explicit about the conversion.
			var convert = lambda.Body as UnaryExpression;
			if (convert != null && convert.NodeType == ExpressionType.Convert)
				lambda = Expression.Lambda(convert.Operand, lambda.Parameters.ToArray());

			return lambda;
		}

		/// <summary>
		/// Casts the body of the lambda expression to a <see cref="MethodCallExpression"/>.
		/// </summary>
		/// <exception cref="ArgumentException">If the body is not a method call.</exception>
		public static MethodCallExpression ToMethodCall(this LambdaExpression expression)
		{
			Guard.NotNull(expression, nameof(expression));

			var methodCall = expression.Body as MethodCallExpression;
			if (methodCall == null)
			{
				throw new ArgumentException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.SetupNotMethod,
					expression.ToStringFixed()));
			}

			return methodCall;
		}

		/// <summary>
		/// Converts the body of the lambda expression into the <see cref="PropertyInfo"/> referenced by it.
		/// </summary>
		public static PropertyInfo ToPropertyInfo(this LambdaExpression expression)
		{
			if (expression.Body is MemberExpression prop)
			{
				if (prop.Member is PropertyInfo info)
				{
					// the following block is required because .NET compilers put the wrong PropertyInfo into MemberExpression
					// for properties originally declared in base classes; they will put the base class' PropertyInfo into
					// the expression. we attempt to correct this here by checking whether the type of the accessed object
					// has a property by the same name whose base definition equals the property in the expression; if so,
					// we "upgrade" to the derived property.
					if (info.DeclaringType != prop.Expression.Type && info.CanRead)
					{
						var propertyInLeft = prop.Expression.Type.GetProperty(info.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
						if (propertyInLeft != null && propertyInLeft.GetMethod.GetBaseDefinition() == info.GetMethod)
						{
							info = propertyInLeft;
						}
					}

					return info;
				}
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
		/// Checks whether the body of the lambda expression is a property indexer, which is true 
		/// when the expression is an <see cref="MethodCallExpression"/> whose 
		/// <see cref="MethodCallExpression.Method"/> has <see cref="MethodBase.IsSpecialName"/> 
		/// equal to <see langword="true"/>.
		/// </summary>
		public static bool IsPropertyIndexer(this LambdaExpression expression)
		{
			Guard.NotNull(expression, nameof(expression));

			return expression.Body is MethodCallExpression methodCallExpression && methodCallExpression.Method.IsSpecialName;
		}

		public static Expression StripQuotes(this Expression expression)
		{
			while (expression.NodeType == ExpressionType.Quote)
			{
				expression = ((UnaryExpression)expression).Operand;
			}

			return expression;
		}

		public static Expression PartialEval(this Expression expression)
		{
			return Evaluator.PartialEval(expression);
		}

		public static Expression PartialMatcherAwareEval(this Expression expression)
		{
			return Evaluator.PartialEval(
				expression,
				PartialMatcherAwareEval_ShouldEvaluate);
		}

		private static bool PartialMatcherAwareEval_ShouldEvaluate(Expression expression)
		{
			switch (expression.NodeType)
			{
				case ExpressionType.Parameter:
					return false;

				case ExpressionType.Call:
				case ExpressionType.MemberAccess:
					// Evaluate everything but matchers:
					using (var context = new FluentMockContext())
					{
						Expression.Lambda<Action>(expression).Compile().Invoke();
						return context.LastMatch == null;
					}

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

		/// <summary>
		/// Extracts, into a common form, information from a <see cref="LambdaExpression" />
		/// around either a <see cref="MethodCallExpression" /> (for a normal method call)
		/// or a <see cref="InvocationExpression" /> (for a delegate invocation).
		/// </summary>
		internal static (Expression Object, MethodInfo Method, Expression[] Arguments) GetCallInfo(this LambdaExpression expression, Mock mock)
		{
			Guard.NotNull(expression, nameof(expression));

			if (mock.IsDelegateMock)
			{
				// We're a mock for a delegate, so this call can only
				// possibly be the result of invoking the delegate.
				// But the expression we have is for a call on the delegate, not our
				// delegate interface proxy, so we need to map instead to the
				// method on that interface, which is the property we've just tested for.
				var invocation = (InvocationExpression)expression.Body;
				return (Object: invocation.Expression, Method: mock.DelegateInterfaceMethod, Arguments: invocation.Arguments.ToArray());
			}

			var methodCall = expression.ToMethodCall();
			return (Object: methodCall.Object, Method: methodCall.Method, Arguments: methodCall.Arguments.ToArray());
		}
	}
}
