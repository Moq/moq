// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
#if FEATURE_SERIALIZATION
using System.Runtime.Serialization;
#endif
using System.Security;
using System.Text;

using Moq.Language;
using Moq.Properties;

namespace Moq
{
	/// <summary>
	/// Exception thrown by mocks when they are not properly set up,
	/// when setups are not matched, when verification fails, etc.
	/// </summary>
	/// <remarks>
	/// A distinct exception type is provided so that exceptions
	/// thrown by a mock can be distinguished from other exceptions
	/// that might be thrown in tests.
	/// <para>
	/// Moq does not provide a richer hierarchy of exception types, as
	/// tests typically should <em>not</em> catch or expect exceptions
	/// from mocks. These are typically the result of changes
	/// in the tested class or its collaborators' implementation, and
	/// result in fixes in the mock setup so that they disappear and
	/// allow the test to pass.
	/// </para>
	/// </remarks>
	[SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors", Justification = "It's only initialized internally.")]
#if FEATURE_SERIALIZATION
	[Serializable]
#endif
	public class MockException : Exception
	{
		/// <summary>
		///   Returns the exception to be thrown when a setup limited by <see cref="IOccurrence.AtMostOnce()"/> is invoked more often than once.
		/// </summary>
		internal static MockException MoreThanOneCall(MethodCall setup, int invocationCount)
		{
			return new MockException(
				MockExceptionReason.MoreThanOneCall,
				Times.AtMostOnce().GetExceptionMessage(setup.FailMessage, setup.Expression.ToStringFixed(), invocationCount));
		}

		/// <summary>
		///   Returns the exception to be thrown when a setup limited by <see cref="IOccurrence.AtMost(int)"/> is invoked more often than the specified maximum number of times.
		/// </summary>
		internal static MockException MoreThanNCalls(MethodCall setup, int maxInvocationCount, int invocationCount)
		{
			return new MockException(
				MockExceptionReason.MoreThanNCalls,
				Times.AtMost(maxInvocationCount).GetExceptionMessage(setup.FailMessage, setup.Expression.ToStringFixed(), invocationCount));
		}

		/// <summary>
		///   Returns the exception to be thrown when <see cref="Mock.Verify"/> finds no invocations (or the wrong number of invocations) that match the specified expectation.
		/// </summary>
		internal static MockException NoMatchingCalls(
			string failMessage,
			IEnumerable<Setup> setups,
			IEnumerable<Invocation> invocations,
			LambdaExpression expression,
			Times times,
			int callCount)
		{
			return new MockException(
				MockExceptionReason.NoMatchingCalls,
				times.GetExceptionMessage(failMessage, expression.PartialMatcherAwareEval().ToStringFixed(), callCount) +
				Environment.NewLine + FormatSetupsInfo() +
				Environment.NewLine + FormatInvocations());

			string FormatSetupsInfo()
			{
				var expressionSetups = setups.Select(s => s.ToString()).ToArray();

				return expressionSetups.Length == 0 ?
					Resources.NoSetupsConfigured :
					Environment.NewLine + string.Format(Resources.ConfiguredSetups, Environment.NewLine + string.Join(Environment.NewLine, expressionSetups));
			}

			string FormatInvocations()
			{
				return invocations.Any() ? Environment.NewLine + string.Format(Resources.PerformedInvocations, Environment.NewLine + string.Join<Invocation>(Environment.NewLine, invocations))
				                         : Resources.NoInvocationsPerformed;
			}
		}

		/// <summary>
		///   Returns the exception to be thrown when a strict mock has no setup corresponding to the specified invocation.
		/// </summary>
		internal static MockException NoSetup(Invocation invocation)
		{
			return new MockException(
				MockExceptionReason.NoSetup,
				string.Format(
					CultureInfo.CurrentCulture,
					Resources.MockExceptionMessage,
					invocation.ToString(),
					MockBehavior.Strict,
					Resources.NoSetup));
		}

		/// <summary>
		///   Returns the exception to be thrown when a strict mock has no setup that provides a return value for the specified invocation.
		/// </summary>
		internal static MockException ReturnValueRequired(Invocation invocation)
		{
			return new MockException(
				MockExceptionReason.ReturnValueRequired,
				string.Format(
					CultureInfo.CurrentCulture,
					Resources.MockExceptionMessage,
					invocation.ToString(),
					MockBehavior.Strict,
					Resources.ReturnValueRequired));
		}

		/// <summary>
		///   Returns the exception to be thrown when <see cref="Mock.Verify"/> or <see cref="MockFactory.Verify"/> find a setup that has not been invoked.
		/// </summary>
		internal static MockException UnmatchedSetups(Mock mock, IEnumerable<Setup> setups)
		{
			return new MockException(
				MockExceptionReason.UnmatchedSetups,
				string.Format(
					CultureInfo.CurrentCulture,
					Resources.UnmatchedSetups,
					mock.ToString(),
					setups.Aggregate(new StringBuilder(), (builder, setup) => builder.AppendLine(setup.ToString())).ToString()));
		}

		/// <summary>
		///   Returns the exception to be thrown when <see cref="MockFactory.Verify"/> finds several mocks with setups that have not been invoked.
		/// </summary>
		internal static MockException UnmatchedSetups(IEnumerable<MockException> errors)
		{
			Debug.Assert(errors.All(e => e.Reason == MockExceptionReason.UnmatchedSetups));

			return new MockException(
				MockExceptionReason.UnmatchedSetups,
				string.Join(
					Environment.NewLine,
					errors.Select(error => error.Message)));
		}

		/// <summary>
		///   Returns the exception to be thrown when <see cref="Mock.VerifyNoOtherCalls(Mock)"/> finds invocations that have not been verified.
		/// </summary>
		internal static MockException UnverifiedInvocations(Mock mock, IEnumerable<Invocation> invocations)
		{
			return new MockException(
				MockExceptionReason.UnverifiedInvocations,
				string.Format(
					CultureInfo.CurrentCulture,
					Resources.UnverifiedInvocations,
					mock.ToString(),
					invocations.Aggregate(new StringBuilder(), (builder, setup) => builder.AppendLine(setup.ToString())).ToString()));
		}

		/// <summary>
		///   Returns the exception to be thrown when <see cref="MockFactory.VerifyNoOtherCalls()"/> finds invocations that have not been verified.
		/// </summary>
		public static Exception UnverifiedInvocations(List<MockException> errors)
		{
			Debug.Assert(errors.All(e => e.Reason == MockExceptionReason.UnverifiedInvocations));

			return new MockException(
				MockExceptionReason.UnverifiedInvocations,
				string.Join(
					Environment.NewLine,
					errors.Select(error => error.Message)));
		}

		private MockExceptionReason reason;

		private MockException(MockExceptionReason reason, string message)
			: base(message)
		{
			this.reason = reason;
		}

		internal MockExceptionReason Reason
		{
			get { return reason; }
		}

		/// <summary>
		/// Indicates whether this exception is a verification fault raised by Verify()
		/// </summary>
		public bool IsVerificationError
		{
			get
			{
				switch (this.reason)
				{
					case MockExceptionReason.NoMatchingCalls:
					case MockExceptionReason.UnmatchedSetups:
					case MockExceptionReason.UnverifiedInvocations:
						return true;

					default:
						return false;
				}
			}
		}

#if FEATURE_SERIALIZATION
		/// <summary>
		/// Supports the serialization infrastructure.
		/// </summary>
		/// <param name="info">Serialization information.</param>
		/// <param name="context">Streaming context.</param>
		protected MockException(
		  System.Runtime.Serialization.SerializationInfo info,
		  System.Runtime.Serialization.StreamingContext context)
			: base(info, context)
		{
			this.reason = (MockExceptionReason)info.GetValue("reason", typeof(MockExceptionReason));
		}

		/// <summary>
		/// Supports the serialization infrastructure.
		/// </summary>
		/// <param name="info">Serialization information.</param>
		/// <param name="context">Streaming context.</param>
		[SecurityCritical]
		[SuppressMessage("Microsoft.Security", "CA2123:OverrideLinkDemandsShouldBeIdenticalToBase")]
		public override void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			base.GetObjectData(info, context);
			info.AddValue("reason", reason);
		}
#endif
	}
}
