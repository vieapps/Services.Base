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
		protected virtual Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> Task.CompletedTask;

		/// <summary>
		/// Process the inter-communicate message of API Gateway
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		protected virtual Task ProcessGatewayCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
			=> Task.CompletedTask;

		#region Attributes & Properties
		SystemEx.IAsyncDisposable ServiceInstance { get; set; } = null;

		SystemEx.IAsyncDisposable ServiceUniqueInstance { get; set; } = null;

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
		public string ServiceURI => $"services.{(this.ServiceName ?? "unknown").Trim().ToLower()}";

		/// <summary>
		/// Gets the unique name for working with related URIs
		/// </summary>
		public string ServiceUniqueName { get; private set; }

		/// <summary>
		/// Gets the full unique URI of this service
		/// </summary>
		public string ServiceUniqueURI => $"services.{(this.ServiceUniqueName ?? "unknown").Trim().ToLower()}";

		/// <summary>
		/// Gets or sets the single instance of current playing service component
		/// </summary>
		public static ServiceBase ServiceComponent { get; set; }
		#endregion

		#region Register the service
		/// <summary>
		/// Registers the service with API Gateway Router
		/// </summary>
		/// <param name="onSuccessAsync"></param>
		/// <param name="onErrorAsync"></param>
		/// <returns></returns>
		protected async Task RegisterServiceAsync(Func<ServiceBase, Task> onSuccessAsync = null, Func<Exception, Task> onErrorAsync = null)
		{
			var name = this.ServiceName.Trim().ToLower();

			async Task registerCalleesAsync()
			{
				this.ServiceInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(name)).ConfigureAwait(false);
				this.ServiceUniqueInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(this.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
			}

			async Task registerServiceAsync()
			{
				try
				{
					await registerCalleesAsync().ConfigureAwait(false);
				}
				catch
				{
					await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
					try
					{
						await registerCalleesAsync().ConfigureAwait(false);
					}
					catch (Exception)
					{
						throw;
					}
				}
				this.Logger.LogInformation($"The service is{(this.State == ServiceState.Disconnected ? " re-" : " ")}registered successful");

				this.ServiceCommunicator?.Dispose();
				this.ServiceCommunicator = Router.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>($"messages.services.{name}")
					.Subscribe(
						async message => await this.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false),
						exception => this.Logger.LogError($"Error occurred while fetching an inter-communicate message => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.GatewayCommunicator?.Dispose();
				this.GatewayCommunicator = Router.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>("messages.services.apigateway")
					.Subscribe(
						async message => await this.ProcessGatewayCommunicateMessageAsync(message).ConfigureAwait(false),
						exception => this.Logger.LogError($"Error occurred while fetching an inter-communicate message of API Gateway => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.Logger.LogInformation($"The inter-communicate message updater is{(this.State == ServiceState.Disconnected ? " re-" : " ")}subscribed successful");
			}

			try
			{
				await registerServiceAsync().ConfigureAwait(false);

				if (this.State == ServiceState.Disconnected)
					this.Logger.LogInformation($"The service is re-started successful - PID: {Process.GetCurrentProcess().Id} - URI: {this.ServiceURI}");
				else if (onSuccessAsync != null)
					await onSuccessAsync(this).ConfigureAwait(false);

				this.State = ServiceState.Connected;
			}
			catch (Exception ex)
			{
				this.Logger.LogError($"Cannot{(this.State == ServiceState.Disconnected ? " re-" : " ")}register the service => {ex.Message}", ex);
				if (onErrorAsync != null)
					await onErrorAsync(ex).ConfigureAwait(false);
			}
		}
		#endregion

		#region Send update & communicate messages
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
			=> this.RTUService.SendUpdateMessagesAsync(messages, deviceID, excludedDeviceID, cancellationToken);

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
			=> this.RTUService.SendInterCommunicateMessagesAsync(serviceName, messages, cancellationToken);

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task SendInterCommunicateMessagesAsync(List<CommunicateMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
			=> this.RTUService.SendInterCommunicateMessagesAsync(messages, cancellationToken);
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

		#region Loggings
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

		string _isDebugResultsEnabled = null, _isDebugStacksEnabled = null, _isDebugAuthorizationsEnabled = null;

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
		/// Gets the state to write debug logs of authorization (from app settings - parameter named 'vieapps:Logs:ShowAuthorizations')
		/// </summary>
		public bool IsDebugAuthorizationsEnabled => "true".IsEquals(this._isDebugAuthorizationsEnabled ?? (this._isDebugAuthorizationsEnabled = UtilityService.GetAppSetting("Logs:ShowAuthorizations", "false")));

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
				await this.LoggingService.WriteLogsAsync(correlationID, serviceName ?? this.ServiceName ?? "APIGateway", objectName, logs, exception.GetStack(), this.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch
			{
				if (log != null)
					this.Logs.Enqueue(log);
				this.Logs.Enqueue(new Tuple<string, string, string, List<string>, string>(correlationID, serviceName ?? this.ServiceName ?? "APIGateway", objectName, logs, exception.GetStack()));
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
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(RequestInfo requestInfo, List<string> logs, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(requestInfo.CorrelationID, this.Logger, logs, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(RequestInfo requestInfo, string log, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(requestInfo, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, mode);

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

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected void WriteLogs(RequestInfo requestInfo, List<string> logs, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(requestInfo, logs, exception, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected void WriteLogs(RequestInfo requestInfo, string log, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogs(requestInfo, !string.IsNullOrWhiteSpace(log) ? new List<string> { log } : null, exception, mode);
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
			var objectName = this.ServiceName.IsEquals(requestInfo.ServiceName) ? "" : requestInfo.ServiceName;
			try
			{
				onStart?.Invoke(requestInfo);
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, $"Start call service {requestInfo.Verb} {requestInfo.GetURI()} - {requestInfo.Session.AppName} ({requestInfo.Session.AppPlatform}) @ {requestInfo.Session.IP}", null, this.ServiceName, objectName).ConfigureAwait(false);

				var json = await Router.GetService(requestInfo.ServiceName).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
				onSuccess?.Invoke(requestInfo, json);

				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo.CorrelationID, "Call service successful" + "\r\n" +
						$"Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
						$"Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					, null, this.ServiceName, objectName).ConfigureAwait(false);

				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(567, cancellationToken).ConfigureAwait(false);
				try
				{
					var json = await Router.GetService(requestInfo.ServiceName).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
					onSuccess?.Invoke(requestInfo, json);

					if (this.IsDebugResultsEnabled)
						await this.WriteLogsAsync(requestInfo.CorrelationID, "Re-call service successful" + "\r\n" +
							$"Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
							$"Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
						, null, this.ServiceName, objectName).ConfigureAwait(false);

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
					await this.WriteLogsAsync(requestInfo.CorrelationID, $"Call service finished in {stopwatch.GetElapsedTimes()}", null, this.ServiceName, objectName).ConfigureAwait(false);
			}
		}
		#endregion

		#region Sessions
		/// <summary>
		/// Gets the sessions of an user. 1st element is session identity, 2nd element is device identity, 3rd element is app info, 4th element is online status
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="userID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task<List<Tuple<string, string, string, bool>>> GetSessionsAsync(RequestInfo requestInfo, string userID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var result = await this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Users", "Account", "HEAD")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", userID ?? requestInfo.Session.User.ID }
				},
				CorrelationID = requestInfo.CorrelationID
			}, cancellationToken).ConfigureAwait(false);
			return (result["Sessions"] as JArray).ToList(info => new Tuple<string, string, string, bool>(info.Get<string>("SessionID"), info.Get<string>("DeviceID"), info.Get<string>("AppInfo"), info.Get<bool>("IsOnline")));
		}
		#endregion

		#region Settings: Keys, Http URIs, Paths
		/// <summary>
		/// Gets the key for encrypting/decrypting data with AES
		/// </summary>
		protected string EncryptionKey => this.GetKey("Encryption", "VIEApps-59EF0859-NGX-BC1A-Services-4088-Encryption-9743-Key-51663AB720EF");

		/// <summary>
		/// Gets the key for validating data
		/// </summary>
		protected string ValidationKey => this.GetKey("Validation", "VIEApps-D6C8C563-NGX-26CC-Services-43AC-Validation-9040-Key-E803AF0F36E4");

		/// <summary>
		/// Gets a key from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultKey"></param>
		/// <returns></returns>
		protected string GetKey(string name, string defaultKey)
			=> UtilityService.GetAppSetting("Keys:" + name, defaultKey);

		/// <summary>
		/// Gets settings of an HTTP URI from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultURI"></param>
		/// <returns></returns>
		protected string GetHttpURI(string name, string defaultURI)
			=> UtilityService.GetAppSetting($"HttpUri:{name}", defaultURI);

		/// <summary>
		/// Gets settings of a directory path from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultPath"></param>
		/// <returns></returns>
		protected string GetPath(string name, string defaultPath = null)
			=> UtilityService.GetAppSetting($"Path:{name}", defaultPath);
		#endregion

		#region Authentication
		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="session">The session that contains user information</param>
		/// <returns></returns>
		protected bool IsAuthenticated(Session session)
			=> session != null && session.User != null && session.User.IsAuthenticated;

		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		protected bool IsAuthenticated(RequestInfo requestInfo)
			=> this.IsAuthenticated(requestInfo?.Session);
		#endregion

		#region Data of authorizations
		/// <summary>
		/// Gets the default privileges of this service (anonymouse can view)
		/// </summary>
		protected virtual Privileges Privileges
			=> new Privileges(true);

		/// <summary>
		/// Gets the default privileges of an user in this service (viewer)
		/// </summary>
		/// <param name="user"></param>
		/// <param name="serviceName">The name of the service</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <returns></returns>
		protected virtual List<Privilege> GetPrivileges(IUser user, string serviceName, string objectName)
			=> new List<Privilege>
			{
				new Privilege(serviceName, objectName, "", PrivilegeRole.Viewer)
			}
			.Where(privilege => privilege.ServiceName.IsEquals(this.ServiceName))
			.ToList();

		/// <summary>
		/// Gets the default actions of a privilege role in this service
		/// </summary>
		/// <param name="role"></param>
		/// <returns></returns>
		protected virtual List<Components.Security.Action> GetActions(PrivilegeRole role)
			=> role.GetActions();

		/// <summary>
		/// Gets the business object that specified by identity of entity definition and object
		/// </summary>
		/// <param name="definitionID"></param>
		/// <param name="objectID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task<IBusinessEntity> GetBusinessObjectAsync(string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
		{
			var @object = !string.IsNullOrWhiteSpace(definitionID) && definitionID.IsValidUUID() && !string.IsNullOrWhiteSpace(objectID) && objectID.IsValidUUID()
				? await RepositoryMediator.GetAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false)
				: null;
			return @object != null && @object is IBusinessEntity
				? @object as IBusinessEntity
				: null;
		}
		#endregion

		#region Role-based authorizations
		/// <summary>
		/// Determines the user is system administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsSystemAdministratorAsync(IUser user, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (user != null && user.IsAuthenticated)
			{
				correlationID = correlationID ?? UtilityService.NewUUID;
				try
				{
					var @is = "Users".IsEquals(this.ServiceName) ? user.IsSystemAdministrator : false;
					if (!@is && !"Users".IsEquals(this.ServiceName))
					{
						var response = await this.CallServiceAsync(new RequestInfo(new Session { User = new User(user) }, "Users", "Account", "GET")
						{
							Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								{ "IsSystemAdministrator", "" }
							},
							CorrelationID = correlationID
						}, cancellationToken).ConfigureAwait(false);
						@is = user.ID.IsEquals(response.Get<string>("ID")) && response.Get<bool>("IsSystemAdministrator");
					}

					if (this.IsDebugAuthorizationsEnabled)
						this.WriteLogs(correlationID, $"Determines the user ({user.ID}) is system administrator => {@is}", null, this.ServiceName, "Authorization");
					return @is;
				}
				catch (Exception ex)
				{
					this.WriteLogs(correlationID, $"Error occurred while determining the user ({user.ID}) is system administrator => {ex.Message}", ex, this.ServiceName, "Authorization", LogLevel.Error);
				}
			}
			return false;
		}

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsSystemAdministratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsSystemAdministratorAsync(session?.User, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsSystemAdministratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsSystemAdministratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsAdministratorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
			if (!@is && user != null)
			{
				privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
				@is = user.IsAdministrator(this.ServiceName, objectName) || user.IsAdministrator(privileges);
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is administrator of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsAdministratorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAdministratorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsServiceAdministratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAdministratorAsync(session?.User, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsServiceAdministratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsServiceAdministratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is moderator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsModeratorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsModerator(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsModerator(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is moderator of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is moderator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsModeratorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsModeratorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsServiceModeratorAsync(IUser user, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsModeratorAsync(user, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsServiceModeratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsServiceModeratorAsync(session?.User, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsServiceModeratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsServiceModeratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is editor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsEditorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsEditor(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsEditor(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is editor of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is editor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsEditorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsEditorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is contributor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsContributorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsContributor(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsContributor(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is contributor of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is contributor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsContributorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsContributorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is viewer or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsViewerAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsViewer(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsViewer(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is viewer of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is viewer or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsViewerAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsViewerAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is downloader or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<bool> IsDownloaderAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsDownloader(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsDownloader(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is downloader of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Definition: {definitionID ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user is downloader or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task<bool> IsDownloaderAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsDownloaderAsync(user, objectName, null, null, correlationID, cancellationToken);
		#endregion

		#region Action-based authorizations
		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectID">The identity of the service's object</param>
		/// <param name="action">The action to perform on the service's object</param>
		/// <param name="privileges">The working privileges of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsAuthorizedAsync(IUser user, string objectName, string objectID, Components.Security.Action action, Privileges privileges, Func<IUser, string, string, List<Privilege>> getPrivileges, Func<PrivilegeRole, List<Components.Security.Action>> getActions, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			var @is = false;
			if (user != null)
			{
				@is = user.IsAuthorized(this.ServiceName, objectName, objectID, action, privileges ?? this.Privileges, getPrivileges ?? this.GetPrivileges, getActions ?? this.GetActions);
				if (!@is)
					@is = await this.IsAdministratorAsync(user, objectName, correlationID, cancellationToken).ConfigureAwait(false);
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user can perform the {action} action => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID}")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {(privileges ?? this.Privileges)?.ToJson()}"
				, null, this.ServiceName, "Authorization");
			return @is;
		}

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="session">The session that contains user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectID">The identity of the service's object</param>
		/// <param name="action">The action to perform on the service's object</param>
		/// <param name="privileges">The working privileges of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(Session session, string objectName, string objectID, Components.Security.Action action, Privileges privileges, Func<IUser, string, string, List<Privilege>> getPrivileges, Func<PrivilegeRole, List<Components.Security.Action>> getActions, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(session?.User, objectName, objectID, action, privileges, getPrivileges, getActions, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectID">The identity of the service's object</param>
		/// <param name="action">The action to perform on the service's object</param>
		/// <param name="privileges">The working privileges of the service's object</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, string objectName, string objectID, Components.Security.Action action, Privileges privileges, Func<IUser, string, string, List<Privilege>> getPrivileges, Func<PrivilegeRole, List<Components.Security.Action>> getActions, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(requestInfo?.Session, objectName, objectID, action, privileges, getPrivileges, getActions, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="action">The action to perform on the service's object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, string objectName, Components.Security.Action action, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(requestInfo, objectName, null, action, null, null, null, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="object">The business  object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(IUser user, string objectName, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(user, objectName ?? @object?.GetTypeName(true), @object?.ID, action, @object?.WorkingPrivileges, getPrivileges, getActions, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="session">The session that contains user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="object">The business  object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="correlationID">The correlation identity</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(Session session, string objectName, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(session?.User, objectName, @object, action, getPrivileges, getActions, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="object">The business  object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the privileges when the user got empty/null privilege</param>
		/// <param name="getActions">The function to prepare the actions when the matched privilege got empty/null action</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, CancellationToken cancellationToken = default(CancellationToken))
			=> this.IsAuthorizedAsync(requestInfo?.Session, requestInfo.ObjectName, @object, action, getPrivileges, getActions, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is able to perform the manage action or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanManageAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsAdministratorAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Full, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanModerateAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsModeratorAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Approve, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanEditAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsEditorAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Update, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanContributeAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsContributorAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Create, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanViewAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsViewerAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.View, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public virtual async Task<bool> CanDownloadAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default(CancellationToken))
			=> await this.IsDownloaderAsync(user, objectName, definitionID, objectID, null, cancellationToken).ConfigureAwait(false)
				? true
				: await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Download, (await this.GetBusinessObjectAsync(definitionID, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);
		#endregion

		#region Files, Thumbnails & Attachments
		/// <summary>
		/// Gets the collection of thumbnails
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetThumbnailsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Files", "Thumbnail")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["object-identity"] = "search",
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.Header.TryGetValue("x-app-token", out var appToken) ? appToken : "").GetHMACSHA256(this.ValidationKey),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)
					},
					CorrelationID = requestInfo.CorrelationID
				}, cancellationToken);

		/// <summary>
		/// Gets the collection of attachments
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetAttachmentsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Files", "Attachment")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["object-identity"] = "search",
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.Header.TryGetValue("x-app-token", out var appToken) ? appToken : "").GetHMACSHA256(this.ValidationKey),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)
					},
					CorrelationID = requestInfo.CorrelationID
				}, cancellationToken);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetFilesAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Files")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.Header.TryGetValue("x-app-token", out var appToken) ? appToken : "").GetHMACSHA256(this.ValidationKey),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)
					},
					CorrelationID = requestInfo.CorrelationID
				}, cancellationToken);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included) as official
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemID"></param>
		/// <param name="definitionID"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> MarkFilesAsOfficialAsync(RequestInfo requestInfo, string systemID = null, string definitionID = null, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default(CancellationToken))
			=> requestInfo == null || requestInfo.Session == null
				? Task.FromResult<JToken>(null)
				: this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Files", null, "PATCH")
				{
					Header = requestInfo.Header,
					Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["x-service-name"] = requestInfo.ServiceName,
						["x-object-name"] = requestInfo.ObjectName,
						["x-system-id"] = systemID,
						["x-definition-id"] = definitionID,
						["x-object-id"] = objectID ?? requestInfo.GetObjectIdentity(),
						["x-object-title"] = objectTitle
					},
					Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
					{
						["Signature"] = (requestInfo.Header.TryGetValue("x-app-token", out var appToken) ? appToken : "").GetHMACSHA256(this.ValidationKey),
						["SessionID"] = requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)
					},
					CorrelationID = requestInfo.CorrelationID
				}, cancellationToken);
		#endregion

		#region Timers
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
		protected void StopTimers()
			=> this.Timers.ForEach(timer => timer.Dispose());
		#endregion

		#region Caching
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
				+ (filter != null ? $"{filter.GetUUID()}:" : "")
				+ (sort != null ? $"{sort.GetUUID()}:" : "")
				+ (pageNumber > 0 ? $"{pageNumber}" : "");

		List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			var key = this.GetCacheKey(filter, sort);
			var keys = new List<string> { key, $"{key}json", $"{key}total" };
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

		#region Runtime exception
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
			this.WriteLogs(requestInfo, new List<string>
			{
				$"Error response: {message}{(stopwatch == null ? "" : $" - Execution times: {stopwatch.GetElapsedTimes()}")}",
				$"Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
			}, exception);

			// return the exception
			if (exception is WampException)
				return exception as WampException;

			else
			{
				var details = exception != null
					? new Dictionary<string, object> { ["0"] = exception.StackTrace }
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
					new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["RequestInfo"] = requestInfo.ToJson() },
					message,
					exception
				);
			}
		}
		#endregion

		#region Controls of forms/views
		/// <summary>
		/// Generates the controls of this type (for working with input forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected JToken GenerateFormControls<T>() where T : class
			=> RepositoryMediator.GenerateFormControls<T>();

		/// <summary>
		/// Generates the controls of this type (for working with view forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected JToken GenerateViewControls<T>() where T : class
			=> RepositoryMediator.GenerateFormControls<T>();
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
			=> Extensions.GetJsEmbedObjects(current, requestInfo, embedObjects);

		/// <summary>
		/// Gest the Javascript embed types
		/// </summary>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected IDictionary<string, Type> GetJsEmbedTypes(IDictionary<string, Type> embedTypes = null)
			=> Extensions.GetJsEmbedTypes(embedTypes);

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
			=> Extensions.GetJsExpression(expression, current, requestInfo);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object the presents the value that evaluated by the expression</returns>
		protected object JsEvaluate(string expression, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = this.GetJsEngine(current, requestInfo, embedObjects, embedTypes))
			{
				var jsExpression = this.GetJsExpression(expression, current, requestInfo);
				return jsEngine.JsEvaluate(jsExpression);
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
		protected T JsEvaluate<T>(string expression, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
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
		protected IEnumerable<object> JsEvaluate(IEnumerable<string> expressions, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = this.GetJsEngine(current, requestInfo, embedObjects, embedTypes))
			{
				return expressions.Select(expression => jsEngine.JsEvaluate(this.GetJsExpression(expression, current, requestInfo))).ToList();
			}
		}
		#endregion

		#region Start & Stop
		/// <summary>
		/// Starts the service (the short way - connect to API Gateway Router and register the service)
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
			this.Logger.LogInformation($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			return Router.ConnectAsync(
				(sender, arguments) =>
				{
					Router.IncomingChannel.Update(arguments.SessionId, this.ServiceName, $"Incoming ({this.ServiceURI})");
					this.Logger.LogInformation($"The incoming channel to API Gateway Router is established - Session ID: {arguments.SessionId}");
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

					if (Router.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
						this.Logger.LogInformation($"The incoming channel to API Gateway Router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

					else if (Router.IncomingChannel != null)
					{
						this.Logger.LogInformation($"The incoming channel to API Gateway Router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
						Router.IncomingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger.LogDebug(msg, ex), "Incoming");
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
					this.Logger.LogError($"Got an unexpected error of the incoming channel to API Gateway Router => {arguments.Exception.Message}", arguments.Exception);
					try
					{
						onIncomingConnectionError?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionError)}\"action: {ex.Message}", ex);
					}
				},
				(sender, arguments) =>
				{
					Router.OutgoingChannel.Update(arguments.SessionId, this.ServiceName, $"Outgoing ({this.ServiceURI})");
					this.Logger.LogInformation($"The outgoing channel to API Gateway Router is established - Session ID: {arguments.SessionId}");

					try
					{
						this.RTUService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>(ProxyInterceptor.Create());
						this.MessagingService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IMessagingService>(ProxyInterceptor.Create());
						this.LoggingService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<ILoggingService>(ProxyInterceptor.Create());
						this.Logger.LogInformation($"Helper services are{(this.State == ServiceState.Disconnected ? " re-" : " ")}initialized");
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while{(this.State == ServiceState.Disconnected ? " re-" : " ")}initializing helper services", ex);
					}

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
					if (Router.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
						this.Logger.LogInformation($"The outgoing channel to API Gateway Router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

					else if (Router.OutgoingChannel != null)
					{
						this.Logger.LogInformation($"The outgoing channel to API Gateway Router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
						Router.OutgoingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger.LogDebug(msg, ex), "Outgoing");
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
					this.Logger.LogError($"Got an unexpected error of the outgoing channel to API Gateway Router => {arguments.Exception.Message}", arguments.Exception);
					try
					{
						onOutgoingConnectionError?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionError)}\"action: {ex.Message}", ex);
					}
				},
				this.CancellationTokenSource.Token
			);
		}

		/// <summary>
		/// Starts the service
		/// </summary>
		/// <param name="args">The arguments</param>
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

						// default privileges
						if (this.IsDebugLogEnabled)
							this.Logger.LogInformation($"Default working privileges: {this.Privileges?.ToJson()}");

						// run the next action
						if (nextAsync != null)
							try
							{
								await nextAsync(this).WithCancellationToken(this.CancellationTokenSource.Token).ConfigureAwait(false);
							}
							catch (Exception ex)
							{
								this.Logger.LogError($"Error occurred while invoking the next action => {ex.Message}", ex);
							}

						// register the service with API Gateway
						try
						{
							await this.SendServiceInfoAsync(args, true).ConfigureAwait(false);
						}
						catch (Exception ex)
						{
							this.Logger.LogError($"Error occurred while sending info to API Gateway => {ex.Message}", ex);
						}
					}).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger.LogError($"Error occurred while starting the service: {ex.Message}", ex);
				}
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Gets the stopped state of the service
		/// </summary>
		protected bool Stopped { get; private set; } = false;

		/// <summary>
		/// Stops this service (unregister, disconnect from API Gateway Router and clean-up)
		/// </summary>
		/// <param name="args">The arguments</param>
		public void Stop(string[] args = null)
		{
			// don't process if already stopped
			if (this.Stopped)
				return;

			// assign the flag
			this.Stopped = true;

			// dispose
			Task.Run(async () =>
				{
					try
					{
						await this.SendServiceInfoAsync(args, false).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger.LogError($"Error occurred while sending info to API Gateway => {ex.Message}", ex);
					}
				})
				.ContinueWith(_ =>
				{
					this.ServiceCommunicator?.Dispose();
					this.GatewayCommunicator?.Dispose();
				}, TaskContinuationOptions.OnlyOnRanToCompletion)
				.ContinueWith(async _ =>
				{
					if (this.ServiceInstance != null)
					try
					{
						await this.ServiceInstance.DisposeAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while unregistering => {ex.Message}", ex);
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
						this.Logger?.LogError($"Error occurred while unregistering => {ex.Message}", ex);
					}
					finally
					{
						this.ServiceUniqueInstance = null;
					}
			}, TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(_ => Router.Disconnect(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(_ => this.StopTimers(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(_ => this.CancellationTokenSource.Cancel(), TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(_ => this.Logger?.LogDebug("Stopped"), TaskContinuationOptions.OnlyOnRanToCompletion)
			.Wait(1234);
		}

		Task SendServiceInfoAsync(string[] args, bool running)
		{
			var arguments = (args ?? new string[] { }).Where(arg => !arg.IsStartsWith("/controller-id:")).ToArray();
			var invokeInfo = arguments.FirstOrDefault(a => a.IsStartsWith("/call-user:")) ?? "";
			if (!string.IsNullOrWhiteSpace(invokeInfo))
			{
				invokeInfo = invokeInfo.Replace(StringComparison.OrdinalIgnoreCase, "/call-user:", "").UrlDecode();
				var host = arguments.FirstOrDefault(a => a.IsStartsWith("/call-host:"));
				var platform = arguments.FirstOrDefault(a => a.IsStartsWith("/call-platform:"));
				var os = arguments.FirstOrDefault(a => a.IsStartsWith("/call-os:"));
				if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(os))
					invokeInfo += $" [Host: {host.Replace(StringComparison.OrdinalIgnoreCase, "/call-host:", "").UrlDecode()} - Platform: {platform.Replace(StringComparison.OrdinalIgnoreCase, "/call-platform:", "").UrlDecode()} @ {os.Replace(StringComparison.OrdinalIgnoreCase, "/call-os:", "").UrlDecode()}]";
			}
			return this.SendInterCommunicateMessageAsync(new CommunicateMessage("APIGateway")
			{
				Type = "Service#Info",
				Data = new ServiceInfo
				{
					Name = this.ServiceName.ToLower(),
					UniqueName = Extensions.GetUniqueName(this.ServiceName, arguments),
					ControllerID = args.FirstOrDefault(arg => arg.IsStartsWith("/controller-id:"))?.Replace("/controller-id:", "") ?? "Unknown",
					InvokeInfo = invokeInfo,
					Available = true,
					Running = running
				}.ToJson()
			});
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