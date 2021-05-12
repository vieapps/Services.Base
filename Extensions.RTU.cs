#region Related components
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WampSharp.V2.Core.Contracts;
using net.vieapps.Components.Security;
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
		/// <returns></returns>
		public static Task SendAsync(this UpdateMessage message, CancellationToken cancellationToken = default)
		{
			message?.Send();
			return Task.CompletedTask;
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <returns></returns>
		public static void Send(this IEnumerable<UpdateMessage> messages)
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
				});
			}
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this IEnumerable<UpdateMessage> messages, CancellationToken cancellationToken = default)
		{
			messages?.Send();
			return Task.CompletedTask;
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="deviceID"></param>
		/// <param name="excludedDeviceID"></param>
		/// <returns></returns>
		public static void Send(this List<BaseMessage> messages, string deviceID, string excludedDeviceID)
		{
			try
			{
				messages?.Select(message => new UpdateMessage
				{
					Type = message.Type,
					Data = message.Data,
					DeviceID = deviceID,
					ExcludedDeviceID = excludedDeviceID
				})?.Send();
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
		/// <returns></returns>
		public static Task SendAsync(this List<BaseMessage> messages, string deviceID, string excludedDeviceID, CancellationToken cancellationToken = default)
			=> messages != null && messages.Any()
				? messages.Select(message => new UpdateMessage
				{
					Type = message.Type,
					Data = message.Data,
					DeviceID = deviceID,
					ExcludedDeviceID = excludedDeviceID
				}).SendAsync(cancellationToken)
				: Task.CompletedTask;

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
		/// <returns></returns>
		public static Task SendAsync(this CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			message?.Send();
			return Task.CompletedTask;
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <returns></returns>
		public static void Send(this IEnumerable<CommunicateMessage> messages)
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
				});
			}
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this IEnumerable<CommunicateMessage> messages, CancellationToken cancellationToken = default)
		{
			messages?.Send();
			return Task.CompletedTask;
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="serviceName"></param>
		/// <returns></returns>
		public static void Send(this List<BaseMessage> messages, string serviceName)
		{
			if (messages != null && messages.Any())
			{
				var subject = messages.First().GetCommunicatingSubject($"messages.services.{serviceName.Trim().ToLower()}");
				messages.Select(message => new CommunicateMessage(serviceName, message)).ForEach(message =>
				{
					try
					{
						subject?.OnNext(message);
					}
					catch { }
				});
			}
		}

		/// <summary>
		/// Sends the collection of communicating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="serviceName"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this List<BaseMessage> messages, string serviceName, CancellationToken cancellationToken = default)
		{
			messages?.Send(serviceName);
			return Task.CompletedTask;
		}
	}
}