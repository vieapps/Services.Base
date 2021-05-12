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
		static string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		static bool IsDirectLoggingServiceAvailable { get; set; } = true;

		static bool IsRegularLoggingServiceAvailable { get; set; } = true;

		static async Task WriteLogsByDirectLoggingServiceAsync(ILoggingService loggingService, ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, CancellationToken cancellationToken)
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
					Extensions.IsDirectLoggingServiceAvailable = false;
				throw;
			}
		}

		static async Task WriteLogsByRegularLoggingServiceAsync(ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, Session session, CancellationToken cancellationToken)
		{
			Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string> log = null;
			try
			{
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
			catch (Exception)
			{
				if (log != null)
					logs.Enqueue(log);
				Extensions.IsRegularLoggingServiceAvailable = false;
				throw;
			}
		}

		static async Task WriteLogsIntoFilesAsync(ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, CancellationToken cancellationToken)
		{
			while (logs.TryDequeue(out var log))
				try
				{
					var filePath = Path.Combine(Extensions.LogsPath, $"logs.services.{DateTime.Now:yyyyMMddHHmmss}.{UtilityService.NewUUID}.json");
					await UtilityService.WriteTextFileAsync(filePath, new JObject
					{
						{ "Time", log.Item1.Item1 },
						{ "CorrelationID", log.Item1.Item2 },
						{ "DeveloperID", log.Item1.Item3 },
						{ "AppID", log.Item1.Item4 },
						{ "ServiceName", log.Item1.Item5 },
						{ "ObjectName", log.Item1.Item6 },
						{ "Logs", log.Item2?.Join("\r\n") ?? "" },
						{ "Stack", log.Item3 }
					}.ToString(Formatting.Indented), false, null, cancellationToken).ConfigureAwait(false);
				}
				catch { }
		}

		static Session GetSession(Session session, Func<Session> sessionBuilder)
		{
			var sessionID = UtilityService.NewUUID;
			return session ?? sessionBuilder?.Invoke() ?? new Session
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
				AppAgent = $"{UtilityService.DesktopUserAgent} VIEApps NGX Daemon/{typeof(ServiceBase).Assembly.GetVersion(false)}",
				AppOrigin = null,
				Verified = true
			};
		}

		/// <summary>
		/// Writes the log messages into centerlized log storage
		/// </summary>
		/// <param name="loggingService"></param>
		/// <param name="logs"></param>
		/// <param name="sessionBuilder"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task WriteLogsAsync(this ILoggingService loggingService, ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, Func<Session> sessionBuilder = null, CancellationToken cancellationToken = default)
		{
			Session session = null;
			if (Extensions.IsDirectLoggingServiceAvailable)
				try
				{
					await Extensions.WriteLogsByDirectLoggingServiceAsync(loggingService, logs, cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					if (Extensions.IsRegularLoggingServiceAvailable)
						try
						{
							await Extensions.WriteLogsByRegularLoggingServiceAsync(logs, Extensions.GetSession(session, sessionBuilder), cancellationToken).ConfigureAwait(false);
						}
						catch
						{
							await Extensions.WriteLogsIntoFilesAsync(logs, cancellationToken).ConfigureAwait(false);
						}
					else
						await Extensions.WriteLogsIntoFilesAsync(logs, cancellationToken).ConfigureAwait(false);
				}

			else if (Extensions.IsRegularLoggingServiceAvailable)
				try
				{
					await Extensions.WriteLogsByRegularLoggingServiceAsync(logs, Extensions.GetSession(session, sessionBuilder), cancellationToken).ConfigureAwait(false);
				}
				catch
				{
					await Extensions.WriteLogsIntoFilesAsync(logs, cancellationToken).ConfigureAwait(false);
				}

			else
				await Extensions.WriteLogsIntoFilesAsync(logs, cancellationToken).ConfigureAwait(false);
		}
	}
}