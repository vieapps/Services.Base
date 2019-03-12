#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;

using WampSharp.V2;
using WampSharp.V2.Rpc;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Client;
using WampSharp.V2.Realm;
using WampSharp.Core.Listener;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Base of all business services
	/// </summary>
	public abstract class ServiceBase : IService, IUniqueService, IServiceComponent, IDisposable
	{
		/// <summary>
		/// Gets the name for working with related URIs
		/// </summary>
		public abstract string ServiceName { get; }

		/// <summary>
		/// Process the request
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public abstract Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Process the inter-communicate message
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		protected virtual Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken)) => Task.CompletedTask;

		#region Attributes & Properties
		SystemEx.IAsyncDisposable ServiceInstance { get; set; } = null;

		SystemEx.IAsyncDisposable ServiceUniqueInstance { get; set; } = null;

		ConcurrentDictionary<string, string> ServiceUniqueNames { get; } = new ConcurrentDictionary<string, string>();

		ConcurrentDictionary<string, IUniqueService> UniqueServices { get; } = new ConcurrentDictionary<string, IUniqueService>(StringComparer.OrdinalIgnoreCase);

		IDisposable ServiceCommunicator { get; set; } = null;

		IDisposable GatewayCommunicator { get; set; } = null;

		IRTUService RTUService { get; set; } = null;

		ILoggingService LoggingService { get; set; } = null;

		IMessagingService MessagingService { get; set; } = null;

		internal protected CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();

		internal protected List<IDisposable> Timers { get; private set; } = new List<IDisposable>();

		internal protected ServiceState State { get; private set; } = ServiceState.Initializing;

		/// <summary>
		/// Gets the full URI of this service
		/// </summary>
		public string ServiceURI => $"net.vieapps.services.{(this.ServiceName ?? "unknown").Trim().ToLower()}";

		/// <summary>
		/// Gets the unique name for working with related URIs
		/// </summary>
		public string ServiceUniqueName { get; private set; }

		/// <summary>
		/// Gets the full unique URI of this service
		/// </summary>
		public string ServiceUniqueURI => $"net.vieapps.services.{(this.ServiceUniqueName ?? "unknown").Trim().ToLower()}";

		/// <summary>
		/// Gets or sets the single instance of current playing service component
		/// </summary>
		public static ServiceBase ServiceComponent { get; set; }
		#endregion

		#region Register the service
		/// <summary>
		/// Registers the service
		/// </summary>
		/// <param name="onSuccessAsync"></param>
		/// <param name="onErrorAsync"></param>
		/// <returns></returns>
		protected async Task RegisterServiceAsync(Func<ServiceBase, Task> onSuccessAsync = null, Func<Exception, Task> onErrorAsync = null)
		{
			var name = this.ServiceName.Trim().ToLower();

			async Task registerAsync()
			{
				try
				{
					this.ServiceInstance = await WAMPConnections.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(name)).ConfigureAwait(false);
					this.ServiceUniqueInstance = await WAMPConnections.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(this.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
				}
				catch
				{
					await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
					try
					{
						this.ServiceInstance = await WAMPConnections.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(name)).ConfigureAwait(false);
						this.ServiceUniqueInstance = await WAMPConnections.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(this.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
					}
					catch (Exception)
					{
						throw;
					}
				}
				this.Logger.LogInformation($"The service is{(this.State == ServiceState.Disconnected ? " re-" : " ")}registered successful");

				this.ServiceCommunicator?.Dispose();
				this.ServiceCommunicator = WAMPConnections.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>($"net.vieapps.rtu.communicate.messages.{name}")
					.Subscribe(
						async message => await this.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false),
						exception => this.Logger.LogError($"Error occurred while fetching inter-communicate message: {exception.Message}", this.State == ServiceState.Connected ? exception : null),
						() => this.Logger.LogInformation("Inter-communicate message channel is completed")
					);

				this.GatewayCommunicator?.Dispose();
				this.GatewayCommunicator = WAMPConnections.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>($"net.vieapps.rtu.communicate.messages.apigateway")
					.Subscribe(
						message =>
						{
							if (message.Type.IsEquals($"Service#UniqueInfo#{name}"))
							{
								var osPlatform = message.Data.Get<string>("OSPlatform") ?? "Windows";
								if (!this.ServiceUniqueNames.TryGetValue(osPlatform, out string uniqueName))
								{
									uniqueName = message.Data.Get<string>("Name") ?? $"{name}.{UtilityService.NewUUID}";
									this.ServiceUniqueNames.TryAdd(osPlatform, uniqueName);
									this.Logger.LogInformation($"The unique name of related URIs is updated: {osPlatform} => net.vieapps.services.{uniqueName}");
								}
							}
						},
						exception => this.Logger.LogError($"Error occurred while fetching inter-communicate message of API Gateway: {exception.Message}", this.State == ServiceState.Connected ? exception : null),
						() => this.Logger.LogInformation("Inter-communicate message channel of API Gateway is completed")
					);

				this.Logger.LogInformation($"The inter-communicate message updater is{(this.State == ServiceState.Disconnected ? " re-" : " ")}subscribed successful");
			}

			try
			{
				await registerAsync().ConfigureAwait(false);

				if (this.State == ServiceState.Disconnected)
					this.Logger.LogInformation($"The service is re-started successful - PID: {Process.GetCurrentProcess().Id} - URI: {this.ServiceURI}");
				else if (onSuccessAsync != null)
					await onSuccessAsync(this).ConfigureAwait(false);

				this.State = ServiceState.Connected;
			}
			catch (Exception ex)
			{
				this.Logger.LogError($"Cannot {(this.State == ServiceState.Disconnected ? " re-" : " ")}register the service: {ex.Message}", ex);
				if (onErrorAsync != null)
					await onErrorAsync(ex).ConfigureAwait(false);
			}
		}
		#endregion

		#region Send update messages & notifications
		/// <summary>
		/// Sends a message for updating data of client
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendUpdateMessageAsync(message, cancellationToken);

		/// <summary>
		/// Sends updating messages to client
		/// </summary>
		/// <param name="messages">The messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendUpdateMessagesAsync(List<UpdateMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
			=> messages.ForEachAsync((message, token) => this.RTUService.SendUpdateMessageAsync(message, token), cancellationToken);

		/// <summary>
		/// Sends updating messages to client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendUpdateMessagesAsync(messages?.Select(message => message as BaseMessage).ToList(), deviceID, excludedDeviceID, cancellationToken);

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendInterCommunicateMessageAsync(serviceName, message, cancellationToken);

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendInterCommunicateMessageAsync(message, cancellationToken);

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendInterCommunicateMessagesAsync(serviceName, messages?.Select(message => message as BaseMessage).ToList(), cancellationToken);

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendInterCommunicateMessagesAsync(List<CommunicateMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendInterCommunicateMessagesAsync(messages?.Select(message => message as CommunicateMessage).ToList(), cancellationToken);
		#endregion

		#region Send email & web hook messages
		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="message">The email message for sending</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> this.MessagingService.SendEmailAsync(message, cancellationToken);

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="from"></param>
		/// <param name="replyTo"></param>
		/// <param name="to"></param>
		/// <param name="cc"></param>
		/// <param name="bcc"></param>
		/// <param name="subject"></param>
		/// <param name="body"></param>
		/// <param name="smtpServer"></param>
		/// <param name="smtpServerPort"></param>
		/// <param name="smtpServerEnableSsl"></param>
		/// <param name="smtpUsername"></param>
		/// <param name="smtpPassword"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected Task SendEmailAsync(string from, string replyTo, string to, string cc, string bcc, string subject, string body, string smtpServer, int smtpServerPort, bool smtpServerEnableSsl, string smtpUsername, string smtpPassword, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendEmailAsync(new EmailMessage
			{
				From = from,
				ReplyTo = replyTo,
				To = to,
				Cc = cc,
				Bcc = bcc,
				Subject = subject,
				Body = body,
				SmtpServer = smtpServer,
				SmtpServerPort = smtpServerPort,
				SmtpUsername = smtpUsername,
				SmtpPassword = smtpPassword,
				SmtpServerEnableSsl = smtpServerEnableSsl
			}, cancellationToken);

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="from"></param>
		/// <param name="to"></param>
		/// <param name="subject"></param>
		/// <param name="body"></param>
		/// <param name="smtpServer"></param>
		/// <param name="smtpServerPort"></param>
		/// <param name="smtpServerEnableSsl"></param>
		/// <param name="smtpUsername"></param>
		/// <param name="smtpPassword"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected Task SendEmailAsync(string from, string to, string subject, string body, string smtpServer, int smtpServerPort, bool smtpServerEnableSsl, string smtpUsername, string smtpPassword, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendEmailAsync(from, null, to, null, null, subject, body, smtpServer, smtpServerPort, smtpServerEnableSsl, smtpUsername, smtpPassword, cancellationToken);

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="to"></param>
		/// <param name="subject"></param>
		/// <param name="body"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default(CancellationToken))
			=> this.SendEmailAsync(null, to, subject, body, null, 0, false, null, null, cancellationToken);

		/// <summary>
		/// Sends a web hook message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected Task SendWebHookAsync(WebHookMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> this.MessagingService.SendWebHookAsync(message, cancellationToken);

		#endregion

		#region Working with logs
		/// <summary>
		/// Gets or sets the logger
		/// </summary>
		ILogger IServiceComponent.Logger
		{
			get => this.Logger;
			set => this.Logger = value;
		}

		/// <summary>
		/// Gets the logger
		/// </summary>
		public ILogger Logger { get; private set; }

		ConcurrentQueue<Tuple<string, string, string, List<string>, string>> Logs { get; } = new ConcurrentQueue<Tuple<string, string, string, List<string>, string>>();

		string _isDebugResultsEnabled = null, _isDebugStacksEnabled = null;

		/// <summary>
		/// Gets the state to write debug log (from app settings - parameter named 'vieapps:Logs:Debug')
		/// </summary>
		public bool IsDebugLogEnabled => this.Logger != null && this.Logger.IsEnabled(LogLevel.Debug);

		/// <summary>
		/// Gets the state to write debug result into log (from app settings - parameter named 'vieapps:Logs:ShowResults')
		/// </summary>
		public bool IsDebugResultsEnabled => "true".IsEquals(this._isDebugResultsEnabled ?? (this._isDebugResultsEnabled = UtilityService.GetAppSetting("Logs:ShowResults", "false")));

		/// <summary>
		/// Gets the state to write error stack to client (from app settings - parameter named 'vieapps:Logs:ShowStacks')
		/// </summary>
		public bool IsDebugStacksEnabled => "true".IsEquals(this._isDebugStacksEnabled ?? (this._isDebugStacksEnabled = UtilityService.GetAppSetting("Logs:ShowStacks", "false")));

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected async Task WriteLogsAsync(string correlationID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
		{
			// prepare
			correlationID = correlationID ?? UtilityService.NewUUID;

			// write to local logs
			if (exception == null)
				logs?.ForEach(message => logger.Log(mode, $"{message} [{correlationID}]"));
			else
			{
				logs?.ForEach(message => logger.Log(LogLevel.Error, $"{message} [{correlationID}]"));
				logger.Log(LogLevel.Error, $"{exception.Message} [{correlationID}]", exception);
			}

			// write to centerlized logs
			logs = logs ?? new List<string>();
			if (exception != null && exception is WampException)
			{
				var details = (exception as WampException).GetDetails();
				logs.Add($"> Message: {details.Item2}");
				logs.Add($"> Type: {details.Item3}");
			}
			else if (exception != null)
			{
				logs.Add($"> Message: {exception.Message}");
				logs.Add($"> Type: {exception.GetType().ToString()}");
			}

			Tuple<string, string, string, List<string>, string> log = null;
			try
			{
				while (this.Logs.TryDequeue(out log))
					await this.LoggingService.WriteLogsAsync(log.Item1, log.Item2, log.Item3, log.Item4, log.Item5, this.CancellationTokenSource.Token).ConfigureAwait(false);
				await this.LoggingService.WriteLogsAsync(correlationID, serviceName ?? (this.ServiceName ?? "APIGateway"), objectName, logs, exception.GetStack(), this.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch
			{
				if (log != null)
					this.Logs.Enqueue(log);
				this.Logs.Enqueue(new Tuple<string, string, string, List<string>, string>(correlationID, serviceName ?? (this.ServiceName ?? "APIGateway"), objectName, logs, exception.GetStack()));
			}
		}

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, logger, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, this.Logger, logs, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected void WriteLogs(string correlationID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, logger, logs, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected void WriteLogs(string correlationID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogs(correlationID, logger, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected void WriteLogs(string correlationID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogs(correlationID, this.Logger, logs, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected void WriteLogs(string correlationID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogs(correlationID, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, serviceName, objectName, mode);
		#endregion

		#region Call services
		/// <summary>
		/// Calls a business service
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onStart">The action to run when start</param>
		/// <param name="onSuccess">The action to run when success</param>
		/// <param name="onError">The action to run when got an error</param>
		/// <returns>A <see cref="JObject">JSON</see> object that presents the results of the business service</returns>
		protected async Task<JToken> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken), Action<RequestInfo> onStart = null, Action<RequestInfo, JToken> onSuccess = null, Action<RequestInfo, Exception> onError = null)
		{
			var stopwatch = Stopwatch.StartNew();
			try
			{
				onStart?.Invoke(requestInfo);
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, $"Begin process ({requestInfo.Verb} /{requestInfo.ServiceName?.ToLower()}/{requestInfo.ObjectName?.ToLower()}/{requestInfo.GetObjectIdentity()?.ToLower()}) - {requestInfo.Session.AppName} ({requestInfo.Session.AppPlatform}) @ {requestInfo.Session.IP}", null, requestInfo.ServiceName, requestInfo.ObjectName);

				var json = await requestInfo.CallServiceAsync(cancellationToken).ConfigureAwait(false);
				onSuccess?.Invoke(requestInfo, json);

				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, new List<string>
					{
						$"Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}",
						$"Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					}, null, requestInfo.ServiceName, requestInfo.ObjectName).ConfigureAwait(false);

				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(567, cancellationToken).ConfigureAwait(false);
				try
				{
					var json = await requestInfo.CallServiceAsync(cancellationToken).ConfigureAwait(false);
					onSuccess?.Invoke(requestInfo, json);

					if (this.IsDebugResultsEnabled)
						await this.WriteLogsAsync(requestInfo.CorrelationID, new List<string>
						{
							$"Request (re-call): {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}",
							$"Response (re-call): {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
						}, null, requestInfo.ServiceName, requestInfo.ObjectName).ConfigureAwait(false);

					return json;
				}
				catch (Exception)
				{
					throw;
				}
			}
			catch (Exception ex)
			{
				onError?.Invoke(requestInfo, ex);
				throw ex;
			}
			finally
			{
				stopwatch.Stop();
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, $"End process ({requestInfo.Verb} /{requestInfo.ServiceName?.ToLower()}/{requestInfo.ObjectName?.ToLower()}/{requestInfo.GetObjectIdentity()?.ToLower()}) - {requestInfo.Session.AppName} ({requestInfo.Session.AppPlatform}) @ {requestInfo.Session.IP} - Execution times: {stopwatch.GetElapsedTimes()}", null, requestInfo.ServiceName, requestInfo.ObjectName).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Gets a business service
		/// </summary>
		/// <param name="name">The string that presents name of a service</param>
		/// <returns></returns>
		protected Task<IService> GetServiceAsync(string name) => WAMPConnections.GetServiceAsync(name);

		/// <summary>
		/// Gets the unique name of the service for a specific OS platform
		/// </summary>
		/// <param name="osPlatform"></param>
		/// <returns></returns>
		protected string GetUniqueServiceName(string osPlatform)
			=> !string.IsNullOrWhiteSpace(osPlatform) && this.ServiceUniqueNames.TryGetValue(osPlatform, out string name)
				? name
				: null;

		/// <summary>
		/// Gets the unique service of a specific OS platform
		/// </summary>
		/// <param name="osPlatform">The string that presents name of a specifict OS Platform (Windows, Linux or macOS)</param>
		/// <returns></returns>
		protected async Task<IUniqueService> GetUniqueServiceAsync(string osPlatform)
		{
			var name = this.GetUniqueServiceName(osPlatform);
			if (string.IsNullOrWhiteSpace(name))
				return null;

			if (!this.UniqueServices.TryGetValue(name, out IUniqueService service))
			{
				await WAMPConnections.OpenOutgoingChannelAsync().ConfigureAwait(false);
				if (!this.UniqueServices.TryGetValue(name, out service))
				{
					service = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IUniqueService>(ProxyInterceptor.Create(name));
					this.UniqueServices.TryAdd(osPlatform, service);
				}
			}
			return service ?? throw new ServiceNotFoundException($"The unique  service \"{this.ServiceURI}\" for specific OS platform ({osPlatform}) is not found");
		}
		#endregion

		#region Working sessions
		/// <summary>
		/// Gets the sessions of an user. 1st element is session identity, 2nd element is device identity, 3rd element is app info, 4th element is online status
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task<List<Tuple<string, string, string, bool>>> GetSessionsAsync(RequestInfo requestInfo, string userID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var result = await this.CallServiceAsync(new RequestInfo
			{
				Session = requestInfo.Session,
				ServiceName = "users",
				ObjectName = "account",
				Verb = "HEAD",
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", userID ?? requestInfo.Session.User.ID }
				},
				CorrelationID = requestInfo.CorrelationID
			}, cancellationToken).ConfigureAwait(false);

			return (result["Sessions"] as JArray).ToList(info => new Tuple<string, string, string, bool>(info.Get<string>("SessionID"), info.Get<string>("DeviceID"), info.Get<string>("AppInfo"), info.Get<bool>("IsOnline")));
		}
		#endregion

		#region Keys & HTTP URIs
		/// <summary>
		/// Gets a key from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultKey"></param>
		/// <returns></returns>
		protected string GetKey(string name, string defaultKey) => UtilityService.GetAppSetting("Keys:" + name, defaultKey);

		/// <summary>
		/// Gets the key for encrypting/decrypting data with AES
		/// </summary>
		protected string EncryptionKey => this.GetKey("Encryption", "VIEApps-59EF0859-NGX-BC1A-Services-4088-Encryption-9743-Key-51663AB720EF");

		/// <summary>
		/// Gets the key for validating data
		/// </summary>
		protected string ValidationKey => this.GetKey("Validation", "VIEApps-D6C8C563-NGX-26CC-Services-43AC-Validation-9040-Key-E803AF0F36E4");

		/// <summary>
		/// Gets a HTTP URI from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultURI"></param>
		/// <returns></returns>
		protected string GetHttpURI(string name, string defaultURI) => UtilityService.GetAppSetting($"HttpUri:{name}", defaultURI);
		#endregion

		#region Authentication & Authorization
		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		protected bool IsAuthenticated(RequestInfo requestInfo) => requestInfo.IsAuthenticated();

		/// <summary>
		/// The the global privilege role of the user in this service
		/// </summary>
		/// <param name="user"></param>
		/// <returns></returns>
		protected virtual string GetPrivilegeRole(IUser user) => user?.GetPrivilegeRole(this.ServiceName);

		/// <summary>
		/// Gets the default privileges  of the user in this service
		/// </summary>
		/// <param name="user"></param>
		/// <param name="privileges"></param>
		/// <returns></returns>
		protected virtual List<Privilege> GetPrivileges(IUser user, Privileges privileges) => user?.GetPrivileges(privileges, this.ServiceName);

		/// <summary>
		/// Gets the default privilege actions in this service
		/// </summary>
		/// <param name="role"></param>
		/// <returns></returns>
		protected virtual List<string> GetPrivilegeActions(PrivilegeRole role) => role.GetPrivilegeActions();

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public Task<bool> IsSystemAdministratorAsync(IUser user, string correlationID = null)
			=> user != null
				? user.IsSystemAdministratorAsync(correlationID)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public Task<bool> IsSystemAdministratorAsync(Session session, string correlationID = null)
			=> session != null
				? session.IsSystemAdministratorAsync(correlationID)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		public Task<bool> IsSystemAdministratorAsync(RequestInfo requestInfo)
			=> requestInfo != null
				? requestInfo.IsSystemAdministratorAsync()
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <returns></returns>
		public Task<bool> IsServiceAdministratorAsync(IUser user, string serviceName = null)
			=> user != null
				? user.IsServiceAdministratorAsync(serviceName ?? this.ServiceName, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <returns></returns>
		public Task<bool> IsServiceAdministratorAsync(Session session, string serviceName = null)
			=> session != null
				? session.IsServiceAdministratorAsync(serviceName ?? this.ServiceName, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <returns></returns>
		public Task<bool> IsServiceAdministratorAsync(RequestInfo requestInfo)
			=> requestInfo != null
				? requestInfo.IsServiceAdministratorAsync(this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <returns></returns>
		public Task<bool> IsServiceModeratorAsync(IUser user, string serviceName = null)
			=> user != null
				? user.IsServiceModeratorAsync(serviceName ?? this.ServiceName, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="serviceName">The name of service</param>
		/// <returns></returns>
		public Task<bool> IsServiceModeratorAsync(Session session, string serviceName = null)
			=> session != null
				? session.IsServiceModeratorAsync(serviceName ?? this.ServiceName, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <returns></returns>
		public Task<bool> IsServiceModeratorAsync(RequestInfo requestInfo)
			=> requestInfo != null
				? requestInfo.IsServiceModeratorAsync(this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(IUser user, string serviceName, string objectName, string objectIdentity, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> user != null
				? user.IsAuthorizedAsync(serviceName, objectName, objectIdentity, action, privileges, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> requestInfo != null
				? requestInfo.IsAuthorizedAsync(action, privileges, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="entity">The business entity object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, IBusinessEntity entity, Components.Security.Action action, Func<IUser, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
			=> requestInfo != null
				? requestInfo.IsAuthorizedAsync(entity, action, getPrivileges, getActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanManageAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanManageAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanManageAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanManageAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanModerateAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanModerateAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanModerateAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanModerateAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanEditAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanEditAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanEditAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanEditAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanContributeAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanContributeAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanContributeAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanContributeAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanViewAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanViewAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanViewAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanViewAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual Task<bool> CanDownloadAsync(IUser user, string objectName, string objectIdentity)
			=> user != null
				? user.CanDownloadAsync(this.ServiceName, objectName, objectIdentity, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual Task<bool> CanDownloadAsync(IUser user, string systemID, string definitionID, string objectID)
			=> user != null
				? user.CanDownloadAsync(this.ServiceName, systemID, definitionID, objectID, this.GetPrivileges, this.GetPrivilegeActions)
				: Task.FromResult(false);
		#endregion

		#region Working with timers
		/// <summary>
		/// Starts a timer (using ReactiveX)
		/// </summary>
		/// <param name="action">The action to fire</param>
		/// <param name="interval">The elapsed time for firing the action (seconds)</param>
		/// <param name="delay">Delay time (miliseconds) before firing the action</param>
		/// <returns></returns>
		protected IDisposable StartTimer(System.Action action, int interval, int delay = 0)
		{
			interval = interval < 1 ? 1 : interval;
			var timer = Observable.Timer(TimeSpan.FromMilliseconds(delay > 0 ? delay : interval * 1000), TimeSpan.FromSeconds(interval)).Subscribe(_ =>
			{
				try
				{
					action?.Invoke();
				}
				catch (Exception ex)
				{
					this.WriteLogs(UtilityService.NewUUID, $"Error occurred while invoking a timer action: {ex.Message}", ex, this.ServiceName, "Timers");
				}
			});
			this.Timers.Add(timer);
			return timer;
		}

		/// <summary>
		/// Stops all timers
		/// </summary>
		protected void StopTimers() => this.Timers.ForEach(timer => timer.Dispose());
		#endregion

		#region Working with cache
		/// <summary>
		/// Gets the key for working with caching
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <returns></returns>
		protected string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageNumber = 0) where T : class
			=> typeof(T).GetTypeName(true) + "#"
				+ (filter != null ? $"{filter.GetMD5()}:" : "")
				+ (sort != null ? $"{sort.GetMD5()}:" : "")
				+ (pageNumber > 0 ? $"{pageNumber}" : "");

		List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			var key = this.GetCacheKey<T>(filter, sort);
			var keys = new List<string>() { key, $"{key}json", $"{key}total" };
			for (var index = 1; index <= 100; index++)
			{
				keys.Add($"{key}{index}");
				keys.Add($"{key}{index}:json");
				keys.Add($"{key}{index}:total");
			}
			return keys;
		}

		/// <summary>
		/// Clears the related data from the cache storage
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="cache">The cache storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected void ClearRelatedCache<T>(Cache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> cache?.Remove(this.GetRelatedCacheKeys(filter, sort));

		/// <summary>
		/// Clears the related data from the cache storage
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="cache">The cache storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected Task ClearRelatedCacheAsync<T>(Cache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> cache != null
				? cache.RemoveAsync(this.GetRelatedCacheKeys(filter, sort))
				: Task.CompletedTask;
		#endregion

		#region Get runtime exception
		/// <summary>
		/// Gets the runtime exception to throw to caller
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="exception">The exception</param>
		/// <param name="stopwatch">The stop watch</param>
		/// <param name="message">The message</param>
		/// <returns></returns>
		public WampException GetRuntimeException(RequestInfo requestInfo, Exception exception, Stopwatch stopwatch = null, string message = null)
		{
			// normalize exception
			exception = exception != null && exception is RepositoryOperationException
				? exception.InnerException
				: exception;

			// prepare message
			message = string.IsNullOrWhiteSpace(message)
				? exception != null
					? exception.Message
					: $"Error occurred while processing"
				: message;

			// write into logs
			stopwatch?.Stop();
			this.WriteLogs(requestInfo.CorrelationID, new List<string> { $"Error response: {message}{(stopwatch == null ? "" : $" - Execution times: {stopwatch.GetElapsedTimes()}")}", $"Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" }, exception, requestInfo.ServiceName, requestInfo.ObjectName);

			// return the exception
			if (exception is WampException)
				return exception as WampException;

			else
			{
				var details = exception != null
					? new Dictionary<string, object>
					{
						{ "0", exception.StackTrace }
					}
					: null;

				var inner = exception?.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					details.Add(counter.ToString(), inner.StackTrace);
					inner = inner.InnerException;
				}

				return new WampRpcRuntimeException(
					details,
					new Dictionary<string, object>(),
					new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
					{
						{ "RequestInfo", requestInfo.ToJson() }
					},
					message,
					exception
				);
			}
		}
		#endregion

		#region Generate form/view controls
		/// <summary>
		/// Generates the controls of this type (for working with input forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected JToken GenerateFormControls<T>() where T : class => RepositoryMediator.GenerateFormControls<T>();

		/// <summary>
		/// Generates the controls of this type (for working with view forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected JToken GenerateViewControls<T>() where T : class => RepositoryMediator.GenerateFormControls<T>();
		#endregion

		#region Evaluate an Javascript expression
		/// <summary>
		/// Gest the Javascript embed objects
		/// </summary>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <returns></returns>
		protected IDictionary<string, object> GetJsEmbedObjects(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null)
			=> new Dictionary<string, object>(embedObjects ?? new Dictionary<string, object>())
			{
				["__current"] = current,
				["__requestInfo"] = requestInfo,
			};

		/// <summary>
		/// Gest the Javascript embed types
		/// </summary>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected IDictionary<string, Type> GetJsEmbedTypes(IDictionary<string, Type> embedTypes = null)
			=> new Dictionary<string, Type>(embedTypes ?? new Dictionary<string, Type>())
			{
				["RequestInfo"] = typeof(RequestInfo),
				["Session"] = typeof(Session),
				["User"] = typeof(User),
			};

		/// <summary>
		/// Creates the Javascript engine for evaluating an expression
		/// </summary>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected JavaScriptEngineSwitcher.Core.IJsEngine CreateJsEngine(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.CreateJsEngine(this.GetJsEmbedObjects(current, requestInfo, embedObjects), this.GetJsEmbedTypes(embedTypes));

		/// <summary>
		/// Gets the Javascript engine for evaluating an expression
		/// </summary>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected JSPool.PooledJsEngine GetJsEngine(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.GetJsEngine(this.GetJsEmbedObjects(current, requestInfo, embedObjects), this.GetJsEmbedTypes(embedTypes));

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfoJSON' global variable</param>
		/// <returns></returns>
		protected string GetJsExpression(string expression, object current, RequestInfo requestInfo)
			=> Extensions.JsFunctions + Environment.NewLine
				+ @"
				function now() {
					return __now();
				}
				function today() {
					return __today();
				}".Replace("\t", "").Replace("\r", "").Replace("\n", " ") + Environment.NewLine
				+ "var __requestInfoJSON = " + (requestInfo ?? new RequestInfo()).ToJson() + ";" + Environment.NewLine
				+ "(function(__object){__object['__evaluate']=function(){" + Environment.NewLine
				+ (string.IsNullOrWhiteSpace(expression) || expression.Trim().Equals(";")
					? "return undefined;"
					: expression.StartsWith("@")
						? $"return {expression.Right(expression.Length - 1).Trim() + (expression.Trim().EndsWith("();") || expression.Trim().EndsWith("()") ? "" : "();")}"
						: expression.Trim()) + Environment.NewLine
				+ "};return __object.__evaluate();})" + Environment.NewLine
				+ "(" + (current != null
					? (current is JToken
						? current as JToken
						: current.GetType().IsPrimitiveType()
							? new JObject
							{
								{ "__value", new JValue(current) }
							}
							: current.ToJson()
					).ToString(Formatting.None)
					: "{}")
				+ ");";

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object the presents the value that evaluated by the expression</returns>
		protected object JsEvaluate(string expression, object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = this.GetJsEngine(current, requestInfo, embedObjects, embedTypes))
			{
				return jsEngine.JsEvaluate(this.GetJsExpression(expression, current, requestInfo));
			}
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object the presents the value that evaluated by the expression</returns>
		protected T JsEvaluate<T>(string expression, object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.JsCast<T>(this.JsEvaluate(expression, current, requestInfo, embedObjects, embedTypes));

		/// <summary>
		/// Evaluates the collection of Javascript expressions
		/// </summary>
		/// <param name="expressions">The collection of Javascript expression for evaluating, each expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The collection of value that evaluated by the expressions</returns>
		protected IEnumerable<object> JsEvaluate(IEnumerable<string> expressions, object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = this.GetJsEngine(current, requestInfo, embedObjects, embedTypes))
			{
				return expressions.Select(expression => jsEngine.JsEvaluate(this.GetJsExpression(expression, current, requestInfo))).ToList();
			}
		}
		#endregion

		#region Start & Stop
		/// <summary>
		/// Starts the service (the short way - open channels and register service)
		/// </summary>
		/// <param name="onRegisterSuccessAsync"></param>
		/// <param name="onRegisterErrorAsync"></param>
		/// <param name="onIncomingConnectionEstablished"></param>
		/// <param name="onOutgoingConnectionEstablished"></param>
		/// <param name="onIncomingConnectionBroken"></param>
		/// <param name="onOutgoingConnectionBroken"></param>
		/// <param name="onIncomingConnectionError"></param>
		/// <param name="onOutgoingConnectionError"></param>
		/// <returns></returns>
		protected virtual Task StartAsync(Func<ServiceBase, Task> onRegisterSuccessAsync = null, Func<Exception, Task> onRegisterErrorAsync = null, Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null, Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null, Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null, Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null)
		{
			this.Logger.LogInformation($"Attempting to connect to WAMP router [{new Uri(WAMPConnections.GetRouterStrInfo()).GetResolvedURI()}]");
			return Task.WhenAll(
				WAMPConnections.OpenIncomingChannelAsync(
					(sender, arguments) =>
					{
						this.Logger.LogInformation($"Incoming channel to WAMP router is established - Session ID: {WAMPConnections.IncomingChannelSessionID}");
						WAMPConnections.IncomingChannel.Update(WAMPConnections.IncomingChannelSessionID, this.ServiceName, $"Incoming ({this.ServiceURI})");
						if (this.State == ServiceState.Initializing)
							this.State = ServiceState.Ready;

						Task.Run(() => this.RegisterServiceAsync(onRegisterSuccessAsync, onRegisterErrorAsync)).ConfigureAwait(false);
						try
						{
							onIncomingConnectionEstablished?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionEstablished)}\"action: {ex.Message}", ex);
						}
					},
					(sender, arguments) =>
					{
						if (this.State == ServiceState.Connected)
							this.State = ServiceState.Disconnected;

						if (WAMPConnections.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
							this.Logger.LogInformation($"The incoming channel to WAMP router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

						else if (WAMPConnections.IncomingChannel != null)
						{
							this.Logger.LogInformation($"The incoming channel to WAMP router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
							WAMPConnections.IncomingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger.LogDebug(msg, ex), "Incoming");
						}

						try
						{
							onIncomingConnectionBroken?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionBroken)}\"action: {ex.Message}", ex);
						}
					},
					(sender, arguments) =>
					{
						this.Logger.LogDebug($"The incoming channel to WAMP router got an error: {arguments.Exception.Message}", arguments.Exception);
						try
						{
							onIncomingConnectionError?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionError)}\"action: {ex.Message}", ex);
						}
					}
				),
				WAMPConnections.OpenOutgoingChannelAsync(
					(sender, arguments) =>
					{
						this.Logger.LogInformation($"Outgoing channel to WAMP router is established - Session ID: {WAMPConnections.OutgoingChannelSessionID}");
						WAMPConnections.OutgoingChannel.Update(WAMPConnections.OutgoingChannelSessionID, this.ServiceName, $"Outgoing ({this.ServiceURI})");

						try
						{
							this.RTUService = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>(ProxyInterceptor.Create());
							this.MessagingService = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IMessagingService>(ProxyInterceptor.Create());
							this.LoggingService = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<ILoggingService>(ProxyInterceptor.Create());
							this.Logger.LogInformation($"Helper services are{(this.State == ServiceState.Disconnected ? " re-" : " ")}initialized");
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while{(this.State == ServiceState.Disconnected ? " re-" : " ")}initializing helper services", ex);
						}

						Task.Run(async () =>
						{
							try
							{
								while (WAMPConnections.IncomingChannel == null || WAMPConnections.OutgoingChannel == null)
									await Task.Delay(UtilityService.GetRandomNumber(123, 456)).ConfigureAwait(false);
								await this.SendInterCommunicateMessageAsync(new CommunicateMessage
								{
									ServiceName = "APIGateway",
									Type = "Service#RequestUniqueInfo",
									Data = new JObject
									{
										{ "Name", this.ServiceName.Trim().ToLower() }
									}
								}).ConfigureAwait(false);
							}
							catch (Exception ex)
							{
								this.Logger.LogError($"Error occurred while sending request of service info => {ex.Message}", ex);
							}
						}).ConfigureAwait(false);

						try
						{
							onOutgoingConnectionEstablished?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionEstablished)}\"action: {ex.Message}", ex);
						}
					},
					(sender, arguments) =>
					{
						if (WAMPConnections.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
							this.Logger.LogInformation($"The outgoing channel to WAMP router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

						else if (WAMPConnections.OutgoingChannel != null)
						{
							this.Logger.LogInformation($"The outgoing channel to WAMP router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
							WAMPConnections.OutgoingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger.LogDebug(msg, ex), "Outgoing");
						}

						try
						{
							onOutgoingConnectionBroken?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionBroken)}\"action: {ex.Message}", ex);
						}
					},
					(sender, arguments) =>
					{
						this.Logger.LogDebug($"The outgoing channel to WAMP router got an error: {arguments.Exception.Message}", arguments.Exception);
						try
						{
							onOutgoingConnectionError?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionError)}\"action: {ex.Message}", ex);
						}
					}
				)
			);
		}

		/// <summary>
		/// Starts the service
		/// </summary>
		/// <param name="args">The starting arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="nextAsync">The next action to run</param>
		public virtual void Start(string[] args = null, bool initializeRepository = true, Func<IService, Task> nextAsync = null)
		{
			this.ServiceUniqueName = Extensions.GetUniqueName(this.ServiceName, args);
			Task.Run(async () =>
			{
				try
				{
					await this.StartAsync(async service =>
					{
						// initialize repository
						if (initializeRepository)
							try
							{
								await Task.Delay(UtilityService.GetRandomNumber(123, 456)).ConfigureAwait(false);
								this.Logger.LogInformation("Initializing the repository");
								RepositoryStarter.Initialize(
									new[] { this.GetType().Assembly }.Concat(this.GetType().Assembly.GetReferencedAssemblies()
										.Where(n => !n.Name.IsStartsWith("mscorlib") && !n.Name.IsStartsWith("System") && !n.Name.IsStartsWith("Microsoft") && !n.Name.IsEquals("NETStandard")
											&& !n.Name.IsStartsWith("Newtonsoft") && !n.Name.IsStartsWith("WampSharp") && !n.Name.IsStartsWith("Castle.") && !n.Name.IsStartsWith("StackExchange.")
											&& !n.Name.IsStartsWith("MongoDB") && !n.Name.IsStartsWith("MySql") && !n.Name.IsStartsWith("Oracle") && !n.Name.IsStartsWith("Npgsql") && !n.Name.IsStartsWith("Serilog")
											&& !n.Name.IsStartsWith("VIEApps.Components.") && !n.Name.IsStartsWith("VIEApps.Services.Abstractions") && !n.Name.IsStartsWith("VIEApps.Services.Base") && !n.Name.IsStartsWith("VIEApps.Services.APIGateway")
										)
										.Select(n =>
										{
											try
											{
												return Assembly.Load(n);
											}
											catch (Exception ex)
											{
												this.Logger.LogError($"Error occurred while loading an assembly [{n.Name}] => {ex.Message}", ex);
												return null;
											}
										})
										.Where(a => a != null)
										.ToList()
									),
									(log, ex) =>
									{
										if (!this.IsDebugLogEnabled)
										{
											if (ex != null)
												this.Logger.LogError(log, ex);
											else
												this.Logger.LogInformation(log);
										}
									}
								);
							}
							catch (Exception ex)
							{
								this.Logger.LogError($"Error occurred while initializing the repository: {ex.Message}", ex);
							}

						// run the next action
						if (nextAsync != null)
							try
							{
								await nextAsync(this).WithCancellationToken(this.CancellationTokenSource.Token).ConfigureAwait(false);
							}
							catch (Exception ex)
							{
								this.Logger.LogError($"Error occurred while invoking the next action: {ex.Message}", ex);
							}
					}).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while starting the service: {ex.Message}", ex);
				}
			}).ConfigureAwait(false);
		}

		protected bool Stopped { get; private set; } = false;

		/// <summary>
		/// Stops this service (close channels and clean-up)
		/// </summary>
		public void Stop()
		{
			// don't process if already stopped
			if (this.Stopped)
				return;

			// dispose communicators
			this.Stopped = true;
			this.ServiceCommunicator?.Dispose();
			this.GatewayCommunicator?.Dispose();

			// dispose instances and close all
			Task.Run(async () =>
			{
				if (this.ServiceInstance != null)
					try
					{
						await this.ServiceInstance.DisposeAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while deregistering: {ex.Message}", ex);
					}
					finally
					{
						this.ServiceInstance = null;
					}

				if (this.ServiceUniqueInstance != null)
					try
					{
						await this.ServiceUniqueInstance.DisposeAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while deregistering: {ex.Message}", ex);
					}
					finally
					{
						this.ServiceUniqueInstance = null;
					}
			})
			.ContinueWith(task => WAMPConnections.CloseChannels(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(task => this.StopTimers(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(task => this.CancellationTokenSource.Cancel(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(task => this.Logger?.LogDebug("Stopped"), TaskContinuationOptions.OnlyOnRanToCompletion)
			.Wait(3456);
		}

		bool Disposed { get; set; } = false;

		public virtual void Dispose()
		{
			if (!this.Disposed)
			{
				this.Disposed = true;
				this.Stop();
				this.CancellationTokenSource.Dispose();
				this.Logger?.LogDebug("Disposed");
				GC.SuppressFinalize(this);
			}
		}

		~ServiceBase() => this.Dispose();
		#endregion

	}
}