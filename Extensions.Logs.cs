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
		static string LogsPath { get; } = UtilityService.GetAppSetting("Path:Logs", "logs");

		static int LogsQueueSize { get; } = Int32.TryParse(UtilityService.GetAppSetting("Logs:Size", "10240"), out var queueSize) && queueSize > 0 ? queueSize : 10240;

		#region Service Logs
		static Channel<LogInfo> ServiceLogs { get; } = Channel.CreateBounded<LogInfo>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Writes the service log into storages
		/// </summary>
		/// <param name="logInfo"></param>
		/// <returns></returns>
		public static bool WriteServiceLog(this LogInfo logInfo)
			=> Extensions.ServiceLogs.Writer.TryWrite(logInfo);

		/// <summary>
		/// Writes the service log into storages
		/// </summary>
		/// <param name="log"></param>
		/// <returns></returns>
		public static bool WriteServiceLog(this ((DateTime Time, string CorrelationID, string DeveloperID, string AppID, string NodeID, string ServiceName, string ObjectName) Info, List<string> Logs, string Stack) log)
			=> new LogInfo
			{
				ID = UtilityService.NewUUID,
				Time = log.Info.Time,
				CorrelationID = log.Info.CorrelationID,
				DeveloperID = log.Info.DeveloperID,
				AppID = log.Info.AppID,
				NodeID = log.Info.NodeID,
				ServiceName = log.Info.ServiceName?.ToLower(),
				ObjectName = log.Info.ObjectName?.ToLower(),
				Logs = (log.Logs ?? new List<string>()).Join("\r\n"),
				Stack = log.Stack
			}.WriteServiceLog();

		/// <summary>
		/// Writes the service log into storages
		/// </summary>
		/// <param name="logger"></param>
		/// <param name="nodeID"></param>
		/// <param name="correlationID"></param>
		/// <param name="developerID"></param>
		/// <param name="appID"></param>
		/// <param name="logs"></param>
		/// <param name="exception"></param>
		/// <param name="serviceName"></param>
		/// <param name="objectName"></param>
		/// <param name="additional"></param>
		/// <param name="mode"></param>
		/// <returns></returns>
		public static bool WriteServiceLog(ILogger logger, string nodeID, string correlationID, string developerID, string appID, List<string> logs, Exception exception, string serviceName, string objectName, string additional, LogLevel mode)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			logs = logs ?? new List<string>();
			var stack = exception?.GetStack(false);

			if (exception == null)
				logs.ForEach(message => logger?.Log(mode, $"{message} [{correlationID}]"));
			else
				logs.ForEach(message => logger?.Log(LogLevel.Error, $"{message} [{correlationID}]"));
			
			if (exception != null && exception is WampSharp.V2.Core.Contracts.WampException wampException)
			{
				var wampDetails = wampException.GetDetails();
				if (wampDetails.Code > 0)
				{
					logger?.Log(LogLevel.Error, message: $"{wampDetails.Type}: {wampDetails.Message}\r\n{wampDetails.Stack} [{correlationID}]", exception: exception);
					stack = $"{wampDetails.Type}: {wampDetails.Message}\r\n{wampDetails.Stack}";
				}
				else
					logger?.Log(LogLevel.Error, $"{exception.Message} [{correlationID}]", exception);
				logs.Add($"> Message: {wampDetails.Message}");
				logs.Add($"> Type: {wampDetails.Type}");
			}
			else if (exception != null)
			{
				logger?.Log(LogLevel.Error, $"{exception.Message} [{correlationID}]", exception);
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

			if (!string.IsNullOrWhiteSpace(additional))
			{
				logs.Add(additional);
				logger?.Log(exception != null ? LogLevel.Error : mode, $"{additional} [{correlationID}]");
			}

			return ((DateTime.Now, correlationID, developerID, appID, nodeID ?? ServiceBase.ServiceComponent?.NodeID ?? Extensions.GetNodeID(), serviceName ?? "APIGateway", objectName ?? ""), logs, stack).WriteServiceLog();
		}

		/// <summary>
		/// Writes the service log into storages
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
			=> Extensions.WriteServiceLog(logger, null, correlationID, developerID, appID, logs, exception, serviceName, objectName, null, mode);

		/// <summary>
		/// Writes the service log into storages
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
		/// Writes the service log into storages
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
				while (Extensions.ServiceLogs.Reader.TryRead(out var logInfo))
					try
					{
						await logInfo.SaveToFileAsync(Path.Combine(Extensions.LogsPath, $"zlogs.services.{logInfo.Time:yyyyMMddHHmmssffffff}.{logInfo.ID}.json")).ConfigureAwait(false);
					}
					catch { }
			}
		}
		#endregion

		#region Trace Logs
		static Channel<TraceInfo> TraceLogs { get; } = Channel.CreateBounded<TraceInfo>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Starts new trace tracking
		/// </summary>
		/// <param name="parentID"></param>
		/// <param name="correlationID"></param>
		/// <param name="nodeID"></param>
		/// <param name="serviceName"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static TraceInfo StartTrace(string parentID, string correlationID, string nodeID, string serviceName, string operation, string kind = "internal", Dictionary<string, object> attributes = null)
			=> new TraceInfo
			{
				ID = UtilityService.NewUUID,
				ParentID = parentID,
				CorrelationID = correlationID,
				NodeID = nodeID,
				ServiceName = serviceName?.ToLower(),
				Operation = operation,
				StartTime = DateTime.Now,
				Kind = kind,
				Status = "unset",
				Attributes = attributes ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
			};

		/// <summary>
		/// Completes trace tracking and write into storages
		/// </summary>
		/// <param name="traceInfo"></param>
		/// <param name="status"></param>
		/// <param name="statusDescription"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static TraceInfo Complete(this TraceInfo traceInfo, string status, string statusDescription = null, Dictionary<string, object> attributes = null)
		{
			traceInfo.EndTime = DateTime.Now;
			traceInfo.DurationMs = (traceInfo.EndTime.Value - traceInfo.StartTime).TotalMilliseconds;
			traceInfo.Status = status;
			if (!string.IsNullOrWhiteSpace(statusDescription))
				traceInfo.StatusDescription = statusDescription;
			attributes?.ForEach(kvp => traceInfo.Attributes[kvp.Key] = kvp.Value);
			Extensions.TraceLogs.Writer.TryWrite(traceInfo);
			return traceInfo;
		}

		/// <summary>
		/// Completes trace tracking and write into storages
		/// </summary>
		/// <param name="traceInfo"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static TraceInfo Complete(this TraceInfo traceInfo, Dictionary<string, object> attributes = null)
			=> traceInfo.Complete("ok", null, attributes);

		/// <summary>
		/// Sets the identifier of parent trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="spanID"></param>
		/// <returns></returns>
		public static RequestInfo SetParentTraceID(this RequestInfo requestInfo, string spanID)
		{
			requestInfo.Extra["x-parent-trace-id"] = spanID;
			return requestInfo;
		}

		/// <summary>
		/// Gets the identifier of parent trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static string GetParentTraceID(this RequestInfo requestInfo)
			=> requestInfo.Extra.TryGetValue("x-parent-trace-id", out var spanID) ? spanID : null;

		/// <summary>
		/// Gets the operation of trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static string GetTraceAction(this RequestInfo requestInfo)
		{
			switch ((requestInfo.Verb ?? "").ToUpper())
			{
				case "GET":
					var objectIdentity = requestInfo.GetObjectIdentity();
					return objectIdentity != null && (objectIdentity.IsValidUUID() || objectIdentity.IsEquals("refresh") || objectIdentity.IsEquals("cache")) ? "READ" : "SEARCH";

				case "POST":
					return "CREATE";

				case "PUT":
					return "UPDATE";

				default:
					return (requestInfo.Verb ?? "Do").ToUpper();
			}
		}

		/// <summary>
		/// Starts new trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <param name="attributes"></param>
		/// <returns></returns>
		public static TraceInfo StartTrace(this RequestInfo requestInfo, string operation, string kind, Dictionary<string, object> attributes, bool autoSetParentTraceSpanID)
		{
			var traceInfo = Extensions.StartTrace(requestInfo.GetParentTraceID(), requestInfo.CorrelationID, ServiceBase.ServiceComponent?.NodeID ?? Extensions.GetNodeID(), requestInfo.ServiceName, operation, kind, attributes);
			if (autoSetParentTraceSpanID)
				requestInfo.SetParentTraceID(traceInfo.ID);
			return traceInfo;
		}

		/// <summary>
		/// Starts new trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <returns></returns>
		public static TraceInfo StartTrace(this RequestInfo requestInfo, string operation, string kind = "internal", bool autoSetParentTraceSpanID = false)
			=> requestInfo.StartTrace(operation, kind, null, autoSetParentTraceSpanID);

		/// <summary>
		/// Starts new trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="traceInfo"></param>
		/// <param name="operation"></param>
		/// <param name="kind"></param>
		/// <param name="autoSetParentTraceSpanID"></param>
		/// <returns></returns>
		public static TraceInfo StartTrace(this RequestInfo requestInfo, TraceInfo traceInfo, string operation, string kind = "internal", bool autoSetParentTraceSpanID = false)
		{
			traceInfo.Complete();
			return requestInfo.StartTrace(operation, kind, autoSetParentTraceSpanID);
		}

		/// <summary>
		/// Starts new trace tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="autoSetParentTraceSpanID"></param>
		/// <returns></returns>
		public static TraceInfo StartTrace(this RequestInfo requestInfo, bool autoSetParentTraceSpanID = true)
			=> requestInfo.StartTrace($"{requestInfo.GetTraceAction()} {requestInfo.ObjectName?.ToArray(".").Last().GetCapitalizedFirstLetter()}", "downstream", null, autoSetParentTraceSpanID);

		static Task TraceLogsWritter { get; } = Task.Run(Extensions.WriteTraceLogsAsync);

		static async Task WriteTraceLogsAsync()
		{
			while (await Extensions.TraceLogs.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.TraceLogs.Reader.TryRead(out var traceInfo))
					try
					{
						await traceInfo.SaveToFileAsync(Path.Combine(Extensions.LogsPath, $"zlogs.traces.{DateTime.Now:yyyyMMddHHmmssffffff}.{traceInfo.ID}.json")).ConfigureAwait(false);
					}
					catch { }
			}
		}
		#endregion

		#region Usage Logs
		static Channel<UsageInfo> UsageLogs { get; } = Channel.CreateBounded<UsageInfo>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Starts new resource usage tracking
		/// </summary>
		/// <param name="developerID"></param>
		/// <param name="appID"></param>
		/// <param name="userID"></param>
		/// <param name="nodeID"></param>
		/// <param name="correlationID"></param>
		/// <param name="serviceName"></param>
		/// <param name="objectName"></param>
		/// <param name="operation"></param>
		/// <param name="type"></param>
		/// <param name="provider"></param>
		/// <param name="model"></param>
		/// <param name="systemID"></param>
		/// <param name="traceID"></param>
		/// <returns></returns>
		public static UsageInfo StartUsage(string developerID, string appID, string userID, string nodeID, string correlationID, string serviceName, string objectName, string operation, string type, string provider = null, string model = null, string systemID = null, string traceID = null)
			=> new UsageInfo
			{
				ID = UtilityService.NewUUID,
				CorrelationID = correlationID,
				TraceID = traceID,
				DeveloperID = developerID,
				AppID = appID,
				UserID = userID,
				NodeID = nodeID,
				ServiceName = serviceName?.ToLower(),
				ObjectName = objectName?.ToLower(),
				SystemID = systemID,
				Operation = operation,
				StartTime = DateTime.Now,
				Type = type,
				Provider = provider,
				Model = model,
				Status = "unset",
				Details = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
			};

		/// <summary>
		/// Completes resource usage tracking and writes into storages
		/// </summary>
		/// <param name="usageInfo"></param>
		/// <param name="status"></param>
		/// <param name="details"></param>
		/// <returns></returns>
		public static UsageInfo Complete(this UsageInfo usageInfo, string status, Dictionary<string, object> details = null)
		{
			usageInfo.EndTime = DateTime.Now;
			usageInfo.DurationMs = (usageInfo.EndTime.Value - usageInfo.StartTime).TotalMilliseconds;
			usageInfo.Status = status;
			details?.ForEach(kvp => usageInfo.Details[kvp.Key] = kvp.Value);
			Extensions.UsageLogs.Writer.TryWrite(usageInfo);
			return usageInfo;
		}

		/// <summary>
		/// Completes resource usage tracking and writes into storages
		/// </summary>
		/// <param name="usageInfo"></param>
		/// <param name="details"></param>
		/// <returns></returns>
		public static UsageInfo Complete(this UsageInfo usageInfo, Dictionary<string, object> details = null)
			=> usageInfo.Complete("ok", details);

		/// <summary>
		/// Starts new resource usage tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="operation"></param>
		/// <param name="type"></param>
		/// <param name="provider"></param>
		/// <param name="model"></param>
		/// <param name="traceID"></param>
		/// <returns></returns>
		public static UsageInfo StartUsage(this RequestInfo requestInfo, string operation, string type, string provider = null, string model = null, string traceID = null)
			=> Extensions.StartUsage(requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, requestInfo.Session?.User?.ID, ServiceBase.ServiceComponent?.NodeID ?? Extensions.GetNodeID(), requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, operation, type, provider, model, requestInfo.GetParameter("x-system-id"), traceID);

		/// <summary>
		/// Starts new resource usage tracking
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="usageInfo"></param>
		/// <param name="operation"></param>
		/// <param name="type"></param>
		/// <param name="provider"></param>
		/// <param name="model"></param>
		/// <param name="traceID"></param>
		/// <returns></returns>
		public static UsageInfo StartUsage(this RequestInfo requestInfo, UsageInfo usageInfo, string operation, string type, string provider = null, string model = null, string traceID = null)
		{
			usageInfo.Complete();
			return requestInfo.StartUsage(operation, type, provider, model, traceID);
		}

		static Task UsageLogsWritter { get; } = Task.Run(Extensions.WriteUsageLogsAsync);

		static async Task WriteUsageLogsAsync()
		{
			while (await Extensions.UsageLogs.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.UsageLogs.Reader.TryRead(out var usageInfo))
					try
					{
						await usageInfo.SaveToFileAsync(Path.Combine(Extensions.LogsPath, $"zlogs.usages.{DateTime.Now:yyyyMMddHHmmssffffff}.{usageInfo.ID}.json")).ConfigureAwait(false);
					}
					catch { }
			}
		}
		#endregion

		#region Audit Logs
		static Channel<AuditInfo> AuditLogs { get; } = Channel.CreateBounded<AuditInfo>(new BoundedChannelOptions(Extensions.LogsQueueSize)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.Wait
		});

		/// <summary>
		/// Starts tracking a business-relevant auditable action.
		/// </summary>
		public static AuditInfo StartAudit(string correlationID, string traceID, string developerID, string appID, string actorType, string actorID, string nodeID, string serviceName, string objectName, string objectID, string systemID, string operation)
			=> new AuditInfo
			{
				ID = UtilityService.NewUUID,
				CorrelationID = correlationID,
				TraceID = traceID,
				DeveloperID = developerID,
				AppID = appID,
				ActorType = actorType,
				ActorID = actorID,
				NodeID = nodeID,
				ServiceName = serviceName?.ToLower(),
				ObjectName = objectName?.ToLower(),
				ObjectID = objectID,
				SystemID = systemID,
				Operation = operation,
				Status = "unset",
				Changes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
				Details = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
			};

		/// <summary>
		/// Starts an audit record from the request context.
		/// </summary>
		public static AuditInfo StartAudit(this RequestInfo requestInfo, string operation, string objectID = null, string systemID = null, string traceID = null, string actorType = "user", string actorID = null)
			=> Extensions.StartAudit(requestInfo.CorrelationID, traceID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, actorType, actorID ?? ("user".IsEquals(actorType) ? requestInfo.Session?.User?.ID : null), ServiceBase.ServiceComponent?.NodeID ?? Extensions.GetNodeID(), requestInfo.ServiceName, requestInfo.ObjectName, objectID, systemID ?? requestInfo.GetParameter("x-system-id"), operation);

		/// <summary>
		/// Completes the audit information without automatically persisting it.
		/// </summary>
		public static AuditInfo Complete(this AuditInfo auditInfo, string status, Dictionary<string, object> changes = null, Dictionary<string, object> details = null)
		{
			if (auditInfo.Time != null)
				throw new InvalidOperationException("The audit has already been completed.");

			if (status != "ok" && status != "error" && status != "denied" && status != "canceled")
				throw new ArgumentException("Invalid audit status.", nameof(status));

			auditInfo.Time = DateTime.Now;
			auditInfo.Status = status;

			if (changes != null)
				auditInfo.Changes = new Dictionary<string, object>(changes, StringComparer.OrdinalIgnoreCase);

			if (details != null)
				auditInfo.Details = new Dictionary<string, object>(details, StringComparer.OrdinalIgnoreCase);

			return auditInfo;
		}

		/// <summary>
		/// Completes and enqueues an auditable action.
		/// </summary>
		public static bool CompleteAndWrite(this AuditInfo auditInfo, string status = "ok", Dictionary<string, object> changes = null, Dictionary<string, object> details = null)
			=> auditInfo.Complete(status, changes, details).WriteAuditLog();

		/// <summary>
		/// Completes and enqueues an auditable action.
		/// </summary>
		public static bool CompleteAndWrite(this AuditInfo auditInfo, Dictionary<string, object> changes, Dictionary<string, object> details)
			=> auditInfo.CompleteAndWrite("ok", changes, details);

		/// <summary>
		/// Completes and enqueues an auditable action.
		/// </summary>
		public static bool CompleteAndWrite(this AuditInfo auditInfo, Dictionary<string, object> details)
			=> auditInfo.CompleteAndWrite(null, details);

		/// <summary>
		/// Enqueues a completed audit record for file persistence.
		/// </summary>
		public static bool WriteAuditLog(this AuditInfo auditInfo)
			=> auditInfo != null && auditInfo.Time != null && !string.IsNullOrWhiteSpace(auditInfo.ID) && !string.IsNullOrWhiteSpace(auditInfo.ServiceName) && !string.IsNullOrWhiteSpace(auditInfo.ObjectName) && !string.IsNullOrWhiteSpace(auditInfo.Operation) && !string.IsNullOrWhiteSpace(auditInfo.ActorType) && !string.IsNullOrWhiteSpace(auditInfo.ActorID) && !string.IsNullOrWhiteSpace(auditInfo.Status) && auditInfo.Status != "unset" && Extensions.AuditLogs.Writer.TryWrite(auditInfo);

		static Task AuditLogsWritter { get; } = Task.Run(Extensions.WriteAuditLogsAsync);

		static async Task WriteAuditLogsAsync()
		{
			while (await Extensions.AuditLogs.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (Extensions.AuditLogs.Reader.TryRead(out var auditInfo))
				{
					try
					{
						await auditInfo.SaveToFileAsync(Path.Combine(Extensions.LogsPath, $"zlogs.audits.{auditInfo.Time:yyyyMMddHHmmssffffff}.{auditInfo.ID}.json")).ConfigureAwait(false);
					}
					catch { }
				}
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
			Extensions.UsageLogs.Writer.TryComplete();
			Extensions.AuditLogs.Writer.TryComplete();
			return Task.WhenAll(Extensions.ServiceLogsWritter, Extensions.TraceLogsWritter, Extensions.UsageLogsWritter, Extensions.AuditLogsWritter);
		}
	}
}