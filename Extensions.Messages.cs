#region Related components
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections.Generic;
using System.Collections.Concurrent;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		static ISubject<UpdateMessage> UpdatingSubject { get; set; }

		static ISubject<UpdateMessage> GetUpdatingSubject()
			=> Extensions.UpdatingSubject ?? (Extensions.UpdatingSubject = Router.OutgoingChannel?.RealmProxy.Services.GetSubject<UpdateMessage>("messages.update"));

		/// <summary>
		/// Sends an updating message
		/// </summary>
		/// <param name="message"></param>
		/// <returns></returns>
		public static void Send(this UpdateMessage message)
		{
			try
			{
				Extensions.GetUpdatingSubject()?.OnNext(message);
			}
			catch { }
		}

		/// <summary>
		/// Sends an updating message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this UpdateMessage message, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			message?.Send();
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <returns></returns>
		public static void Send(this IEnumerable<UpdateMessage> messages, bool parallelExecutions = false, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
		{
			if (messages != null && messages.Any())
			{
				var subject = Extensions.GetUpdatingSubject();
				messages.ForEach(message =>
				{
					try
					{
						subject?.OnNext(message);
					}
					catch { }
				}, parallelExecutions, maxDegreeOfParallelism, cancellationToken);
			}
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this IEnumerable<UpdateMessage> messages, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			messages?.Send();
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="deviceID"></param>
		/// <param name="excludedDeviceID"></param>
		/// <returns></returns>
		public static void Send(this List<BaseMessage> messages, string deviceID, string excludedDeviceID, bool parallelExecutions = false, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
		{
			try
			{
				messages?.Select(message => new UpdateMessage
				{
					Type = message.Type,
					Data = message.Data,
					DeviceID = deviceID,
					ExcludedDeviceID = excludedDeviceID
				}).Send(parallelExecutions, maxDegreeOfParallelism, cancellationToken);
			}
			catch { }
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="deviceID"></param>
		/// <param name="excludedDeviceID"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this List<BaseMessage> messages, string deviceID, string excludedDeviceID, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			if (messages != null && messages.Count != 0)
				messages.Select(message => new UpdateMessage
				{
					Type = message.Type,
					Data = message.Data,
					DeviceID = deviceID,
					ExcludedDeviceID = excludedDeviceID
				}).Send();
		}

		static ConcurrentDictionary<string, ISubject<CommunicateMessage>> CommunicatingSubjects { get; } = new ConcurrentDictionary<string, ISubject<CommunicateMessage>>();

		static ISubject<CommunicateMessage> GetCommunicatingSubject(this BaseMessage message, string uri = null)
		{
			uri = uri ?? $"messages.services.{(message != null && message is CommunicateMessage ? (message as CommunicateMessage).ServiceName.Trim().ToLower() : "apigateway")}";
			if (!Extensions.CommunicatingSubjects.TryGetValue(uri, out var subject))
			{
				subject = Router.OutgoingChannel?.RealmProxy.Services.GetSubject<CommunicateMessage>(uri);
				if (subject != null)
					Extensions.CommunicatingSubjects.TryAdd(uri, subject);
			}
			return subject;
		}

		/// <summary>
		/// Sends a communicating message
		/// </summary>
		/// <param name="message"></param>
		/// <returns></returns>
		public static void Send(this CommunicateMessage message)
		{
			try
			{
				message?.GetCommunicatingSubject()?.OnNext(message);
			}
			catch { }
		}

		/// <summary>
		/// Sends a communicating message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this CommunicateMessage message, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			message?.Send();
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <returns></returns>
		public static void Send(this IEnumerable<CommunicateMessage> messages, bool parallelExecutions = false, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
		{
			if (messages != null && messages.Any())
			{
				var subject = messages.First().GetCommunicatingSubject();
				messages.ForEach(message =>
				{
					try
					{
						subject?.OnNext(message);
					}
					catch { }
				}, parallelExecutions, maxDegreeOfParallelism, cancellationToken);
			}
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this IEnumerable<CommunicateMessage> messages, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			messages?.Send();
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="serviceName"></param>
		/// <returns></returns>
		public static void Send(this List<BaseMessage> messages, string serviceName, bool parallelExecutions = false, int? maxDegreeOfParallelism = null, CancellationToken cancellationToken = default)
		{
			if (messages != null && messages.Count != 0)
			{
				var subject = messages.First().GetCommunicatingSubject($"messages.services.{serviceName.Trim().ToLower()}");
				messages.Select(message => new CommunicateMessage(serviceName, message)).ForEach(message =>
				{
					try
					{
						subject?.OnNext(message);
					}
					catch { }
				}, parallelExecutions, maxDegreeOfParallelism, cancellationToken);
			}
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="serviceName"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="defer"></param>
		/// <returns></returns>
		public static async Task SendAsync(this List<BaseMessage> messages, string serviceName, CancellationToken cancellationToken = default, int defer = 0)
		{
			if (defer > 0)
				await Task.Delay(defer, cancellationToken).ConfigureAwait(false);
			messages?.Send(serviceName);
		}
	}
}