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

		static bool Writting { get; set; } = false;

		/// <summary>
		/// Writes the log messages into centerlized log storage
		/// </summary>
		/// <param name="logs"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="logger"></param>
		/// <returns></returns>
		public static async Task WriteLogsAsync(this ConcurrentQueue<((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack)> logs, CancellationToken cancellationToken = default, ILogger logger = null)
		{
			if (!Extensions.Writting)
				try
				{
					Extensions.Writting = true;
					await Extensions.Locker.WaitAsync(cancellationToken).ConfigureAwait(false);
					while (logs.TryDequeue(out var log))
						try
						{
							var filePath = Path.Combine(Extensions.LogsPath, $"logs.services.{DateTime.Now:yyyyMMddHHmmss}.{UtilityService.NewUUID}.json");
							await new JObject
							{
								{ "Time", log.Info.Time },
								{ "CorrelationID", log.Info.CorrelationID },
								{ "DeveloperID", log.Info.DeveloperID },
								{ "AppID", log.Info.AppID },
								{ "NodeID", log.Info.NodeID },
								{ "ServiceName", log.Info.ServiceName },
								{ "ObjectName", log.Info.ObjectName },
								{ "Logs", log.Logs?.Join("\r\n") ?? "" },
								{ "Stack", log.Stack }
							}.ToString(Formatting.Indented).ToBytes().SaveAsTextAsync(filePath, cancellationToken).ConfigureAwait(false);
						}
						catch { }
				}
				catch (Exception ex)
				{
					logger?.LogError($"Cannot write logs into files => {ex.Message}", ex);
				}
				finally
				{
					Extensions.Writting = false;
					Extensions.Locker.Release();
				}
		}
	}
}