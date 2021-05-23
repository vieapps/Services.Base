#region Related components
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		static string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		static SemaphoreSlim Locker { get; } = new SemaphoreSlim(1, 1);

		/// <summary>
		/// Writes the log messages into centerlized log storage
		/// </summary>
		/// <param name="logs"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="logger"></param>
		/// <returns></returns>
		public static async Task WriteLogsAsync(this ConcurrentQueue<Tuple<Tuple<DateTime, string, string, string, string, string>, List<string>, string>> logs, CancellationToken cancellationToken = default, ILogger logger = null)
		{
			try
			{
				await Extensions.Locker.WaitAsync(cancellationToken).ConfigureAwait(false);
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
			catch (Exception ex)
			{
				logger?.LogError($"Cannot write logs into files => {ex.Message}", ex);
			}
			finally
			{
				Extensions.Locker.Release();
			}
		}
	}
}