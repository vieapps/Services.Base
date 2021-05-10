#region Related components
using System;
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
			=> Extensions.GetUpdatingSubject()?.OnNext(message);

		/// <summary>
		/// Sends an updating message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this UpdateMessage message, CancellationToken cancellationToken = default)
		{
			try
			{
				message?.Send();
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
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
				messages.ForEach(message => subject?.OnNext(message));
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
			try
			{
				messages?.Send();
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Sends the collection of updating messages
		/// </summary>
		/// <param name="messages"></param>
		/// <param name="deviceID"></param>
		/// <param name="excludedDeviceID"></param>
		/// <returns></returns>
		public static void Send(this List<BaseMessage> messages, string deviceID, string excludedDeviceID)
			=> messages?.Select(message => new UpdateMessage
			{
				Type = message.Type,
				Data = message.Data,
				DeviceID = deviceID,
				ExcludedDeviceID = excludedDeviceID
			})?.Send();

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
			=> message?.GetCommunicatingSubject()?.OnNext(message);

		/// <summary>
		/// Sends a communicating message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			try
			{
				message?.Send();
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
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
				messages.ForEach(message => subject?.OnNext(message));
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
			try
			{
				messages?.Send();
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
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
				messages.Select(message => new CommunicateMessage(serviceName, message)).ForEach(message => subject?.OnNext(message));
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
			try
			{
				messages?.Send(serviceName);
				return Task.CompletedTask;
			}
			catch (Exception ex)
			{
				return Task.FromException(ex);
			}
		}

		/// <summary>
		/// Writes the log messages into centerlized log storage
		/// </summary>
		/// <param name="loggingService"></param>
		/// <param name="logs"></param>
		/// <param name="sessionBuilder"></param>
		/// <param name="logger"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteLogsAsync(this ILoggingService loggingService, ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, Func<Session> sessionBuilder = null, ILogger logger = null, CancellationToken cancellationToken = default)
		{
			Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string> log = null;
			try
			{
				while (logs.TryDequeue(out log))
					await loggingService.WriteLogsAsync(log.Item1.Item2, log.Item1.Item3, log.Item1.Item4, log.Item1.Item5, log.Item1.Item6, log.Item2, log.Item3, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				if (log != null)
					logs.Enqueue(log);

				if (ex is WampException && ex.Message.IsEquals("wamp.error.no_such_procedure"))
					try
					{
						var sessionID = UtilityService.NewUUID;
						var session = sessionBuilder?.Invoke() ?? new Session
						{
							SessionID = sessionID,
							User = new User
							{
								SessionID = sessionID,
								ID = UtilityService.GetAppSetting("Users:SystemAccountID", "VIEAppsNGX-MMXVII-System-Account")
							},
							IP = "127.0.0.1",
							DeviceID = $"{ServiceBase.ServiceComponent?.NodeID ?? UtilityService.NewUUID}@logger",
							AppName = "VIEApps NGX Logger",
							AppPlatform = $"{Extensions.GetRuntimeOS()} Daemon",
							AppAgent = $"{UtilityService.DesktopUserAgent} VIEApps NGX Logging Daemon/{typeof(ServiceBase).Assembly.GetVersion(false)}",
							AppOrigin = null,
							Verified = true
						};
						while (logs.TryDequeue(out log))
							await Router.CallServiceAsync(new RequestInfo
							{
								Session = session,
								ServiceName = "logs",
								ObjectName = "service",
								Verb = "POST",
								Body = new JObject
								{
									{ "Time", log.Item1.Item1 },
									{ "CorrelationID", log.Item1.Item2 },
									{ "DeveloperID", log.Item1.Item3 },
									{ "AppID", log.Item1.Item4 },
									{ "ServiceName", log.Item1.Item5 },
									{ "ObjectName", log.Item1.Item6 },
									{ "Logs", log.Item2?.Join("\r\n") ?? "" },
									{ "Stack", log.Item3 }
								}.ToString(Formatting.None),
								CorrelationID = log.Item1.Item2
							}, cancellationToken).ConfigureAwait(false);
					}
					catch (Exception e)
					{
						logger?.LogError($"Cannot write logs by the alternative logging service => {e.Message}", e);
						if (log != null)
							logs.Enqueue(log);
					}
			}
		}
	}
}