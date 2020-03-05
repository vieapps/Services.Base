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
	/// Base of all microservices
	/// </summary>
	public abstract class ServiceBase : IService, IUniqueService, IServiceComponent
	{
		/// <summary>
		/// Gets the name of the service (for working with related URIs)
		/// </summary>
		public abstract string ServiceName { get; }

		/// <summary>
		/// Process the request of the service
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public abstract Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default);

		/// <summary>
		/// Processes the inter-communicate messages between the services' instances
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		protected virtual Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		/// <summary>
		/// Processes the inter-communicate messages between the service and API Gateway
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		protected virtual Task ProcessGatewayCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		#region Properties
		IAsyncDisposable ServiceInstance { get; set; }

		IAsyncDisposable ServiceUniqueInstance { get; set; }

		IDisposable ServiceCommunicator { get; set; }

		IDisposable GatewayCommunicator { get; set; }

		/// <summary>
		/// Gets the real-time updater (RTU) service
		/// </summary>
		protected IRTUService RTUService { get; private set; }

		/// <summary>
		/// Gets the logging service
		/// </summary>
		protected ILoggingService LoggingService { get; private set; }

		/// <summary>
		/// Gets the messaging service
		/// </summary>
		protected IMessagingService MessagingService { get; private set; }

		/// <summary>
		/// Gets the cancellation token source
		/// </summary>
		internal protected CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();

		/// <summary>
		/// Gets the collection of timers
		/// </summary>
		internal protected List<IDisposable> Timers { get; private set; } = new List<IDisposable>();

		/// <summary>
		/// Gets the state of the service
		/// </summary>
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

		#region Register/Unregister the service
		/// <summary>
		/// Registers the service with API Gateway
		/// </summary>
		/// <param name="onSuccess">The action to run when the service was registered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		protected virtual async Task RegisterServiceAsync(Action<ServiceBase> onSuccess = null, Action<Exception> onError = null)
		{
			this.ServiceUniqueName = this.ServiceUniqueName ?? Extensions.GetUniqueName(this.ServiceName);

			async Task registerCalleesAsync()
			{
				this.ServiceInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(this.ServiceName)).ConfigureAwait(false);
				this.ServiceUniqueInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => this, RegistrationInterceptor.Create(this.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
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
				this.Logger?.LogDebug($"The service was{(this.State == ServiceState.Disconnected ? " re-" : " ")}registered successful");

				this.ServiceCommunicator?.Dispose();
				this.ServiceCommunicator = Router.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>($"messages.services.{this.ServiceName.Trim().ToLower()}")
					.Subscribe(
						async message => await this.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false),
						exception => this.Logger?.LogError($"Error occurred while fetching an inter-communicate message => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.GatewayCommunicator?.Dispose();
				this.GatewayCommunicator = Router.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>("messages.services.apigateway")
					.Subscribe(
						async message => await this.ProcessGatewayCommunicateMessageAsync(message).ConfigureAwait(false),
						exception => this.Logger?.LogError($"Error occurred while fetching an inter-communicate message of API Gateway => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.Logger?.LogDebug($"The inter-communicate message updater was{(this.State == ServiceState.Disconnected ? " re-" : " ")}subscribed successful");
			}

			try
			{
				while (Router.IncomingChannel == null)
					await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);

				await registerServiceAsync().ConfigureAwait(false);

				if (this.State == ServiceState.Disconnected)
					this.Logger?.LogDebug("The service was re-started successful");

				this.State = ServiceState.Connected;
				onSuccess?.Invoke(this);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Cannot{(this.State == ServiceState.Disconnected ? " re-" : " ")}register the service => {ex.Message}", ex);
				onError?.Invoke(ex);
			}
		}

		/// <summary>
		/// Unregisters the service with API Gateway
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="onSuccess">The action to run when the service was unregistered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		protected virtual async Task UnregisterServiceAsync(string[] args, bool available = true, Action<ServiceBase> onSuccess = null, Action<Exception> onError = null)
		{
			// send information to API Gateway
			try
			{
				await this.SendServiceInfoAsync(args, false, available).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while sending info to API Gateway => {ex.Message}", ex);
				onError?.Invoke(ex);
			}

			// dispose all communicators
			try
			{
				this.ServiceCommunicator?.Dispose();
				this.GatewayCommunicator?.Dispose();
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while disposing the services' communicators => {ex.Message}", ex);
				onError?.Invoke(ex);
			}

			// dispose all instances
			try
			{
				await Task.WhenAll(
					this.ServiceInstance != null ? this.ServiceInstance.DisposeAsync().AsTask() : Task.CompletedTask,
					this.ServiceUniqueInstance != null ? this.ServiceUniqueInstance.DisposeAsync().AsTask() : Task.CompletedTask
				).ConfigureAwait(false);
				onSuccess?.Invoke(this);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while disposing the services' instances => {ex.Message}", ex);
				onError?.Invoke(ex);
			}
			finally
			{
				this.ServiceInstance = null;
				this.ServiceUniqueInstance = null;
			}
		}

		/// <summary>
		/// Initializes the helper services from API Gateway
		/// </summary>
		/// <param name="onSuccess">The action to run when the service was registered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		protected virtual async Task InitializeHelperServicesAsync(Action<ServiceBase> onSuccess = null, Action<Exception> onError = null)
		{
			try
			{
				while (Router.OutgoingChannel == null)
					await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);

				this.RTUService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>(ProxyInterceptor.Create());
				this.MessagingService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IMessagingService>(ProxyInterceptor.Create());
				this.LoggingService = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<ILoggingService>(ProxyInterceptor.Create());
				this.Logger?.LogDebug($"The helper services are{(this.State == ServiceState.Disconnected ? " re-" : " ")}initialized");

				onSuccess?.Invoke(this);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while{(this.State == ServiceState.Disconnected ? " re-" : " ")}initializing the helper services", ex);
				onError?.Invoke(ex);
			}
		}
		#endregion

		#region Send update & communicate messages
		/// <summary>
		/// Sends a message for updating data to all connected clients
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default)
			=> this.RTUService.SendUpdateMessageAsync(message, cancellationToken);

		/// <summary>
		/// Sends the updating messages to all connected clients
		/// </summary>
		/// <param name="messages">The messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendUpdateMessagesAsync(List<UpdateMessage> messages, CancellationToken cancellationToken = default)
			=> messages.ForEachAsync((message, token) => this.RTUService.SendUpdateMessageAsync(message, token), cancellationToken);

		/// <summary>
		/// Sends the updating messages to all connected clients
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null, CancellationToken cancellationToken = default)
			=> this.RTUService.SendUpdateMessagesAsync(messages, deviceID, excludedDeviceID, cancellationToken);

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message, CancellationToken cancellationToken = default)
			=> this.RTUService.SendInterCommunicateMessageAsync(serviceName, message, cancellationToken);

		/// <summary>
		/// Send a message for communicating of other services
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
			=> this.RTUService.SendInterCommunicateMessageAsync(message, cancellationToken);

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages, CancellationToken cancellationToken = default)
			=> this.RTUService.SendInterCommunicateMessagesAsync(serviceName, messages, cancellationToken);

		/// <summary>
		/// Send a message for communicating of other services
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendInterCommunicateMessagesAsync(List<CommunicateMessage> messages, CancellationToken cancellationToken = default)
			=> this.RTUService.SendInterCommunicateMessagesAsync(messages, cancellationToken);

		/// <summary>
		/// Sends the service information to API Gateway
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="running">The running state</param>
		/// <param name="available">The available state</param>
		/// <returns></returns>
		protected virtual Task SendServiceInfoAsync(string[] args, bool running, bool available = true)
			=> this.RTUService.SendServiceInfoAsync(this.ServiceName, args, running, available);
		#endregion

		#region Send email & web hook messages
		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="message">The email message for sending</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
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
		protected virtual Task SendEmailAsync(string from, string replyTo, string to, string cc, string bcc, string subject, string body, string smtpServer, int smtpServerPort, bool smtpServerEnableSsl, string smtpUsername, string smtpPassword, CancellationToken cancellationToken = default)
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
		protected virtual Task SendEmailAsync(string from, string to, string subject, string body, string smtpServer, int smtpServerPort, bool smtpServerEnableSsl, string smtpUsername, string smtpPassword, CancellationToken cancellationToken = default)
			=> this.SendEmailAsync(from, null, to, null, null, subject, body, smtpServer, smtpServerPort, smtpServerEnableSsl, smtpUsername, smtpPassword, cancellationToken);

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="to"></param>
		/// <param name="subject"></param>
		/// <param name="body"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected virtual Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
			=> this.SendEmailAsync(null, to, subject, body, null, 0, false, null, null, cancellationToken);

		/// <summary>
		/// Sends a web hook message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected virtual Task SendWebHookAsync(WebHookMessage message, CancellationToken cancellationToken = default)
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

		ConcurrentQueue<Tuple<string, string, string, string, string, List<string>, string>> Logs { get; } = new ConcurrentQueue<Tuple<string, string, string, string, string, List<string>, string>>();

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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual async Task WriteLogsAsync(string correlationID, string developerID, string appID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
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

			Tuple<string, string, string, string, string, List<string>, string> log = null;
			try
			{
				while (this.Logs.TryDequeue(out log))
					await this.LoggingService.WriteLogsAsync(log.Item1, log.Item2, log.Item3, log.Item4, log.Item5, log.Item6, log.Item7, this.CancellationTokenSource.Token).ConfigureAwait(false);
				await this.LoggingService.WriteLogsAsync(correlationID, developerID, appID, serviceName ?? this.ServiceName ?? "APIGateway", objectName, logs, exception?.GetStack() ?? "", this.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch
			{
				if (log != null)
					this.Logs.Enqueue(log);
				this.Logs.Enqueue(new Tuple<string, string, string, string, string, List<string>, string>(correlationID, developerID, appID, serviceName ?? this.ServiceName ?? "APIGateway", objectName, logs, exception?.GetStack() ?? ""));
			}
		}

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, null, null, logger, logs, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, string developerID, string appID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, developerID, appID, logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, null, null, logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, string developerID, string appID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, developerID, appID, this.Logger, logs, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, null, null, this.Logger, logs, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, string developerID, string appID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, developerID, appID, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(string correlationID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(correlationID, null, null, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(RequestInfo requestInfo, List<string> logs, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, this.Logger, logs, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual Task WriteLogsAsync(RequestInfo requestInfo, string log, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, string developerID, string appID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, developerID, appID, logger, logs, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logger">The local logger</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, ILogger logger, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, null, null, logger, logs, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, string developerID, string appID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, developerID, appID, logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logger">The local logger</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, ILogger logger, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, null, null, logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, string developerID, string appID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, developerID, appID, this.Logger, logs, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, List<string> logs, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, null, null, this.Logger, logs, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="developerID">The identity of the developer</param>
		/// <param name="appID">The identity of the app</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, string developerID, string appID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, developerID, appID, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs into centerlized logging system
		/// </summary>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The error exception</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of object</param>
		/// <param name="mode">The logging mode</param>
		protected virtual void WriteLogs(string correlationID, string log, Exception exception = null, string serviceName = null, string objectName = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(correlationID, null, null, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, serviceName, objectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual void WriteLogs(RequestInfo requestInfo, List<string> logs, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, this.Logger, logs, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode)).ConfigureAwait(false);

		/// <summary>
		/// Writes the logs (to centerlized logging system and local logs)
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The logs</param>
		/// <param name="exception">The exception</param>
		/// <param name="mode">The logging mode</param>
		/// <returns></returns>
		protected virtual void WriteLogs(RequestInfo requestInfo, string log, Exception exception = null, LogLevel mode = LogLevel.Information)
			=> Task.Run(() => this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.Session?.DeveloperID, requestInfo.Session?.AppID, this.Logger, string.IsNullOrWhiteSpace(log) ? null : new List<string> { log }, exception, requestInfo.ServiceName, requestInfo.ObjectName, mode)).ConfigureAwait(false);
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
		protected virtual async Task<JToken> CallServiceAsync(
			RequestInfo requestInfo,
			CancellationToken cancellationToken = default,
			Action<RequestInfo> onStart = null,
			Action<RequestInfo, JToken> onSuccess = null,
			Action<RequestInfo, Exception> onError = null
		)
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
						$"Request: {requestInfo.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
						$"Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					, null, this.ServiceName, objectName).ConfigureAwait(false);

				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				try
				{
					var json = await Router.GetService(requestInfo.ServiceName).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
					onSuccess?.Invoke(requestInfo, json);

					if (this.IsDebugResultsEnabled)
						await this.WriteLogsAsync(requestInfo.CorrelationID, "Re-call service successful" + "\r\n" +
							$"Request: {requestInfo.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
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
		protected virtual async Task<List<Tuple<string, string, string, bool>>> GetSessionsAsync(RequestInfo requestInfo, string userID = null, CancellationToken cancellationToken = default)
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
		protected virtual string EncryptionKey => this.GetKey("Encryption", "VIEApps-59EF0859-NGX-BC1A-Services-4088-Encryption-9743-Key-51663AB720EF");

		/// <summary>
		/// Gets the key for validating data
		/// </summary>
		protected virtual string ValidationKey => this.GetKey("Validation", "VIEApps-D6C8C563-NGX-26CC-Services-43AC-Validation-9040-Key-E803AF0F36E4");

		/// <summary>
		/// Gets a key from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultKey"></param>
		/// <returns></returns>
		protected virtual string GetKey(string name, string defaultKey)
			=> UtilityService.GetAppSetting("Keys:" + name, defaultKey);

		/// <summary>
		/// Gets settings of an HTTP URI from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultURI"></param>
		/// <returns></returns>
		protected virtual string GetHttpURI(string name, string defaultURI)
			=> UtilityService.GetAppSetting($"HttpUri:{name}", defaultURI);

		/// <summary>
		/// Gets settings of a directory path from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultPath"></param>
		/// <returns></returns>
		protected virtual string GetPath(string name, string defaultPath = null)
			=> UtilityService.GetAppSetting($"Path:{name}", defaultPath);
		#endregion

		#region Authentication
		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="session">The session that contains user information</param>
		/// <returns></returns>
		protected virtual bool IsAuthenticated(Session session)
			=> session != null && session.User != null && session.User.IsAuthenticated;

		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		protected virtual bool IsAuthenticated(RequestInfo requestInfo)
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
		protected virtual async Task<IBusinessEntity> GetBusinessObjectAsync(string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsSystemAdministratorAsync(IUser user, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsSystemAdministratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsSystemAdministratorAsync(session?.User, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsSystemAdministratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> this.IsSystemAdministratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsAdministratorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAdministratorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsAdministratorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsServiceAdministratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsAdministratorAsync(session?.User, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsServiceAdministratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> this.IsServiceAdministratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is moderator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsModeratorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsModeratorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsModeratorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsServiceModeratorAsync(IUser user, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsModeratorAsync(user, null, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsServiceModeratorAsync(Session session, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsServiceModeratorAsync(session?.User, correlationID, cancellationToken);

		/// <summary>
		/// Gets the state that determines the user is service administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information and related service</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsServiceModeratorAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> this.IsServiceModeratorAsync(requestInfo?.Session, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user is editor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsEditorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsEditorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsEditorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is contributor or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsContributorAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsContributorAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsContributorAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is viewer or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsViewerAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsViewerAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
			=> this.IsViewerAsync(user, objectName, null, null, correlationID, cancellationToken);

		/// <summary>
		/// Determines the user is downloader or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsDownloaderAsync(IUser user, string objectName, string definitionID, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsDownloaderAsync(IUser user, string objectName = null, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsAuthorizedAsync(
			IUser user,
			string objectName,
			string objectID,
			Components.Security.Action action,
			Privileges privileges,
			Func<IUser, string, string, List<Privilege>> getPrivileges,
			Func<PrivilegeRole, List<Components.Security.Action>> getActions,
			string correlationID = null,
			CancellationToken cancellationToken = default
		)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(Session session, string objectName, string objectID, Components.Security.Action action, Privileges privileges, Func<IUser, string, string, List<Privilege>> getPrivileges, Func<PrivilegeRole, List<Components.Security.Action>> getActions, string correlationID = null, CancellationToken cancellationToken = default)
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
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, string objectName, string objectID, Components.Security.Action action, Privileges privileges, Func<IUser, string, string, List<Privilege>> getPrivileges, Func<PrivilegeRole, List<Components.Security.Action>> getActions, CancellationToken cancellationToken = default)
			=> this.IsAuthorizedAsync(requestInfo?.Session, objectName, objectID, action, privileges, getPrivileges, getActions, requestInfo?.CorrelationID, cancellationToken);

		/// <summary>
		/// Determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="action">The action to perform on the service's object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, string objectName, Components.Security.Action action, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(IUser user, string objectName, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default)
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
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(Session session, string objectName, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, string correlationID = null, CancellationToken cancellationToken = default)
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
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, IBusinessEntity @object, Components.Security.Action action, Func<IUser, string, string, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<Components.Security.Action>> getActions = null, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanManageAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanModerateAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanEditAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanContributeAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanViewAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public virtual async Task<bool> CanDownloadAsync(User user, string objectName, string systemID, string definitionID, string objectID, CancellationToken cancellationToken = default)
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
		public Task<JToken> GetThumbnailsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
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
		public Task<JToken> GetAttachmentsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
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
		public Task<JToken> GetFilesAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
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
		public Task<JToken> MarkFilesAsOfficialAsync(RequestInfo requestInfo, string systemID = null, string definitionID = null, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
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
		/// <param name="action">The action to run</param>
		/// <param name="interval">The elapsed time for running the action (seconds)</param>
		/// <param name="delay">Delay time (miliseconds) before running the action</param>
		/// <returns></returns>
		protected virtual IDisposable StartTimer(System.Action action, int interval, int delay = 0)
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
					this.WriteLogs(UtilityService.NewUUID, $"Error occurred while invoking a timer action => {ex.Message}", ex, this.ServiceName, "Timers");
				}
			});
			this.Timers.Add(timer);
			return timer;
		}

		/// <summary>
		/// Stops all timers
		/// </summary>
		protected virtual void StopTimers()
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
		protected virtual string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageNumber = 0) where T : class
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
		/// <param name="cache">The caching storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected virtual void ClearRelatedCache<T>(ICache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> cache?.Remove(this.GetRelatedCacheKeys(filter, sort));

		/// <summary>
		/// Clears the related data from the cache storage
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="cache">The caching storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected virtual Task ClearRelatedCacheAsync<T>(ICache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
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
				$"Request: {requestInfo.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
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
		protected virtual JToken GenerateFormControls<T>() where T : class
			=> RepositoryMediator.GenerateFormControls<T>();

		/// <summary>
		/// Generates the controls of this type (for working with view forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected virtual JToken GenerateViewControls<T>() where T : class
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
		protected virtual IDictionary<string, object> GetJsEmbedObjects(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null)
			=> Extensions.GetJsEmbedObjects(current, requestInfo, embedObjects);

		/// <summary>
		/// Gest the Javascript embed types
		/// </summary>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected virtual IDictionary<string, Type> GetJsEmbedTypes(IDictionary<string, Type> embedTypes = null)
			=> Extensions.GetJsEmbedTypes(embedTypes);

		/// <summary>
		/// Creates the Javascript engine for evaluating an expression
		/// </summary>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected virtual JavaScriptEngineSwitcher.Core.IJsEngine CreateJsEngine(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.CreateJsEngine(this.GetJsEmbedObjects(current, requestInfo, embedObjects), this.GetJsEmbedTypes(embedTypes));

		/// <summary>
		/// Gets the Javascript engine for evaluating an expression
		/// </summary>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfo' global variable</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		protected virtual JSPool.PooledJsEngine GetJsEngine(object current, RequestInfo requestInfo, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.GetJsEngine(this.GetJsEmbedObjects(current, requestInfo, embedObjects), this.GetJsEmbedTypes(embedTypes));

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="current">The object that presents information of current processing object - '__current' global variable and 'this' instance is bond to JSON stringify</param>
		/// <param name="requestInfo">The object that presents the information - '__requestInfoJSON' global variable</param>
		/// <returns></returns>
		protected virtual string GetJsExpression(string expression, object current, RequestInfo requestInfo)
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
		protected virtual object JsEvaluate(string expression, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
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
		protected virtual T JsEvaluate<T>(string expression, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
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
		protected virtual IEnumerable<object> JsEvaluate(IEnumerable<string> expressions, object current = null, RequestInfo requestInfo = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = this.GetJsEngine(current, requestInfo, embedObjects, embedTypes))
			{
				return expressions.Select(expression => jsEngine.JsEvaluate(this.GetJsExpression(expression, current, requestInfo))).ToList();
			}
		}
		#endregion

		#region Start the service
		/// <summary>
		/// Starts the service (the short way - connect to API Gateway and register the service)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="onRegisterSuccess">The action to run when the service was registered successful</param>
		/// <param name="onRegisterError">The action to run when got any error while registering the service</param>
		/// <param name="onIncomingConnectionEstablished">The action to run when the incomming connection is established</param>
		/// <param name="onOutgoingConnectionEstablished">The action to run when the outgoing connection is established</param>
		/// <param name="onIncomingConnectionBroken">The action to run when the incomming connection is broken</param>
		/// <param name="onOutgoingConnectionBroken">The action to run when the outgoing connection is broken</param>
		/// <param name="onIncomingConnectionError">The action to run when the incomming connection got any error</param>
		/// <param name="onOutgoingConnectionError">The action to run when the outgoing connection got any error</param>
		/// <returns></returns>
		protected virtual Task StartAsync(
			string[] args,
			Action<ServiceBase> onRegisterSuccess = null,
			Action<Exception> onRegisterError = null,
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null
		)
		{
			this.Logger?.LogInformation($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			return Router.ConnectAsync(
				async (sender, arguments) =>
				{
					// update session info
					Router.IncomingChannel.Update(arguments.SessionId, this.ServiceName, $"Incoming ({this.ServiceURI})");
					this.Logger?.LogInformation($"The incoming channel to API Gateway Router is established - Session ID: {arguments.SessionId}");
					if (this.State == ServiceState.Initializing)
						this.State = ServiceState.Ready;

					// register the service
					await this.RegisterServiceAsync(onRegisterSuccess, onRegisterError).ConfigureAwait(false);

					// handling the established event
					try
					{
						onIncomingConnectionEstablished?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionEstablished)}\" => {ex.Message}", ex);
					}
				},
				(sender, arguments) =>
				{
					// update state
					if (this.State == ServiceState.Connected)
						this.State = ServiceState.Disconnected;

					// re-connect
					if (Router.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
						this.Logger?.LogDebug($"The incoming channel to API Gateway Router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

					else if (Router.IncomingChannel != null)
					{
						this.Logger?.LogDebug($"The incoming channel to API Gateway Router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
						Router.IncomingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger?.LogDebug(msg, ex), "Incoming");
					}

					// handling the broken event
					try
					{
						onIncomingConnectionBroken?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionBroken)}\" => {ex.Message}", ex);
					}
				},
				(sender, arguments) =>
				{
					// handling the error event
					this.Logger?.LogError($"Got an unexpected error of the incoming channel to API Gateway Router => {arguments.Exception.Message}", arguments.Exception);
					try
					{
						onIncomingConnectionError?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onIncomingConnectionError)}\" => {ex.Message}", ex);
					}
				},
				async (sender, arguments) =>
				{
					// update session info
					Router.OutgoingChannel.Update(arguments.SessionId, this.ServiceName, $"Outgoing ({this.ServiceURI})");
					this.Logger?.LogInformation($"The outgoing channel to API Gateway Router is established - Session ID: {arguments.SessionId}");

					// initialize all helper services
					await this.InitializeHelperServicesAsync().ConfigureAwait(false);

					// send the service information to API Gateway
					try
					{
						await this.SendServiceInfoAsync(args, true).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while sending info to API Gateway => {ex.Message}", ex);
					}

					// handling the established event
					try
					{
						onOutgoingConnectionEstablished?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionEstablished)}\" => {ex.Message}", ex);
					}
				},
				(sender, arguments) =>
				{
					// re-connect
					if (Router.ChannelsAreClosedBySystem || arguments.CloseType.Equals(SessionCloseType.Goodbye))
						this.Logger?.LogDebug($"The outgoing channel to API Gateway Router is closed - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");

					else if (Router.OutgoingChannel != null)
					{
						this.Logger?.LogDebug($"The outgoing channel to API Gateway Router is broken - {arguments.CloseType} ({(string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason)})");
						Router.OutgoingChannel.ReOpen(this.CancellationTokenSource.Token, (msg, ex) => this.Logger?.LogDebug(msg, ex), "Outgoing");
					}

					// handling the broken event
					try
					{
						onOutgoingConnectionBroken?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionBroken)}\" => {ex.Message}", ex);
					}
				},
				(sender, arguments) =>
				{
					// handling the error event
					this.Logger?.LogError($"Got an unexpected error of the outgoing channel to API Gateway Router => {arguments.Exception.Message}", arguments.Exception);
					try
					{
						onOutgoingConnectionError?.Invoke(sender, arguments);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while invoking \"{nameof(onOutgoingConnectionError)}\" => {ex.Message}", ex);
					}
				},
				this.CancellationTokenSource.Token,
				exception => this.Logger?.LogError($"Error occurred while connecting to API Gateway Router => {exception.Message}", exception)
			);
		}

		/// <summary>
		/// Starts the service (the short way - connect to API Gateway and register the service)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="next">The next action to run when the service was started</param>
		public virtual Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
			=> this.StartAsync(args, _ =>
			{
				// show privileges
				if (this.IsDebugLogEnabled)
					this.Logger?.LogDebug($"Default working privileges\r\n{this.Privileges?.ToJson()}");

				// initialize repository
				if (initializeRepository)
					try
					{
						if (this.IsDebugLogEnabled)
							this.Logger?.LogDebug("Initializing the repository");

						RepositoryStarter.Initialize(
							new[] { this.GetType().Assembly }.Concat(this.GetType().Assembly.GetReferencedAssemblies()
								.Where(a => !a.Name.IsStartsWith("System") && !a.Name.IsStartsWith("mscorlib") && !a.Name.IsStartsWith("Microsoft") && !a.Name.IsEquals("NETStandard")
									&& !a.Name.IsStartsWith("Newtonsoft") && !a.Name.IsStartsWith("WampSharp") && !a.Name.IsStartsWith("Castle.") && !a.Name.IsStartsWith("StackExchange.")
									&& !a.Name.IsStartsWith("MongoDB") && !a.Name.IsStartsWith("MySql") && !a.Name.IsStartsWith("Oracle") && !a.Name.IsStartsWith("Npgsql") && !a.Name.IsStartsWith("Serilog")
									&& !a.Name.IsStartsWith("VIEApps.Components.") && !a.Name.IsStartsWith("VIEApps.Services.Abstractions") && !a.Name.IsStartsWith("VIEApps.Services.Base")
								)
								.Select(assemblyName =>
								{
									try
									{
										return Assembly.Load(assemblyName);
									}
									catch (Exception ex)
									{
										this.Logger?.LogError($"Error occurred while loading an assembly [{assemblyName.Name}] => {ex.Message}", ex);
										return null;
									}
								})
								.Where(assembly => assembly != null)
							),
							(msg, ex) =>
							{
								if (ex != null)
									this.Logger?.LogError(msg, ex);
								else if (!this.IsDebugLogEnabled)
									this.Logger?.LogDebug(msg);
							}
						);
					}
					catch (Exception ex)
					{
						this.Logger?.LogError($"Error occurred while initializing the repository => {ex.Message}", ex);
					}

				// run the next action
				try
				{
					next?.Invoke(this);
				}
				catch (Exception ex)
				{
					this.Logger?.LogError($"Error occurred while invoking the next action \"{nameof(next)}\" => {ex.Message}", ex);
				}
			});

		/// <summary>
		/// Starts the service (the short way - connect to API Gateway and register the service)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="next">The next action to run when the service was started</param>
		public virtual void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
			=> this.StartAsync(args, initializeRepository, next).Wait();
		#endregion

		#region Stop the service
		/// <summary>
		/// Gets the state that determines the service was stopped or not
		/// </summary>
		public bool Stopped { get; private set; } = false;

		/// <summary>
		/// Stops the service (unregister/disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="disconnect">true to disconnect from API Gateway Router and close all WAMP channels</param>
		/// <param name="next">The next action to run when the service was stopped</param>
		protected virtual async Task StopAsync(string[] args, bool available, bool disconnect, Action<IService> next = null)
		{
			// stop the service
			if (!this.Stopped)
			{
				// assign the flag and do unregister the services
				this.Stopped = true;
				await this.UnregisterServiceAsync(args, available).ConfigureAwait(false);

				// do the clean up tasks
				this.StopTimers();
				this.CancellationTokenSource.Cancel();

				// disconnect from API Gateway Router
				await (disconnect ? Router.DisconnectAsync() : Task.CompletedTask).ConfigureAwait(false);
				this.Logger?.LogDebug("The service was stopped");
			}

			// run the next action
			try
			{
				next?.Invoke(this);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while invoking the next action \"{nameof(next)}\" => {ex.Message}", ex);
			}
		}

		/// <summary>
		/// Stops the service (unregister/disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="next">The next action to run when the service was stopped</param>
		public virtual Task StopAsync(string[] args = null, Action<IService> next = null)
			=> this.StopAsync(args, true, true, next);

		/// <summary>
		/// Stops the service (unregister/disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="disconnect">true to disconnect from API Gateway Router and close all WAMP channels</param>
		/// <param name="next">The next action to run when the service was stopped</param>
		protected virtual void Stop(string[] args, bool available, bool disconnect, Action<IService> next = null)
			=> this.StopAsync(args, available, disconnect, next).Wait();

		/// <summary>
		/// Stops the service (unregister/disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="next">The next action to run when the service was stopped</param>
		public virtual void Stop(string[] args = null, Action<IService> next = null)
			=> this.StopAsync(args, next).Wait();
		#endregion

		#region Dispose the service
		/// <summary>
		/// Gets the state that determines the service was disposed or not
		/// </summary>
		public bool Disposed { get; private set; } = false;

		/// <summary>
		/// Disposes the service
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="disconnect">true to disconnect from API Gateway Router and close all WAMP channels</param>
		/// <param name="next">The next action to run when the service was disposed</param>
		public virtual ValueTask DisposeAsync(string[] args, bool available, bool disconnect = true, Action<IService> next = null)
			=> new ValueTask(this.Disposed ? Task.CompletedTask : this.StopAsync(args, available, disconnect, _ =>
			{
				// clean up
				this.Disposed = true;
				this.CancellationTokenSource.Dispose();
				this.Logger?.LogDebug("The service was disposed");
				GC.SuppressFinalize(this);

				// run the next action
				try
				{
					next?.Invoke(this);
				}
				catch (Exception ex)
				{
					this.Logger?.LogError($"Error occurred while invoking the next action \"{nameof(next)}\" => {ex.Message}", ex);
				}
			}));

		/// <summary>
		/// Disposes the service
		/// </summary>
		public virtual ValueTask DisposeAsync()
			=> this.DisposeAsync(null, true);

		/// <summary>
		/// Disposes the service
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="disconnect">true to disconnect from API Gateway Router and close all WAMP channels</param>
		/// <param name="next">The next action to run when the service was disposed</param>
		public virtual void Dispose(string[] args, bool available, bool disconnect = true, Action<IService> next = null)
			=> this.DisposeAsync(args, available, disconnect, next).AsTask().Wait();

		/// <summary>
		/// Disposes the service
		/// </summary>
		public virtual void Dispose()
			=> this.DisposeAsync().AsTask().Wait();

		~ServiceBase()
			=> this.Dispose();
		#endregion

	}
}