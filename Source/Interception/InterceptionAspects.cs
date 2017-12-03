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
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Moq
{
	internal sealed class HandleWellKnownMethods : InterceptionAspect
	{
		public static HandleWellKnownMethods Instance { get; } = new HandleWellKnownMethods();

		private static Dictionary<string, Func<Invocation, Mock, InterceptionAction>> specialMethods = new Dictionary<string, Func<Invocation, Mock, InterceptionAction>>()
		{
			["Equals"] = HandleEquals,
			["Finalize"] = HandleFinalize,
			["GetHashCode"] = HandleGetHashCode,
			["get_" + nameof(IMocked.Mock)] = HandleMockGetter,
			["ToString"] = HandleToString,
		};

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			if (specialMethods.TryGetValue(invocation.Method.Name, out Func<Invocation, Mock, InterceptionAction> handler))
			{
				return handler.Invoke(invocation, mock);
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static InterceptionAction HandleEquals(Invocation invocation, Mock mock)
		{
			if (IsObjectMethod(invocation.Method) && !mock.Setups.Any(c => IsObjectMethod(c.Method, "Equals")))
			{
				invocation.ReturnValue = ReferenceEquals(invocation.Arguments.First(), mock.Object);
				return InterceptionAction.Stop;
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static InterceptionAction HandleFinalize(Invocation invocation, Mock mock)
		{
			return IsFinalizer(invocation.Method) ? InterceptionAction.Stop : InterceptionAction.Continue;
		}

		private static InterceptionAction HandleGetHashCode(Invocation invocation, Mock mock)
		{
			// Only if there is no corresponding setup for `GetHashCode()`
			if (IsObjectMethod(invocation.Method) && !mock.Setups.Any(c => IsObjectMethod(c.Method, "GetHashCode")))
			{
				invocation.ReturnValue = mock.GetHashCode();
				return InterceptionAction.Stop;
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static InterceptionAction HandleToString(Invocation invocation, Mock mock)
		{
			// Only if there is no corresponding setup for `ToString()`
			if (IsObjectMethod(invocation.Method) && !mock.Setups.Any(c => IsObjectMethod(c.Method, "ToString")))
			{
				invocation.ReturnValue = mock.ToString() + ".Object";
				return InterceptionAction.Stop;
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static InterceptionAction HandleMockGetter(Invocation invocation, Mock mock)
		{
			if (typeof(IMocked).IsAssignableFrom(invocation.Method.DeclaringType))
			{
				invocation.ReturnValue = mock;
				return InterceptionAction.Stop;
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static bool IsFinalizer(MethodInfo method)
		{
			return method.GetBaseDefinition() == typeof(object).GetMethod("Finalize", BindingFlags.NonPublic | BindingFlags.Instance);
		}

		private static bool IsObjectMethod(MethodInfo method) => method.DeclaringType == typeof(object);

		private static bool IsObjectMethod(MethodInfo method, string name) => IsObjectMethod(method) && method.Name == name;
	}

	internal sealed class ProduceDefaultReturnValue : InterceptionAspect
	{
		public static ProduceDefaultReturnValue Instance { get; } = new ProduceDefaultReturnValue();

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			Debug.Assert(invocation.Method != null);
			Debug.Assert(invocation.Method.ReturnType != null);

			if (invocation.Method.ReturnType != typeof(void))
			{
				MockWithWrappedMockObject recursiveMock;
				if (mock.InnerMocks.TryGetValue(invocation.Method, out recursiveMock))
				{
					invocation.ReturnValue = recursiveMock.WrappedMockObject;
				}
				else
				{
					invocation.ReturnValue = mock.GetDefaultValue(invocation.Method);
				}
				return InterceptionAction.Stop;
			}
			return InterceptionAction.Continue;
		}
	}

	internal sealed class InvokeBase : InterceptionAspect
	{
		public static InvokeBase Instance { get; } = new InvokeBase();

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			if (invocation.Method.DeclaringType == typeof(object) || // interface proxy
				mock.ImplementsInterface(invocation.Method.DeclaringType) && !invocation.Method.LooksLikeEventAttach() && !invocation.Method.LooksLikeEventDetach() && mock.CallBase && !mock.MockedType.GetTypeInfo().IsInterface || // class proxy with explicitly implemented interfaces. The method's declaring type is the interface and the method couldn't be abstract
				invocation.Method.DeclaringType.GetTypeInfo().IsClass && !invocation.Method.IsAbstract && mock.CallBase // class proxy
				)
			{
				// Invoke underlying implementation.

				// For mocked classes, if the target method was not abstract, 
				// invoke directly.
				// Will only get here for Loose behavior.
				// TODO: we may want to provide a way to skip this by the user.
				invocation.InvokeBase();
				return InterceptionAction.Stop;
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}
	}

	internal sealed class FindAndExecuteMatchingSetup : InterceptionAspect
	{
		public static FindAndExecuteMatchingSetup Instance { get; } = new FindAndExecuteMatchingSetup();

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			if (FluentMockContext.IsActive)
			{
				return InterceptionAction.Continue;
			}

			var matchedSetup = mock.Setups.FindMatchFor(invocation);
			if (matchedSetup != null)
			{
				matchedSetup.EvaluatedSuccessfully();
				matchedSetup.SetOutParameters(invocation);

				// We first execute, as there may be a Throws 
				// and therefore we might never get to the 
				// next line.
				matchedSetup.Execute(invocation);
				ThrowIfReturnValueRequired(matchedSetup, invocation, mock);
				return InterceptionAction.Stop;
			}
			else if (mock.Behavior == MockBehavior.Strict)
			{
				throw new MockException(MockException.ExceptionReason.NoSetup, MockBehavior.Strict, invocation);
			}
			else
			{
				return InterceptionAction.Continue;
			}
		}

		private static void ThrowIfReturnValueRequired(IProxyCall call, Invocation invocation, Mock mock)
		{
			if (mock.Behavior != MockBehavior.Loose &&
				invocation.Method != null &&
				invocation.Method.ReturnType != null &&
				invocation.Method.ReturnType != typeof(void))
			{
				if (!(call is IMethodCallReturn methodCallReturn && methodCallReturn.ProvidesReturnValue()))
				{
					throw new MockException(
						MockException.ExceptionReason.ReturnValueRequired,
						mock.Behavior,
						invocation);
				}
			}
		}
	}

	internal sealed class HandleTracking : InterceptionAspect
	{
		public static HandleTracking Instance { get; } = new HandleTracking();

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			// Track current invocation if we're in "record" mode in a fluent invocation context.
			if (FluentMockContext.IsActive)
			{
				FluentMockContext.Current.Add(mock, invocation);
			}
			return InterceptionAction.Continue;
		}
	}

	internal sealed class RecordInvocation : InterceptionAspect
	{
		public static RecordInvocation Instance { get; } = new RecordInvocation();

		public override InterceptionAction Handle(Invocation invocation, Mock mock)
		{
			if (FluentMockContext.IsActive)
			{
				// In a fluent invocation context, which is a recorder-like
				// mode we use to evaluate delegates by actually running them,
				// we don't want to count the invocation, or actually run
				// previous setups.
				return InterceptionAction.Continue;
			}

			var methodName = invocation.Method.Name;

			// Special case for event accessors. The following, seemingly random character checks are guards against
			// more expensive checks (for the common case where the invoked method is *not* an event accessor).
			if (methodName.Length > 4)
			{
				if (methodName[0] == 'a' && methodName[3] == '_' && invocation.Method.LooksLikeEventAttach())
				{
					var eventInfo = GetEventFromName(invocation.Method.Name.Substring("add_".Length), mock);
					if (eventInfo != null)
					{
						// TODO: We could compare `invocation.Method` and `eventInfo.GetAddMethod()` here.
						// If they are equal, then `invocation.Method` is definitely an event `add` accessor.
						// Not sure whether this would work with F# and COM; see commit 44070a9.

						if (mock.CallBase && !invocation.Method.IsAbstract)
						{
							invocation.InvokeBase();
							return InterceptionAction.Stop;
						}
						else if (invocation.Arguments.Length > 0 && invocation.Arguments[0] is Delegate delegateInstance)
						{
							mock.EventHandlers.Add(eventInfo.Name, delegateInstance);
							return InterceptionAction.Stop;
						}
					}
				}
				else if (methodName[0] == 'r' && methodName.Length > 7 && methodName[6] == '_' && invocation.Method.LooksLikeEventDetach())
				{
					var eventInfo = GetEventFromName(invocation.Method.Name.Substring("remove_".Length), mock);
					if (eventInfo != null)
					{
						// TODO: We could compare `invocation.Method` and `eventInfo.GetRemoveMethod()` here.
						// If they are equal, then `invocation.Method` is definitely an event `remove` accessor.
						// Not sure whether this would work with F# and COM; see commit 44070a9.

						if (mock.CallBase && !invocation.Method.IsAbstract)
						{
							invocation.InvokeBase();
							return InterceptionAction.Stop;
						}
						else if (invocation.Arguments.Length > 0 && invocation.Arguments[0] is Delegate delegateInstance)
						{
							mock.EventHandlers.Remove(eventInfo.Name, delegateInstance);
							return InterceptionAction.Stop;
						}
					}
				}
			}

			// Save to support Verify[expression] pattern.
			mock.Invocations.Add(invocation);
			return InterceptionAction.Continue;
		}

		/// <summary>
		/// Get an eventInfo for a given event name.  Search type ancestors depth first if necessary.
		/// </summary>
		/// <param name="eventName">Name of the event, with the set_ or get_ prefix already removed</param>
		/// <param name="mock"/>
		private static EventInfo GetEventFromName(string eventName, Mock mock)
		{
			return GetEventFromName(eventName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public, mock)
				?? GetEventFromName(eventName, BindingFlags.Instance | BindingFlags.NonPublic, mock);
		}

		/// <summary>
		/// Get an eventInfo for a given event name.  Search type ancestors depth first if necessary.
		/// Searches events using the specified binding constraints.
		/// </summary>
		/// <param name="eventName">Name of the event, with the set_ or get_ prefix already removed</param>
		/// <param name="bindingAttr">Specifies how the search for events is conducted</param>
		/// <param name="mock"/>
		private static EventInfo GetEventFromName(string eventName, BindingFlags bindingAttr, Mock mock)
		{
			// Ignore inherited interfaces and internally defined IMocked<T>
			var depthFirstProgress = new Queue<Type>(mock.AdditionalInterfaces);
			depthFirstProgress.Enqueue(mock.TargetType);
			while (depthFirstProgress.Count > 0)
			{
				var currentType = depthFirstProgress.Dequeue();
				var eventInfo = currentType.GetEvent(eventName, bindingAttr);
				if (eventInfo != null)
				{
					return eventInfo;
				}

				foreach (var implementedType in GetAncestorTypes(currentType))
				{
					depthFirstProgress.Enqueue(implementedType);
				}
			}

			return null;
		}

		/// <summary>
		/// Given a type return all of its ancestors, both types and interfaces.
		/// </summary>
		/// <param name="initialType">The type to find immediate ancestors of</param>
		private static IEnumerable<Type> GetAncestorTypes(Type initialType)
		{
			var baseType = initialType.GetTypeInfo().BaseType;
			if (baseType != null)
			{
				return new[] { baseType };
			}

			return initialType.GetInterfaces();
		}
	}
}
