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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Moq.Diagnostics.Errors;
using Moq.Language.Flow;
using Moq.Properties;
using Moq.Proxy;

namespace Moq
{
	/// <include file='Mock.xdoc' path='docs/doc[@for="Mock"]/*'/>
	public abstract partial class Mock : IFluentInterface
	{
		internal static IProxyFactory ProxyFactory => CastleProxyFactory.Instance;

		private bool isInitialized;
		private EventHandlerCollection eventHandlers;
		private InvocationCollection invocations;
		private SetupCollection setups;
		private Switches switches;

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.ctor"]/*'/>
		protected Mock()
		{
			this.eventHandlers = new EventHandlerCollection();
			this.ImplementedInterfaces = new List<Type>();
			this.InnerMocks = new ConcurrentDictionary<MethodInfo, MockWithWrappedMockObject>();
			this.invocations = new InvocationCollection();
			this.setups = new SetupCollection();
			this.switches = Switches.Default;
		}

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Get"]/*'/>
		public static Mock<T> Get<T>(T mocked) where T : class
		{
			var mockedOfT = mocked as IMocked<T>;
			if (mockedOfT != null)
			{
				// This would be the fastest check.
				return mockedOfT.Mock;
			}

			var aDelegate = mocked as Delegate;
			if (aDelegate != null)
			{
				var mockedDelegateImpl = aDelegate.Target as IMocked<T>;
				if (mockedDelegateImpl != null)
					return mockedDelegateImpl.Mock;
			}

			var mockedPlain = mocked as IMocked;
			if (mockedPlain != null)
			{
				// We may have received a T of an implemented 
				// interface in the mock.
				var mock = mockedPlain.Mock;
				var imockedType = mocked.GetType().GetTypeInfo().ImplementedInterfaces.Single(i => i.Name.Equals("IMocked`1", StringComparison.Ordinal));
				var mockedType = imockedType.GetGenericArguments()[0];

				if (mock.ImplementedInterfaces.Contains(typeof(T)))
				{
					return mock.As<T>();
				}

				// Alternatively, we may have been asked 
				// for a type that is assignable to the 
				// one for the mock.
				// This is not valid as generic types 
				// do not support covariance on 
				// the generic parameters.
				var types = string.Join(
					", ",
					new[] {mockedType}
						// Ignore internally defined IMocked<T>
						.Concat(mock.ImplementedInterfaces.Where(t => t != imockedType))
						.Select(t => t.Name)
						.ToArray());

				throw new ArgumentException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.InvalidMockGetType,
					typeof(T).Name,
					types));
			}

			throw new ArgumentException(Resources.ObjectInstanceNotMock, "mocked");
		}
		
		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Verify"]/*'/>
		public static void Verify(params Mock[] mocks)
		{
			foreach (var mock in mocks)
			{
				mock.Verify();
			}
		}
		
		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.VerifyAll"]/*'/>
		public static void VerifyAll(params Mock[] mocks)
		{
			foreach (var mock in mocks)
			{
				mock.VerifyAll();
			}
		}

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Behavior"]/*'/>
		public virtual MockBehavior Behavior { get; internal set; }

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.CallBase"]/*'/>
		public virtual bool CallBase { get; set; }

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.DefaultValue"]/*'/>
		public DefaultValue DefaultValue
		{
			get
			{
				return this.DefaultValueProvider.Kind;
			}
			set
			{
				switch (value)
				{
					case DefaultValue.Empty:
						this.DefaultValueProvider = DefaultValueProvider.Empty;
						return;

					case DefaultValue.Mock:
						this.DefaultValueProvider = DefaultValueProvider.Mock;
						return;

					default:
						throw new ArgumentOutOfRangeException(nameof(value));
				}
			}
		}

		internal virtual EventHandlerCollection EventHandlers => this.eventHandlers;

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Object"]/*'/>
		[SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "Object", Justification = "Exposes the mocked object instance, so it's appropriate.")]
		[SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods", Justification = "The public Object property is the only one visible to Moq consumers. The protected member is for internal use only.")]
		public object Object
		{
			get { return this.GetObject(); }
		}

		private object GetObject()
		{
			var value = this.OnGetObject();
			this.isInitialized = true;
			return value;
		}

		internal virtual ConcurrentDictionary<MethodInfo, MockWithWrappedMockObject> InnerMocks { get; private set; }

		internal virtual InvocationCollection Invocations => this.invocations;

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.OnGetObject"]/*'/>
		[SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "This is actually the protected virtual implementation of the property Object.")]
		protected abstract object OnGetObject();

		/// <summary>
		/// Retrieves the type of the mocked object, its generic type argument.
		/// This is used in the auto-mocking of hierarchy access.
		/// </summary>
		internal abstract Type MockedType { get; }

		/// <summary>
		/// If this is a mock of a delegate, this property contains the method
		/// on the autogenerated interface so that we can convert setup + verify
		/// expressions on the delegate into expressions on the interface proxy.
		/// </summary>
		internal abstract MethodInfo DelegateInterfaceMethod { get; }

		/// <summary>
		/// Allows to check whether expression conversion to the <see cref="DelegateInterfaceMethod"/>
		/// must be performed on the mock, without causing unnecessarily early initialization of
		/// the mock instance, which breaks As{T}.
		/// </summary>
		internal abstract bool IsDelegateMock { get; }

		/// <summary>
		/// Gets or sets the <see cref="DefaultValueProvider"/> instance that will be used
		/// e. g. to produce default return values for unexpected invocations.
		/// </summary>
		public abstract DefaultValueProvider DefaultValueProvider { get; set; }

		/// <summary>
		/// Exposes the list of extra interfaces implemented by the mock.
		/// </summary>
		internal List<Type> ImplementedInterfaces { get; private set; }

		/// <summary>
		/// Indicates the number of interfaces in <see cref="ImplementedInterfaces"/> that were
		/// defined internally, rather than through calls to <see cref="As{TInterface}"/>.
		/// </summary>
		internal protected int InternallyImplementedInterfaceCount { get; protected set; }

		internal virtual SetupCollection Setups => this.setups;

		/// <summary>
		/// A set of switches that influence how this mock will operate.
		/// You can opt in or out of certain features via this property.
		/// </summary>
		public virtual Switches Switches
		{
			get => this.switches;
			set => this.switches = value;
		}

		internal abstract Type TargetType { get; }

		#region Verify

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.Verify"]/*'/>
		public void Verify()
		{
			if (!this.TryVerify(out UnmatchedSetups error))
			{
				throw error.AsMockException();
			}
		}

		private bool TryVerify(out UnmatchedSetups error)
		{
			var uninvokedVerifiableSetups = this.Setups.ToArrayLive(setup => setup.IsVerifiable && !setup.Invoked);
			if (uninvokedVerifiableSetups.Length > 0)
			{
				error = new UnmatchedSetups(uninvokedVerifiableSetups);
				return false;
			}

			foreach (var inner in this.InnerMocks.Values)
			{
				if (!inner.Mock.TryVerify(out error))
				{
					return false;
				}
			}

			error = null;
			return true;
		}

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.VerifyAll"]/*'/>		
		public void VerifyAll()
		{
			if (!this.TryVerifyAll(out UnmatchedSetups error))
			{
				throw error.AsMockException();
			}
		}

		private bool TryVerifyAll(out UnmatchedSetups error)
		{
			var uninvokedSetups = this.Setups.ToArrayLive(setup => !setup.Invoked);
			if (uninvokedSetups.Length > 0)
			{
				error = new UnmatchedSetups(uninvokedSetups);
				return false;
			}

			foreach (var inner in this.InnerMocks.Values)
			{
				if (!inner.Mock.TryVerifyAll(out error))
				{
					return false;
				}
			}

			error = null;
			return true;
		}

		internal static void Verify<T>(
			Mock<T> mock,
			Expression<Action<T>> expression,
			Times times,
			string failMessage)
			where T : class
		{
			Guard.NotNull(times, nameof(times));

			var methodCall = expression.GetCallInfo(mock);
			var method = methodCall.Method;
			ThrowIfVerifyExpressionInvolvesUnsupportedMember(expression, method);
			var args = methodCall.Arguments.ToArray();

			var expected = new MethodCall(mock, null, expression, method, args) { FailMessage = failMessage };
			VerifyCalls(GetTargetMock(methodCall.Object, mock), expected, expression, times);
		}

		internal static void Verify<T, TResult>(
			Mock<T> mock,
			Expression<Func<T, TResult>> expression,
			Times times,
			string failMessage)
			where T : class
		{
			Guard.NotNull(times, nameof(times));

			if (expression.IsProperty())
			{
				VerifyGet<T, TResult>(mock, expression, times, failMessage);
			}
			else
			{
				var methodCall = expression.GetCallInfo(mock);
				var method = methodCall.Method;
				ThrowIfVerifyExpressionInvolvesUnsupportedMember(expression, method);
				var args = methodCall.Arguments.ToArray();

				var expected = new MethodCallReturn<T, TResult>(mock, null, expression, method, args)
				{
					FailMessage = failMessage
				};
				VerifyCalls(GetTargetMock(methodCall.Object, mock), expected, expression, times);
			}
		}

		internal static void VerifyGet<T, TProperty>(
			Mock<T> mock,
			Expression<Func<T, TProperty>> expression,
			Times times,
			string failMessage)
			where T : class
		{
			var method = expression.ToPropertyInfo().GetGetMethod(true);
			ThrowIfVerifyExpressionInvolvesUnsupportedMember(expression, method);

			var expected = new MethodCallReturn<T, TProperty>(mock, null, expression, method, new Expression[0])
			{
				FailMessage = failMessage
			};
			VerifyCalls(GetTargetMock(((MemberExpression)expression.Body).Expression, mock), expected, expression, times);
		}

		internal static void VerifySet<T>(
			Mock<T> mock,
			Action<T> setterExpression,
			Times times,
			string failMessage)
			where T : class
		{
			Mock targetMock = null;
			Expression expression = null;
			var expected = SetupSetImpl<T, MethodCall<T>>(mock, setterExpression, (m, expr, method, value) =>
				{
					targetMock = m;
					expression = expr;
					return new MethodCall<T>(m, null, expr, method, value) { FailMessage = failMessage };
				});

			VerifyCalls(targetMock, expected, expression, times);
		}

		private static bool AreSameMethod(Expression left, Expression right)
		{
			var leftLambda = left.ToLambda();
			var rightLambda = right.ToLambda();
			if (leftLambda != null && rightLambda != null &&
				leftLambda.Body is MethodCallExpression && rightLambda.Body is MethodCallExpression)
			{
				return leftLambda.ToMethodCall().Method == rightLambda.ToMethodCall().Method;
			}

			return false;
		}

		private static void VerifyCalls(
			Mock targetMock,
			MethodCall expected,
			Expression expression,
			Times times)
		{
			var actualCalls = targetMock.Invocations.ToArray();

			var callCount = actualCalls.Count(expected.Matches);
			if (!times.Verify(callCount))
			{
				var setups = targetMock.Setups.ToArrayLive(oc => AreSameMethod(oc.SetupExpression, expression));
				ThrowVerifyException(expected, setups, actualCalls, expression, times, callCount);
			}
		}

		private static void ThrowVerifyException(
			MethodCall expected,
			IEnumerable<IProxyCall> setups,
			IEnumerable<ICallContext> actualCalls,
			Expression expression,
			Times times,
			int callCount)
		{
			var message = times.GetExceptionMessage(expected.FailMessage, expression.PartialMatcherAwareEval().ToLambda().ToStringFixed(), callCount) +
				Environment.NewLine + FormatSetupsInfo(setups) +
				Environment.NewLine + FormatInvocations(actualCalls);
			throw new MockException(MockException.ExceptionReason.VerificationFailed, message);
		}

		private static string FormatSetupsInfo(IEnumerable<IProxyCall> setups)
		{
			var expressionSetups = setups
				.Select(s => s.Format())
				.ToArray();

			return expressionSetups.Length == 0 ?
				Resources.NoSetupsConfigured :
				Environment.NewLine + string.Format(Resources.ConfiguredSetups, Environment.NewLine + string.Join(Environment.NewLine, expressionSetups));
		}

		private static string FormatInvocations(IEnumerable<ICallContext> invocations)
		{
			var formattedInvocations = invocations
				.Select(i => i.Format())
				.ToArray();

			return formattedInvocations.Length == 0 ?
				Resources.NoInvocationsPerformed :
				Environment.NewLine + string.Format(Resources.PerformedInvocations, Environment.NewLine + string.Join(Environment.NewLine, formattedInvocations));
		}

		#endregion

		#region Setup

		[DebuggerStepThrough]
		internal static MethodCall<T> Setup<T>(Mock<T> mock, Expression<Action<T>> expression, Condition condition)
			where T : class
		{
			return PexProtector.Invoke(SetupPexProtected, mock, expression, condition);
		}

		private static MethodCall<T> SetupPexProtected<T>(Mock<T> mock, Expression<Action<T>> expression, Condition condition)
			where T : class
		{
			var methodCall = expression.GetCallInfo(mock);
			var method = methodCall.Method;
			var args = methodCall.Arguments.ToArray();

			ThrowIfSetupExpressionInvolvesUnsupportedMember(expression, method);
			ThrowIfSetupMethodNotVisibleToProxyFactory(method);
			var setup = new MethodCall<T>(mock, condition, expression, method, args);

			var targetMock = GetTargetMock(methodCall.Object, mock);
			targetMock.Setups.Add(setup);

			return setup;
		}

		[DebuggerStepThrough]
		internal static MethodCallReturn<T, TResult> Setup<T, TResult>(
			Mock<T> mock,
			Expression<Func<T, TResult>> expression,
			Condition condition)
			where T : class
		{
			return PexProtector.Invoke(SetupPexProtected, mock, expression, condition);
		}

		private static MethodCallReturn<T, TResult> SetupPexProtected<T, TResult>(
			Mock<T> mock,
			Expression<Func<T, TResult>> expression,
			Condition condition)
			where T : class
		{
			if (expression.IsProperty())
			{
				return SetupGet(mock, expression, condition);
			}

			var methodCall = expression.GetCallInfo(mock);
			var method = methodCall.Method;
			var args = methodCall.Arguments.ToArray();

			ThrowIfSetupExpressionInvolvesUnsupportedMember(expression, method);
			ThrowIfSetupMethodNotVisibleToProxyFactory(method);
			var setup = new MethodCallReturn<T, TResult>(mock, condition, expression, method, args);

			var targetMock = GetTargetMock(methodCall.Object, mock);
			targetMock.Setups.Add(setup);

			return setup;
		}

		[DebuggerStepThrough]
		internal static MethodCallReturn<T, TProperty> SetupGet<T, TProperty>(
			Mock<T> mock,
			Expression<Func<T, TProperty>> expression,
			Condition condition)
			where T : class
		{
			return PexProtector.Invoke(SetupGetPexProtected, mock, expression, condition);
		}

		private static MethodCallReturn<T, TProperty> SetupGetPexProtected<T, TProperty>(
			Mock<T> mock,
			Expression<Func<T, TProperty>> expression,
			Condition condition)
			where T : class
		{
			if (expression.IsPropertyIndexer())
			{
				// Treat indexers as regular method invocations.
				return Setup<T, TProperty>(mock, expression, condition);
			}

			var prop = expression.ToPropertyInfo();
			ThrowIfPropertyNotReadable(prop);

			var propGet = prop.GetGetMethod(true);
			ThrowIfSetupExpressionInvolvesUnsupportedMember(expression, propGet);
			ThrowIfSetupMethodNotVisibleToProxyFactory(propGet);

			var setup = new MethodCallReturn<T, TProperty>(mock, condition, expression, propGet, new Expression[0]);
			// Directly casting to MemberExpression is fine as ToPropertyInfo would throw if it wasn't
			var targetMock = GetTargetMock(((MemberExpression)expression.Body).Expression, mock);
			targetMock.Setups.Add(setup);

			return setup;
		}

		[DebuggerStepThrough]
		internal static SetterMethodCall<T, TProperty> SetupSet<T, TProperty>(
			Mock<T> mock,
			Action<T> setterExpression,
			Condition condition)
			where T : class
		{
			return PexProtector.Invoke(SetupSetPexProtected<T, TProperty>, mock, setterExpression, condition);
		}

		private static SetterMethodCall<T, TProperty> SetupSetPexProtected<T, TProperty>(
			Mock<T> mock,
			Action<T> setterExpression,
			Condition condition)
			where T : class
		{
			return SetupSetImpl<T, SetterMethodCall<T, TProperty>>(
				mock,
				setterExpression,
				(m, expr, method, value) =>
				{
					var setup = new SetterMethodCall<T, TProperty>(m, condition, expr, method, value[0]);
					m.Setups.Add(setup);
					return setup;
				});
		}

		[DebuggerStepThrough]
		internal static MethodCall<T> SetupSet<T>(Mock<T> mock, Action<T> setterExpression, Condition condition)
			where T : class
		{
			return PexProtector.Invoke(SetupSetPexProtected, mock, setterExpression, condition);
		}

		private static MethodCall<T> SetupSetPexProtected<T>(Mock<T> mock, Action<T> setterExpression, Condition condition)
			where T : class
		{
			return SetupSetImpl<T, MethodCall<T>>(
				mock,
				setterExpression,
				(m, expr, method, values) =>
				{
					var setup = new MethodCall<T>(m, condition, expr, method, values);
					m.Setups.Add(setup);
					return setup;
				});
		}

		internal static SetterMethodCall<T, TProperty> SetupSet<T, TProperty>(
			Mock<T> mock,
			Expression<Func<T, TProperty>> expression)
			where T : class
		{
			var prop = expression.ToPropertyInfo();
			ThrowIfPropertyNotWritable(prop);

			var propSet = prop.GetSetMethod(true);
			ThrowIfSetupExpressionInvolvesUnsupportedMember(expression, propSet);
			ThrowIfSetupMethodNotVisibleToProxyFactory(propSet);

			var setup = new SetterMethodCall<T, TProperty>(mock, expression, propSet);
			var targetMock = GetTargetMock(((MemberExpression)expression.Body).Expression, mock);

			targetMock.Setups.Add(setup);

			return setup;
		}

		private static TCall SetupSetImpl<T, TCall>(
			Mock<T> mock,
			Action<T> setterExpression,
			Func<Mock, Expression, MethodInfo, Expression[], TCall> callFactory)
			where T : class
			where TCall : MethodCall
		{
			using (var context = new FluentMockContext())
			{
				setterExpression(mock.Object);

				var last = context.LastInvocation;
				if (last == null)
				{
					throw new ArgumentException(string.Format(
						CultureInfo.InvariantCulture,
						Resources.SetupOnNonVirtualMember,
						string.Empty));
				}

				var setter = last.Invocation.Method;
				if (!setter.IsPropertySetter())
				{
					throw new ArgumentException(Resources.SetupNotSetter);
				}

				// No need to call ThrowIfCantOverride as non-overridable would have thrown above already.

				// Get the variable name as used in the actual delegate :)
				// because of delegate currying, look at the last parameter for the Action's backing method, not the first
				var setterExpressionParameters = setterExpression.GetMethodInfo().GetParameters();
				var parameterName = setterExpressionParameters[setterExpressionParameters.Length - 1].Name;
				var x = Expression.Parameter(last.Invocation.Method.DeclaringType, parameterName);

				var arguments = last.Invocation.Arguments;
				var parameters = setter.GetParameters();
				var values = new Expression[arguments.Length];

				if (last.Match == null)
				{
					// Length == 1 || Length == 2 (Indexer property)
					for (int i = 0; i < arguments.Length; i++)
					{
						values[i] = GetValueExpression(arguments[i], parameters[i].ParameterType);
					}

					var lambda = Expression.Lambda(
						typeof(Action<>).MakeGenericType(x.Type),
						Expression.Call(x, last.Invocation.Method, values),
						x);

					return callFactory(last.Mock, lambda, last.Invocation.Method, values);
				}
				else
				{
					var matchers = new Expression[arguments.Length];
					var valueIndex = arguments.Length - 1;
					var propertyType = setter.GetParameters()[valueIndex].ParameterType;

					// If the value matcher is not equal to the property 
					// type (i.e. prop is int?, but you use It.IsAny<int>())
					// add a cast.
					if (last.Match.RenderExpression.Type != propertyType)
					{
						values[valueIndex] = Expression.Convert(last.Match.RenderExpression, propertyType);
					}
					else
					{
						values[valueIndex] = last.Match.RenderExpression;
					}

					matchers[valueIndex] = new MatchExpression(last.Match);

					if (arguments.Length == 2)
					{
						// TODO: what about multi-index setters?
						// Add the index value for the property indexer
						values[0] = GetValueExpression(arguments[0], parameters[0].ParameterType);
						// TODO: No matcher supported now for the index
						matchers[0] = values[0];
					}

					var lambda = Expression.Lambda(
						typeof(Action<>).MakeGenericType(x.Type),
						Expression.Call(x, last.Invocation.Method, values),
						x);

					return callFactory(last.Mock, lambda, last.Invocation.Method, matchers);
				}
			}
		}

		private static Expression GetValueExpression(object value, Type type)
		{
			if (value != null && value.GetType() == type)
			{
				return Expression.Constant(value);
			}

			// Add a cast if values do not match exactly (i.e. for Nullable<T>)
			return Expression.Convert(Expression.Constant(value), type);
		}

		internal static SetupSequencePhrase<TResult> SetupSequence<TResult>(Mock mock, LambdaExpression expression)
		{
			if (expression.IsProperty())
			{
				var prop = expression.ToPropertyInfo();
				ThrowIfPropertyNotReadable(prop);

				var propGet = prop.GetGetMethod(true);
				ThrowIfSetupExpressionInvolvesUnsupportedMember(expression, propGet);
				ThrowIfSetupMethodNotVisibleToProxyFactory(propGet);

				var setup = new SequenceMethodCall(mock, expression, propGet, new Expression[0]);
				var targetMock = GetTargetMock(((MemberExpression)expression.Body).Expression, mock);
				targetMock.Setups.Add(setup);
				return new SetupSequencePhrase<TResult>(setup);
			}
			else
			{
				var methodCall = expression.GetCallInfo(mock);
				var setup = new SequenceMethodCall(mock, expression, methodCall.Method, methodCall.Arguments.ToArray());
				var targetMock = GetTargetMock(methodCall.Object, mock);
				targetMock.Setups.Add(setup);
				return new SetupSequencePhrase<TResult>(setup);
			}
		}

		internal static SetupSequencePhrase SetupSequence(Mock mock, LambdaExpression expression)
		{
			var methodCall = expression.GetCallInfo(mock);
			var setup = new SequenceMethodCall(mock, expression, methodCall.Method, methodCall.Arguments.ToArray());
			var targetMock = GetTargetMock(methodCall.Object, mock);
			targetMock.Setups.Add(setup);
			return new SetupSequencePhrase(setup);
		}

		[DebuggerStepThrough]
		internal static void SetupAllProperties(Mock mock)
		{
			PexProtector.Invoke(SetupAllPropertiesPexProtected, mock);
		}

		private static void SetupAllPropertiesPexProtected(Mock mock)
		{
			var mockedTypesStack = new Stack<Type>();
			SetupAllProperties(mock, mockedTypesStack);
		}

		private static void SetupAllProperties(Mock mock, Stack<Type> mockedTypesStack)
		{
			var mockType = mock.MockedType;
			mockedTypesStack.Push(mockType);

			var properties =
				mockType
				.GetAllPropertiesInDepthFirstOrder()
				// ^ Depth-first traversal is important because properties in derived interfaces
				//   that shadow properties in base interfaces should be set up last. This
				//   enables the use case where a getter-only property is redeclared in a derived
				//   interface as a getter-and-setter property.
				.Where(p =>
					   p.CanRead && p.CanOverrideGet() &&
					   p.CanWrite == p.CanOverrideSet() &&
					   // ^ This condition will be true for two kinds of properties:
					   //    (a) those that are read-only; and
					   //    (b) those that are writable and whose setter can be overridden.
					   p.GetIndexParameters().Length == 0 &&
					   ProxyFactory.IsMethodVisible(p.GetGetMethod(), out _))
				.Distinct();

			var setupPropertyMethod = mock.GetType().GetMethods("SetupProperty")
				.First(m => m.GetParameters().Length == 2);
			var setupGetMethod = mock.GetType().GetMethods("SetupGet")
				.First(m => m.GetParameters().Length == 1);

			foreach (var property in properties)
			{
				var expression = GetPropertyExpression(mockType, property);
				object initialValue = GetInitialValue(mock, mockedTypesStack, property);

				var mocked = initialValue as IMocked;
				if (mocked != null)
				{
					SetupAllProperties(mocked.Mock, mockedTypesStack);
				}

				if (property.CanWrite)
				{
					setupPropertyMethod.MakeGenericMethod(property.PropertyType)
						.Invoke(mock, new[] { expression, initialValue });
				}
				else
				{
					var genericSetupGetMethod = setupGetMethod.MakeGenericMethod(property.PropertyType);
					var returnsMethod =
						genericSetupGetMethod
							.ReturnType
							.GetTypeInfo()
							.ImplementedInterfaces
							.SingleOrDefault(i => i.Name.Equals("IReturnsGetter`2", StringComparison.OrdinalIgnoreCase))
							.GetTypeInfo()
							.DeclaredMethods
							.SingleOrDefault(m => m.Name == "Returns" && m.GetParameterTypes().Count() == 1 && m.GetParameterTypes().First() == property.PropertyType);

					var returnsGetter = genericSetupGetMethod.Invoke(mock, new[] {expression});
					returnsMethod.Invoke(returnsGetter, new[] {initialValue});
				}
			}

			mockedTypesStack.Pop();
		}

		private static object GetInitialValue(Mock mock, Stack<Type> mockedTypesStack, PropertyInfo property)
		{
			var valueProvider = mock.DefaultValueProvider;

			if (mockedTypesStack.Contains(property.PropertyType))
			{
				// to deal with loops in the property graph
				valueProvider = DefaultValueProvider.Empty;
			}
#if FEATURE_SERIALIZATION
			else
			{
				// to make sure that properties of types that don't implement ISerializable properly (Castle throws ArgumentException)
				// are mocked with default value instead.
				// It will only result in exception if the properties are accessed.
				valueProvider = new SerializableTypesValueProvider(valueProvider);
			}
#endif
			return mock.GetDefaultValue(property.GetGetMethod(), useAlternateProvider: valueProvider);
		}

		private static Expression GetPropertyExpression(Type mockType, PropertyInfo property)
		{
			var param = Expression.Parameter(mockType, "m");
			return Expression.Lambda(Expression.MakeMemberAccess(param, property), param);
		}

		/// <summary>
		/// Gets the interceptor target for the given expression and root mock, 
		/// building the intermediate hierarchy of mock objects if necessary.
		/// </summary>
		private static Mock GetTargetMock(Expression fluentExpression, Mock mock)
		{
			if (fluentExpression is ParameterExpression)
			{
				// fast path for single-dot setup expressions;
				// no need for expensive lambda compilation.
				return mock;
			}

			var targetExpression = FluentMockVisitor.Accept(fluentExpression, mock);
			var targetLambda = Expression.Lambda<Func<Mock>>(Expression.Convert(targetExpression, typeof(Mock)));

			var targetObject = targetLambda.Compile()();
			return targetObject;
		}

		[SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "This is a helper method for the one receiving the expression.")]
		private static void ThrowIfPropertyNotWritable(PropertyInfo prop)
		{
			if (!prop.CanWrite)
			{
				throw new ArgumentException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.PropertyNotWritable,
					prop.DeclaringType.Name,
					prop.Name), "expression");
			}
		}

		private static void ThrowIfPropertyNotReadable(PropertyInfo prop)
		{
			// If property is not readable, the compiler won't let 
			// the user to specify it in the lambda :)
			// This is just reassuring that in case they build the 
			// expression tree manually?
			if (!prop.CanRead)
			{
				throw new ArgumentException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.PropertyNotReadable,
					prop.DeclaringType.Name,
					prop.Name));
			}
		}

		private static void ThrowIfSetupMethodNotVisibleToProxyFactory(MethodInfo method)
		{
			if (Mock.ProxyFactory.IsMethodVisible(method, out string messageIfNotVisible) == false)
			{
				throw new ArgumentException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.MethodNotVisibleToProxyFactory,
					method.DeclaringType.Name,
					method.Name,
					messageIfNotVisible));
			}
		}

		private static void ThrowIfSetupExpressionInvolvesUnsupportedMember(Expression setup, MethodInfo method)
		{
			if (method.IsStatic)
			{
				throw new NotSupportedException(string.Format(
					CultureInfo.CurrentCulture,
					method.IsExtensionMethod() ? Resources.SetupOnExtensionMethod : Resources.SetupOnStaticMember,
					setup.ToStringFixed()));
			}
			else if (!method.CanOverride())
			{
				throw new NotSupportedException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.SetupOnNonVirtualMember,
					setup.ToStringFixed()));
			}
		}

		private static void ThrowIfVerifyExpressionInvolvesUnsupportedMember(Expression verify, MethodInfo method)
		{
			if (method.IsStatic)
			{
				throw new NotSupportedException(string.Format(
					CultureInfo.CurrentCulture,
					method.IsExtensionMethod() ? Resources.VerifyOnExtensionMethod : Resources.VerifyOnStaticMember,
					verify.ToStringFixed()));
			}
			else if (!method.CanOverride())
			{
				throw new NotSupportedException(string.Format(
					CultureInfo.CurrentCulture,
					Resources.VerifyOnNonVirtualMember,
					verify.ToStringFixed()));
			}
		}

		private class FluentMockVisitor : ExpressionVisitor
		{
			static readonly MethodInfo FluentMockGenericMethod = ((Func<Mock<string>, Expression<Func<string, string>>, Mock<string>>)
				QueryableMockExtensions.FluentMock<string, string>).GetMethodInfo().GetGenericMethodDefinition();
			static readonly MethodInfo MockGetGenericMethod = ((Func<string, Mock<string>>)Moq.Mock.Get<string>)
				.GetMethodInfo().GetGenericMethodDefinition();

			Expression expression;
			Mock mock;

			public FluentMockVisitor(Expression expression, Mock mock)
			{
				this.expression = expression;
				this.mock = mock;
			}

			public static Expression Accept(Expression expression, Mock mock)
			{
				return new FluentMockVisitor(expression, mock).Accept();
			}

			public Expression Accept()
			{
				return Visit(expression);
			}

			protected override Expression VisitParameter(ParameterExpression p)
			{
				// the actual first object being used in a fluent expression, 
				// which will be against the actual mock rather than 
				// the parameter.
				return Expression.Constant(mock);
			}

			protected override Expression VisitMethodCall(MethodCallExpression node)
			{
				if (node == null)
				{
					return null;
				}

				var lambdaParam = Expression.Parameter(node.Object.Type, "mock");
				Expression lambdaBody = Expression.Call(lambdaParam, node.Method, node.Arguments);
				var targetMethod = GetTargetMethod(node.Object.Type, node.Method.ReturnType);

				return TranslateFluent(
					node.Object.Type,
					node.Method.ReturnType,
					targetMethod,
					this.Visit(node.Object),
					lambdaParam,
					lambdaBody);
			}

			protected override Expression VisitMember(MemberExpression node)
			{
				if (node == null)
				{
					return null;
				}

				// Translate differently member accesses over transparent
				// compiler-generated types as they are typically the 
				// anonymous types generated to build up the query expressions.
				if (node.Expression.NodeType == ExpressionType.Parameter &&
					node.Expression.Type.GetTypeInfo().IsDefined(typeof(CompilerGeneratedAttribute), false))
				{
					var memberType = node.Member is FieldInfo ?
						((FieldInfo)node.Member).FieldType :
						((PropertyInfo)node.Member).PropertyType;

					// Generate a Mock.Get over the entire member access rather.
					// <anonymous_type>.foo => Mock.Get(<anonymous_type>.foo)
					return Expression.Call(null,
						MockGetGenericMethod.MakeGenericMethod(memberType), node);
				}

				// If member is not mock-able, actually, including being a sealed class, etc.?
				if (node.Member is FieldInfo)
					throw new NotSupportedException();

				var lambdaParam = Expression.Parameter(node.Expression.Type, "mock");
				Expression lambdaBody = Expression.MakeMemberAccess(lambdaParam, node.Member);
				var targetMethod = GetTargetMethod(node.Expression.Type, ((PropertyInfo)node.Member).PropertyType);

				return TranslateFluent(node.Expression.Type, ((PropertyInfo)node.Member).PropertyType, targetMethod, Visit(node.Expression), lambdaParam, lambdaBody);
			}

			private static Expression TranslateFluent(
				Type objectType,
				Type returnType,
				MethodInfo targetMethod,
				Expression instance,
				ParameterExpression lambdaParam,
				Expression lambdaBody)
			{
				var funcType = typeof(Func<,>).MakeGenericType(objectType, returnType);

				// This is the fluent extension method one, so pass the instance as one more arg.
				return Expression.Call(
					targetMethod,
					instance,
					Expression.Lambda(
						funcType,
						lambdaBody,
						lambdaParam
					)
				);
			}

			private static MethodInfo GetTargetMethod(Type objectType, Type returnType)
			{
				returnType.ThrowIfNotMockeable();
				return FluentMockGenericMethod.MakeGenericMethod(objectType, returnType);
			}
		}

		#endregion

		#region Raise

		/// <summary>
		/// Raises the associated event with the given 
		/// event argument data.
		/// </summary>
		internal void DoRaise(EventInfo ev, EventArgs args)
		{
			if (ev == null)
			{
				throw new InvalidOperationException(Resources.RaisedUnassociatedEvent);
			}

			foreach (var del in this.EventHandlers.ToArray(ev.Name))
			{
				del.InvokePreserveStack(this.Object, args);
			}
		}

		/// <summary>
		/// Raises the associated event with the given
		/// event argument data.
		/// </summary>
		internal void DoRaise(EventInfo ev, params object[] args)
		{
			if (ev == null)
			{
				throw new InvalidOperationException(Resources.RaisedUnassociatedEvent);
			}

			foreach (var del in this.EventHandlers.ToArray(ev.Name))
			{
				// Non EventHandler-compatible delegates get the straight
				// arguments, not the typical "sender, args" arguments.
				del.InvokePreserveStack(args);
			}
		}

		#endregion

		#region As<TInterface>

		/// <include file='Mock.xdoc' path='docs/doc[@for="Mock.As{TInterface}"]/*'/>
		[SuppressMessage("Microsoft.Naming", "CA1716:IdentifiersShouldNotMatchKeywords", MessageId = "As", Justification = "We want the method called exactly as the keyword because that's what it does, it adds an implemented interface so that you can cast it later.")]
		public virtual Mock<TInterface> As<TInterface>()
			where TInterface : class
		{
			var index = this.ImplementedInterfaces.LastIndexOf(typeof(TInterface));

			var isImplemented = index >= 0;
			if (this.isInitialized && !isImplemented)
			{
				throw new InvalidOperationException(Resources.AlreadyInitialized);
			}

			if (!typeof(TInterface).GetTypeInfo().IsInterface)
			{
				throw new ArgumentException(Resources.AsMustBeInterface);
			}

			var isNotOrInternallyImplemented = index < this.InternallyImplementedInterfaceCount - 1; // - 1 because of IMocked<>
			if (isNotOrInternallyImplemented)
			{
				// We get here for either of two reasons:
				//
				// 1. We are being asked to implement an interface that the mocked type does *not* itself
				//    inherit or implement. We need to hand this interface type to DynamicProxy's
				//    `CreateClassProxy` method as an additional interface to be implemented. Therefore we
				//    add it at the end of this list, after the "internally implemented" interfaces
				//    (i.e. those that the mocked type inherits or implements itself, plus `IMocked<>`).
				//    In this case, `index == -1`.
				//
				// 2. The user is possibly going to create a setup through an interface type that the
				//    mocked type *does* implement. Since the mocked type might implement that interface's
				//    methods non-virtually, we can only intercept those if DynamicProxy reimplements the
				//    interface in the generated proxy type. Therefore we do the same as for (1). Note
				//    that this might lead to the interface type being contained twice in the list, once
				//    as an "internally implemented" type, and once as an "additional" type. That should
				//    not matter apart from slightly higher memory consumption, but it has the benefit
				//    that we don't need to perform a non-atomic removal of the "internally implemented"
				//    item.
				//    In this case, `index >= 0 && index < this.InternallyImplementedInterfaceCount - 1`.
				this.ImplementedInterfaces.Add(typeof(TInterface));
			}

			return new AsInterface<TInterface>(this);
		}

		#endregion

		#region Default Values

		internal abstract Dictionary<Type, object> ConfiguredDefaultValues { get; }

		/// <include file='Mock.Generic.xdoc' path='docs/doc[@for="Mock.SetReturnDefault{TReturn}"]/*'/>
		public void SetReturnsDefault<TReturn>(TReturn value)
		{
			this.ConfiguredDefaultValues[typeof(TReturn)] = value;
		}

		internal object GetDefaultValue(MethodInfo method, DefaultValueProvider useAlternateProvider = null)
		{
			Debug.Assert(method != null);
			Debug.Assert(method.ReturnType != null);
			Debug.Assert(method.ReturnType != typeof(void));

			if (this.ConfiguredDefaultValues.TryGetValue(method.ReturnType, out object configuredDefaultValue))
			{
				return configuredDefaultValue;
			}

			var result = (useAlternateProvider ?? this.DefaultValueProvider).GetDefaultReturnValue(method, this);
			var unwrappedResult = TryUnwrapResultFromCompletedTaskRecursively(result);

			if (unwrappedResult is IMocked unwrappedMockedResult)
			{
				// TODO: Perhaps the following `InnerMocks` update isn't in quite the right place yet.
				// There are two main places in Moq where `InnerMocks` are used: `Mock<T>.FluentMock` and
				// the `HandleMockRecursion` interception strategy. Both places first query `InnerMocks`,
				// and if no value for a given member is present, the default value provider get invoked
				// via the present method. Querying and updating `InnerMocks` is thus spread over two
				// code locations and therefore non-atomic. It would be good if those could be combined
				// (`InnerMocks.GetOrAdd`), but that might not be easily possible since `InnerMocks` is
				// only mocks while default value providers can also return plain, unmocked values.
				this.InnerMocks.TryAdd(method, new MockWithWrappedMockObject(unwrappedMockedResult.Mock, result));
			}

			return result;
		}

		/// <summary>
		/// Recursively unwraps the result from completed <see cref="Task{TResult}"/> or <see cref="ValueTask{TResult}"/> instances.
		/// If the given value is not a task, the value itself is returned.
		/// </summary>
		/// <param name="obj">The value to be unwrapped.</param>
		private static object TryUnwrapResultFromCompletedTaskRecursively(object obj)
		{
			if (obj != null)
			{
				var objType = obj.GetType();
				if (objType.GetTypeInfo().IsGenericType)
				{
					var genericTypeDefinition = objType.GetGenericTypeDefinition();
					if (genericTypeDefinition == typeof(Task<>) || genericTypeDefinition == typeof(ValueTask<>))
					{
						var isCompleted = (bool)objType.GetProperty("IsCompleted").GetValue(obj, null);
						if (isCompleted)
						{
							var innerObj = objType.GetProperty("Result").GetValue(obj, null);
							return TryUnwrapResultFromCompletedTaskRecursively(innerObj);
						}
					}
				}
			}

			return obj;
		}

		#endregion
	}
}
