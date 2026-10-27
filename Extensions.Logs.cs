#region Related components
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		internal static string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		internal static int LogsQueueSize { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:QueueSize", "10240"), out var queueSize) && queueSize > 0 ? queueSize : 10240;

		#region Service Logs
		static Channel<((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack)> ServiceLogs { get; } = Channel.CreateBounded<((DateTime, string, string, string, string, string, string), List<string>, string)>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Writes the service log into centerlized storage
		/// </summary>
		/// <param name="log"></param>
		/// <returns></returns>
		public static bool WriteServiceLog(this ((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack) log)
			=> Extensions.ServiceLogs.Writer.TryWrite(log);

		/// <summary>
		/// Writes the service log into centerlized storage
		/// </summary>
		/// <param name="correlationID"></param>
		/// <param name="developerID"></param>
		/// <param name="appID"></param>
		/// <param name="logger"></param>
		/// <param name="logs"></param>
		/// <param name="exception"></param>
		/// <param name="serviceName"></param>
		/// <param name="objectName"></param>
		/// <param name="mode"></param>
		/// <returns></returns>
		public static bool WriteServiceLog(string correlationID, string developerID, string appID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			if (exception == null)
				logs?.ForEach(message => logger?.Log(mode, $"{message} [{correlationID}]"));
			else
			{
				logs?.ForEach(message => logger?.Log(LogLevel.Error, $"{message} [{correlationID}]"));
				logger?.Log(LogLevel.Error, $"{exception.Message} [{correlationID}]", exception);
			}
			logs = logs ?? new List<string>();
			if (exception != null && exception is WampSharp.V2.Core.Contracts.WampException wampException)
			{
				var details = wampException.GetDetails();
				logs.Add($"> Message: {details.Message}");
				logs.Add($"> Type: {details.Type}");
			}
			else if (exception != null)
			{
				if (exception is AggregateException agg)
					foreach (var inner in agg.Flatten().InnerExceptions)
					{
						logs.Add($"> Message: {inner.Message}");
						logs.Add($"> Type: {inner.GetTypeName(true)}");
					}
				else
				{
					logs.Add($"> Message: {exception.Message}");
					logs.Add($"> Type: {exception.GetTypeName(true)}");
				}
			}
			return ((DateTime.Now, correlationID, developerID, appID, ServiceBase.ServiceComponent.NodeID ?? Extensions.GetNodeID(), serviceName ?? "APIGateway", objectName ?? ""), logs, exception?.GetStack(false)).WriteServiceLog();
		}

		/// <summary>
		/// Writes the service log into centerlized storage
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="logger"></param>
		/// <param name="logs"></param>
		/// <param name="exception"></param>
		/// <param name="mode"></param>
		/// <returns></returns>
		public static bool WriteServiceLogs(this RequestInfo requestInfo, ILogger logger, List<string> logs, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> Extensions.WriteServiceLog(requestInfo.CorrelationID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, logger, logs, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode);

		/// <summary>
		/// Writes the service log into centerlized storage
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="logger"></param>
		/// <param name="log"></param>
		/// <param name="exception"></param>
		/// <param name="mode"></param>
		/// <returns></returns>
		public static bool WriteServiceLogs(this RequestInfo requestInfo, ILogger logger, string log, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> requestInfo.WriteServiceLogs(logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, mode);

		static Task ServiceLogsWritter { get; } = Task.Run(Extensions.WriteServiceLogsAsync);

		static async Task WriteServiceLogsAsync()
		{
			while (await Extensions.ServiceLogs.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.ServiceLogs.Reader.TryRead(out var log))
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
		}
		#endregion

		#region Trace Logs
		static Channel<(string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes)> TraceLogs { get; } = Channel.CreateBounded<(string, string, string, string, string, string, DateTime, DateTime?, double?, string, string, string, Dictionary<string, object>)>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Starts new trace
		/// </summary>
		/// <param name="parentID"></param>
		/// <param name="correlationID"></param>
		/// <param name="nodeID"></param>
		/// <param name="serviceName"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) StartTrace(string parentID, string correlationID, string nodeID, string serviceName, string operation, string kind = "internal", Dictionary<string, object> attributes = null)
			=> (UtilityService.NewUUID, parentID, correlationID, nodeID, serviceName, operation, DateTime.Now, null, null, kind, "unset", null, attributes ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

		/// <summary>
		/// Completes a trace and write into centerlized storage
		/// </summary>
		/// <param name="trace"></param>
		/// <param name="status"></param>
		/// <param name="statusDescription"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) CompleteTrace(this (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) trace, string status, string statusDescription = null, Dictionary<string, object> attributes = null)
		{
			trace.EndTime = DateTime.Now;
			trace.DurationMs = (trace.EndTime.Value - trace.StartTime).TotalMilliseconds;
			trace.Status = status;
			if (!string.IsNullOrWhiteSpace(statusDescription))
				trace.StatusDescription = statusDescription;
			attributes?.ForEach(kvp => trace.Attributes[kvp.Key] = kvp.Value);
			Extensions.TraceLogs.Writer.TryWrite(trace);
			return trace;
		}

		/// <summary>
		/// Completes a trace and write into centerlized storage
		/// </summary>
		/// <param name="trace"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) CompleteTrace(this (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) trace, Dictionary<string, object> attributes = null)
			=> trace.CompleteTrace("ok", null, attributes);

		/// <summary>
		/// Sets the identifier of parent tracing span
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="spanID"></param>
		/// <returns></returns>
		public static RequestInfo SetParentSpanID(this RequestInfo requestInfo, string spanID)
		{
			requestInfo.Extra["x-parent-span-id"] = spanID;
			return requestInfo;
		}

		/// <summary>
		/// Gets the identifier of parent tracing span
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static string GetParentSpanID(this RequestInfo requestInfo)
			=> requestInfo.Extra.TryGetValue("x-parent-span-id", out var spanID) ? spanID : null;

		/// <summary>
		/// Starts new trace (and set parent span ID automatically)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) StartTrace(this RequestInfo requestInfo, string operation, string kind, Dictionary<string, object> attributes = null, bool autoSetParentSpanID = true)
		{
			var trace = Extensions.StartTrace(requestInfo.GetParentSpanID(), requestInfo.CorrelationID, ServiceBase.ServiceComponent.NodeID, requestInfo.ServiceName, operation, kind, attributes);
			if (autoSetParentSpanID)
				requestInfo.SetParentSpanID(trace.SpanID);
			return trace;
		}

		/// <summary>
		/// Starts new trace (and set parent span ID automatically)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="kind"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) StartTrace(this RequestInfo requestInfo, string kind, bool autoSetParentSpanID)
			=> requestInfo.StartTrace($"{requestInfo.Verb} {requestInfo.ObjectName}", kind, null, autoSetParentSpanID);

		/// <summary>
		/// Starts new trace (and set parent span ID automatically)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) StartTrace(this RequestInfo requestInfo, string operation, string kind = "internal", bool autoSetParentSpanID = false)
			=> requestInfo.StartTrace(operation, kind, null, autoSetParentSpanID);

		/// <summary>
		/// Starts new trace (and set parent span ID automatically)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <returns></returns>
		public static (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) StartTrace(this RequestInfo requestInfo, (string SpanID, string ParentID, string CorrelationID, string NodeID, string ServiceName, string Operation, DateTime StartTime, DateTime? EndTime, double? DurationMs, string Kind, string Status, string StatusDescription, Dictionary<string, object> Attributes) trace, string operation, string kind = "internal", bool autoSetParentSpanID = false)
		{
			trace.CompleteTrace();
			return requestInfo.StartTrace(operation, kind, autoSetParentSpanID);
		}

		static Task TraceLogsWritter { get; } = Task.Run(Extensions.WriteTraceLogsAsync);

		static async Task WriteTraceLogsAsync()
		{
			while (await Extensions.TraceLogs.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.TraceLogs.Reader.TryRead(out var log))
					try
					{
						await new JObject
						{
							{ "ID", log.SpanID },
							{ "ParentID", log.ParentID },
							{ "CorrelationID", log.CorrelationID },
							{ "NodeID", log.NodeID },
							{ "ServiceName", log.ServiceName },
							{ "Operation", log.Operation },
							{ "StartTime", log.StartTime },
							{ "EndTime", log.EndTime },
							{ "DurationMs", log.DurationMs },
							{ "Kind", log.Kind },
							{ "Status", log.Status },
							{ "StatusDescription", log.StatusDescription },
							{ "Attributes", log.Attributes?.ToJson() }
						}.SaveAsTextAsync(Path.Combine(Extensions.LogsPath, $"zlogs.traces.{DateTime.Now:yyyyMMddHHmmssffffff}.{log.SpanID}.json")).ConfigureAwait(false);
					}
					catch { }
			}
		}
		#endregion

		/// <summary>
		/// Shutdowns the logs
		/// </summary>
		/// <returns></returns>
		public static Task ShutdownLogsAsync()
		{
			Extensions.ServiceLogs.Writer.TryComplete();
			Extensions.TraceLogs.Writer.TryComplete();
			return Task.WhenAll(Extensions.ServiceLogsWritter, Extensions.TraceLogsWritter);
		}
	}
}