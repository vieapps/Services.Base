#region Related components
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		static string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		static Channel<((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack)> LogsQueue { get; } = Channel.CreateBounded<((DateTime, string, string, string, string, string, string), List<string>, string)>(new BoundedChannelOptions(4096)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		static Task LogsWritter { get; } = Task.Run(Extensions.WriteLogsAsync);

		static SemaphoreSlim LogsLocker { get; } = new SemaphoreSlim(1, 1);

		static bool IsLogsWritting { get; set; } = false;

		static bool UseChannel { get; } = "Channel".IsEquals(UtilityService.GetAppSetting("Logs:Mode", "Channel"));

		static async Task WriteLogAsync(this ((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack) log)
		{
			try
			{
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
				}.SaveAsTextAsync(Path.Combine(Extensions.LogsPath, $"zlogs.services.{log.Info.Time:yyyyMMddHHmmssffffff}.{UtilityService.NewUUID}.json")).ConfigureAwait(false);
			}
			catch { }
		}

		static async Task WriteLogsAsync()
		{
			while (await Extensions.LogsQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.LogsQueue.Reader.TryRead(out var log))
					await log.WriteLogAsync().ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Writes the log messages into centerlized log storage
		/// </summary>
		/// <param name="logs"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="logger"></param>
		/// <returns></returns>
		public static async Task WriteLogsAsync(this ConcurrentQueue<((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack)> logs, ILogger logger = null, CancellationToken cancellationToken = default)
		{
			if (Extensions.UseChannel)
				try
				{
					while (logs.TryDequeue(out var log))
						await Extensions.LogsQueue.Writer.WriteAsync(log, cancellationToken).ConfigureAwait(false);
				}
				catch (TaskCanceledException) { }
				catch (OperationCanceledException) { }
				catch (ObjectDisposedException) { }
				catch (Exception ex)
				{
					logger?.LogError(ex, $"Error occurred while writting logs => {ex.Message}");
				}

			else if (!Extensions.IsLogsWritting)
			{
				Extensions.IsLogsWritting = true;
				await Extensions.LogsLocker.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					while (logs.TryDequeue(out var log))
						await log.WriteLogAsync().ConfigureAwait(false);
				}
				catch (TaskCanceledException) { }
				catch (OperationCanceledException) { }
				catch (ObjectDisposedException) { }
				catch (Exception ex)
				{
					logger?.LogError(ex, $"Error occurred while writting logs => {ex.Message}");
				}
				finally
				{
					try
					{
						Extensions.LogsLocker.Release();
					}
					catch { }
					Extensions.IsLogsWritting = false;
				}
			}
		}

		/// <summary>
		/// Shutdowns the logs
		/// </summary>
		/// <returns></returns>
		public static Task ShutdownLogsAsync()
		{
			Extensions.LogsQueue.Writer.TryComplete();
			return Extensions.LogsWritter;
		}
	}
}