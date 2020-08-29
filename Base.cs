#region Related components
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Dynamic;
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
	public abstract class ServiceBase : IService, IUniqueService, ISyncableService, IServiceComponent
	{
		public abstract string ServiceName { get; }

		public abstract Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default);

		public virtual Task<JToken> SyncAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			// validate
			if (requestInfo.Extra == null || !requestInfo.Extra.TryGetValue("SyncKey", out var syncKey) || !this.SyncKey.Equals(syncKey))
				throw new InformationInvalidException("The sync key (in the request) is not found or invalid");

			if (string.IsNullOrWhiteSpace(requestInfo.Body))
				throw new InformationInvalidException("The request body (data to sync) is invalid");

			if (requestInfo.Extra == null || !requestInfo.Extra.TryGetValue("Signature", out var signature) || string.IsNullOrWhiteSpace(signature) || !signature.Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			// do the process
			return Task.FromResult<JToken>(null);
		}

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

		IAsyncDisposable ServiceSyncInstance { get; set; }

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
		protected CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();

		/// <summary>
		/// Gets the collection of timers
		/// </summary>
		protected List<IDisposable> Timers { get; private set; } = new List<IDisposable>();

		/// <summary>
		/// Gets the JSON formatting mode
		/// </summary>
		protected Formatting JsonFormat => this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None;

		/// <summary>
		/// Gets the state of the service
		/// </summary>
		protected ServiceState State { get; private set; } = ServiceState.Initializing;

		/// <summary>
		/// The identity of the synchronizing session
		/// </summary>
		protected string SyncSessionID { get; private set; }

		public string NodeID { get; private set; }

		public string ServiceURI => $"services.{(this.ServiceName ?? "unknown").Trim().ToLower()}";

		public string ServiceUniqueName => $"{(this.ServiceName ?? "unknown").Trim().ToLower()}.{this.NodeID}";

		public string ServiceUniqueURI => $"services.{this.ServiceUniqueName}";

		/// <summary>
		/// Gets or sets the single instance of current playing service component
		/// </summary>
		public static ServiceBase ServiceComponent { get; set; }
		#endregion

		#region Register/Unregister the service
		/// <summary>
		/// Registers the service with API Gateway
		/// </summary>
		/// <param name="args">The arguments for registering</param>
		/// <param name="onSuccess">The action to run when the service was registered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		public virtual async Task RegisterServiceAsync(IEnumerable<string> args, Action<IService> onSuccess = null, Action<Exception> onError = null)
		{
			async Task registerCalleesAsync()
			{
				this.ServiceInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(this.ServiceName)).ConfigureAwait(false);
				this.ServiceUniqueInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => this, RegistrationInterceptor.Create(this.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
				this.ServiceSyncInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<ISyncableService>(() => this, RegistrationInterceptor.Create(this.ServiceName)).ConfigureAwait(false);
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
						async message => await (this.NodeID.IsEquals(message.ExcludedNodeID) ? Task.CompletedTask : this.ProcessInterCommunicateMessageAsync(message)).ConfigureAwait(false),
						exception => this.Logger?.LogError($"Error occurred while fetching an inter-communicate message => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.GatewayCommunicator?.Dispose();
				this.GatewayCommunicator = Router.IncomingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>("messages.services.apigateway")
					.Subscribe(
						async message => await (this.NodeID.IsEquals(message.ExcludedNodeID) ? Task.CompletedTask : this.ProcessGatewayCommunicateMessageAsync(message)).ConfigureAwait(false),
						exception => this.Logger?.LogError($"Error occurred while fetching an inter-communicate message of API Gateway => {exception.Message}", this.State == ServiceState.Connected ? exception : null)
					);

				this.Logger?.LogDebug($"The inter-communicate message updater was{(this.State == ServiceState.Disconnected ? " re-" : " ")}subscribed successful");
			}

			this.NodeID = this.NodeID ?? Extensions.GetNodeID(args);

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
		/// <param name="args">The arguments for unregistering</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="onSuccess">The action to run when the service was unregistered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		public virtual async Task UnregisterServiceAsync(IEnumerable<string> args, bool available = true, Action<IService> onSuccess = null, Action<Exception> onError = null)
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
				await Task.WhenAll
				(
					this.ServiceInstance != null ? this.ServiceInstance.DisposeAsync().AsTask() : Task.CompletedTask,
					this.ServiceUniqueInstance != null ? this.ServiceUniqueInstance.DisposeAsync().AsTask() : Task.CompletedTask,
					this.ServiceSyncInstance != null ? this.ServiceSyncInstance.DisposeAsync().AsTask() : Task.CompletedTask
				).ConfigureAwait(false);
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
				this.ServiceSyncInstance = null;
				onSuccess?.Invoke(this);
			}
		}

		/// <summary>
		/// Initializes the helper services from API Gateway
		/// </summary>
		/// <param name="onSuccess">The action to run when the service was registered successful</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		protected virtual async Task InitializeHelperServicesAsync(Action<IService> onSuccess = null, Action<Exception> onError = null)
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
		protected virtual Task SendServiceInfoAsync(IEnumerable<string> args, bool running, bool available = true)
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
		/// Sends a web-hook message
		/// </summary>
		/// <param name="message">The well-formed message to send</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendWebHookAsync(WebHookMessage message, CancellationToken cancellationToken = default)
			=> this.MessagingService.SendWebHookAsync(message, cancellationToken);

		/// <summary>
		/// Sends a web-hook message
		/// </summary>
		/// <param name="message">The message to send</param>
		/// <param name="developerID">The identity of developer</param>
		/// <param name="appID">The identity of app</param>
		/// <param name="signAlgorithm">The HMAC algorithm to sign the body with the specified key (md5, sha1, sha256, sha384, sha512, ripemd/ripemd160, blake128, blake/blake256, blake384, blake512)</param>
		/// <param name="signKey">The key that use to sign</param>
		/// <param name="signatureName">The name of the signature parameter, default is combination of algorithm and the string 'Signature', ex: HmacSha256Signature</param>
		/// <param name="signatureAsHex">true to use signature as hex, false to use as Base64</param>
		/// <param name="signatureInQuery">true to place the signature in query string, false to place in header, default is false</param>
		/// <param name="additionalQuery">The additional query string</param>
		/// <param name="additionalHeader">The additional header</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task SendWebHookAsync(WebHookMessage message, string developerID, string appID, string signAlgorithm = "SHA256", string signKey = null, string signatureName = null, bool signatureAsHex = true, bool signatureInQuery = false, Dictionary<string, string> additionalQuery = null, Dictionary<string, string> additionalHeader = null, CancellationToken cancellationToken = default)
			=> this.MessagingService.SendWebHookAsync(message?.Normalize(signAlgorithm, signKey ?? appID, signatureName, signatureAsHex, signatureInQuery, additionalQuery, new Dictionary<string, string>(additionalHeader ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
			{
				{ "DeveloperID", developerID },
				{ "AppID", appID }
			}), cancellationToken);
		#endregion

		#region Loggings
		ILogger IServiceComponent.Logger
		{
			get => this.Logger;
			set => this.Logger = value;
		}

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
				logs.Add($"> Type: {exception.GetType()}");
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
		protected Action<string, Exception> GetTracker(RequestInfo requestInfo)
		{
			var objectName = this.ServiceName.IsEquals(requestInfo.ServiceName) ? "" : requestInfo.ServiceName;
			void tracker(string log, Exception exception)
			{
				if (this.IsDebugResultsEnabled)
					this.WriteLogs(requestInfo.CorrelationID, log, exception, this.ServiceName, objectName);
			}
			return tracker;
		}

		/// <summary>
		/// Calls a business service
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onStart">The action to run when start</param>
		/// <param name="onSuccess">The action to run when success</param>
		/// <param name="onError">The action to run when got an error</param>
		/// <returns>A <see cref="JObject">JSON</see> object that presents the results of the business service</returns>
		protected virtual async Task<JToken> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default, Action<RequestInfo> onStart = null, Action<RequestInfo, JToken> onSuccess = null, Action<RequestInfo, Exception> onError = null)
			=> await requestInfo.CallServiceAsync(cancellationToken, onStart, onSuccess, onError, this.GetTracker(requestInfo), this.JsonFormat).ConfigureAwait(false);
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
		/// Gets the key for synchronizing data
		/// </summary>
		protected virtual string SyncKey => this.GetKey("Sync", "VIEApps-FD2CD7FA-NGX-40DE-Services-401D-Sync-93D9-Key-A47006F07048");

		/// <summary>
		/// Gets the key for validating/signing a JSON Web Token
		/// </summary>
		/// <returns></returns>
		protected virtual string JWTKey => this.ValidationKey.GetHMACHash(this.EncryptionKey, "BLAKE256").ToBase64Url();

		/// <summary>
		/// Gets the key for encrypting/decrypting data with ECCsecp256k1
		/// </summary>
		protected virtual System.Numerics.BigInteger ECCKey => ECCsecp256k1.GetPrivateKey(this.GetKey("Keys:ECC", "MD9g3THNC0Z1Ulk+5eGpijotaR5gtv/mzMzfMa5Oio3gOCCSbpCZe5SBIsvdzyof3rFVFgBxOXBM0QgyhBgaCSVkUGaLko5YAmX8qJ6ThORAwrOJNGqNx08y3l0b+A3jkWdvqVVnu6oS7QfnAPaOp4QjMC0Uxpl/2E3QpsI+vNZ9HkWx4mTJeW1AegNmmvov+KhzgWXt8HuT6Vys/MWGxoWPq+ooDGPAfmeVZiY+8GyY4zgMisdqUObEejaAj+gQd+nnnpI8YOFimjir8fp5eP/rT1t6urYcHNUGjsHvPZUAC7uczE3M3ZIhPXz4iT5MDBtonUGsTnrKZKh/NGGvaC/DAhptFIsnjOlLbAyiXmY=").Base64ToBytes().Decrypt());

		/// <summary>
		/// Gets the key for encrypting/decrypting data with ECCsecp256k1
		/// </summary>
		protected virtual ECCsecp256k1.Point ECCPublicKey => ECCsecp256k1.GeneratePublicKey(this.ECCKey);

		/// <summary>
		/// Gets the key for encrypting/decrypting data with RSA
		/// </summary>
		protected virtual string RSAKey => this.GetKey("Keys:RSA", "DA90WJt+jHmBfNlAS31qY3OS+3iUfwN7Gg+bKUm5RxqV13y7eh4daubWAHqtbrPS/Qw5F3d3D26yEo5FZroGvhyFGpfqJqeoz9EhsByn8hZZwns09qtITU6Wbqi74mQe9/h7Xp/57sJUDKssiTFKZYC+OS9RFytJDFXZF8zVoMDQmdG8f7lD6t16bIk27+KwX3OzdSoPOtNalSAwWxZVKchL23NXbHR6EAhnqouLWGHXTOBLIuOnJdqFE8IzgwuffFJ53iq47K7ILC2mAm3DEyv+j24VBYE/EcB8GBLGVlo4uv3tNaDIw9isTlxyETtZwR+NbV7JXOl3j/wKjCL2U/nsfPzQhAMC58+0oKeda2fCV4cXtg/EyrQSpjn56S04BybThgJjoYF1Vf1FqmaNLB9GaV73PLQKUPLY3qFws7k6og5A08eNsgUVfcZqO1iqVUJDbJHCuPgygnRMSsamGS8oWBtSb/rDto+jdpx2oC/KhNA2zMkhYiIO7DtK7sdwo0XeDjid7aipP+bsIuAGmRmt1RgklF65DGcvbglEPSziopUH2hfvbKhtxD+9gp4RrO7KZPrcFKaP8YOKAh05bAvNKwH6Bou3TKPXSjxzalAJqdHzjZNOLmNsfgS2+Y0J9BJhrGMTZtKqjtkbM2qYLkD8DONGdmUmud0TYjBLQVwesScjXxZsYyyohnU+vzqVD6AOxkc9FcU2RMEnSrCu7HAKTTo930v3p4S1iQrKDXn0zrIvDuX5m0LzeUJcV1WJUsu+n6lQCwDKWYZkNpGnJfodl2TtCjt82etcZMyU13Tpoo1M7oyFqlKjcUmy3hzmqfTqbG2AM348VTg9O3jgJxe9kBu5/Gf5tJXvNKaG3sXIh5Ym8pJ08tpE2DS3v3hlPCOD8YsqouW4FzBMmBgNykY5XjtgYZgDHPxCSlIQSuu19Iv6fXk5lDWjJ1Lx3RqRiXbRk7Xj6wlwu/WlomRRzwyO9fL5W89Gj1BaeYVGK+tBnGs9DFVBIIqlrpDyMOVRhkFayZ5J96r+guuZqmHiq+e4JYIC7aYHMT78n8F8DbWbV7hcnyLTe+e5zFQ4WmuBcPlP3ne4YT+Rs/G2NWvdHKmMDOj91CfyuCCgIFSA2/N8gmElrwt3t2yofkhC2tbJEwLCbErupxC/ttjQkjnqEy84me1mR3rkjRNrhbWer3OLAFNwaVMpX6XkcDuGn7evG9Km73Sv8f7y3G2jH9pj5D67T6iLywiyL0s/4Hs+m+VdRRDagWc9P/I+D9ub9tdD8zYTe89UVHzBGpAA3rA7xlowSZNpN2RQC/j0x2J32uy7sSBOh4U8OcJaAJCZjGZjobrhOr6jQJgNpzs8Zx9L/zTGHRDHb0DI6WOAG++KYkcNYqPS1/aewNE8wSMMaZVRkV4Lp7zx4jj3G6+hj80ZOtpRVto7sVoTH34wbzhz0M+NpunGN/ozvmumGeHqZVSQCwnOSnZjiDg+NJU24nmAwv0m0Bc2fY57M50M14gdfBa0ezuCyElMdySr6Kt1ftFtR5NHl/jHjzD+PPq5Bgzgu8uK06iJtRwOvG4K5RrVcIpoj1absbc+Lh22Ri887iLTxZf7uQyau13FXUbpk2eAwKy1oi5RVYT8MTiijSFhct8xCFj359WYSWq5On7onMn39cWPFEFOKxw48aWu/pyLFjRdZgFxlNvEUgBIie/kI+bj3vlBAaTD+3MWFnCrkLcd1flp4nuyQj0iL2xX8pE49FlSNhkkcF2eHF48JaHrNbpnoFLlUKPg98225M0LR2Qxz/rz9uH7P+YEkrQgcO1fYnRbuFx2o5BJ5PdB45B9GmmpdIZJlP2gagxiWqDdotASjD3pfr17S8jL02bko9oBpmf1Eh5lQYyjYDnNjHmYv3nLRcCd8BKxyksAfqv8lOhpvLsKnwHhFVG2yefKOdmC/M3SGwxDabUI7Xv0kA8+COvGq6AC+sLXHydfPN901UjcvRJwNk85yTJO94zwLUUFgVFQNJtEVbarpPsDGYcAeuyF+ccN74HlVvdi8h9WyT1en39hWO8elhTrEZTDB/1ZNfi9Q6iTJYHrLCqw8vaABdBpN4bEm/XEV2gQE923YuItiPAznDCEl0En5VzYQSOT+mENq6XZTVdu1peSFvmexDoNwreK0waGtCYgmbxMnhXq").Decrypt();

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
		/// Gets a business object
		/// </summary>
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<RepositoryBase> GetBusinessObjectAsync(string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> string.IsNullOrWhiteSpace(entityInfo) || string.IsNullOrWhiteSpace(objectID)
				? Task.FromResult<RepositoryBase>(null)
				: RepositoryMediator.GetAsync(entityInfo, objectID, cancellationToken);

		/// <summary>
		/// Gets a business object
		/// </summary>
		/// <param name="definition">The entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual Task<RepositoryBase> GetBusinessObjectAsync(EntityDefinition definition, string objectID, CancellationToken cancellationToken = default)
			=> definition  == null || string.IsNullOrWhiteSpace(objectID)
				? Task.FromResult<RepositoryBase>(null)
				: RepositoryMediator.GetAsync(definition, objectID, cancellationToken);
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
					var @is = "Users".IsEquals(this.ServiceName) && user.IsSystemAdministrator;
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsAdministratorAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
			if (!@is && user != null)
			{
				@is = user.IsAdministrator(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsAdministrator(privileges);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is administrator of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
					$"User: {user?.ID ?? "N/A"}" + "\r\n\t" + $"- Roles: {user?.Roles?.ToString(", ")}" + "\r\n\t" + $"- Privileges: {(user?.Privileges == null || user.Privileges.Count < 1 ? "None" : user.Privileges.ToJArray().ToString())}" + "\r\n" +
					$"Privileges for determining: {privileges?.ToJson().ToString() ?? "None"}"
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsModeratorAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsModerator(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsModerator(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is moderator of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsEditorAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsEditor(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsEditor(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is editor of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsContributorAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsContributor(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsContributor(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is contributor of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsViewerAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsViewer(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsViewer(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is viewer of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
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
		/// <param name="entityInfo">The identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition</param>
		/// <param name="objectID">The identity of the object</param>
		/// <param name="correlationID">The identity for tracking the correlation</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsDownloaderAsync(IUser user, string objectName, string entityInfo, string objectID, string correlationID = null, CancellationToken cancellationToken = default)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			Privileges privileges = null;
			var @is = false;
			if (user != null)
			{
				@is = user.IsDownloader(this.ServiceName, objectName);
				if (!@is)
				{
					privileges = (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges ?? this.Privileges;
					@is = user.IsDownloader(privileges) || await this.IsSystemAdministratorAsync(user, correlationID, cancellationToken).ConfigureAwait(false);
				}
			}

			if (this.IsDebugAuthorizationsEnabled)
				this.WriteLogs(correlationID, $"Determines the user is downloader of service/object => {@is}" + "\r\n" +
					$"Object: {objectName ?? "N/A"}{(string.IsNullOrWhiteSpace(objectID) ? "" : $"#{objectID} (Entity: {entityInfo ?? "N/A"})")}" + "\r\n" +
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
					$"Privileges for determining: {(privileges ?? this.Privileges)?.ToJson().ToString() ?? "None"}"
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

		public virtual async Task<bool> CanManageAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsAdministratorAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Full, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		public virtual async Task<bool> CanModerateAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsModeratorAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Approve, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		public virtual async Task<bool> CanEditAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsEditorAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Update, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		public virtual async Task<bool> CanContributeAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsContributorAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Create, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		public virtual async Task<bool> CanViewAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsViewerAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.View, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);

		public virtual async Task<bool> CanDownloadAsync(User user, string objectName, string systemID, string entityInfo, string objectID, CancellationToken cancellationToken = default)
			=> await this.IsDownloaderAsync(user, objectName, entityInfo, objectID, null, cancellationToken).ConfigureAwait(false) || await this.IsAuthorizedAsync(user, objectName, objectID, Components.Security.Action.Download, (await this.GetBusinessObjectAsync(entityInfo, objectID, cancellationToken).ConfigureAwait(false))?.WorkingPrivileges, null, null, null, cancellationToken).ConfigureAwait(false);
		#endregion

		#region Files (Thumbnails & Attachments) & Form Controls
		/// <summary>
		/// Gets the collection of thumbnails
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetThumbnailsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
			=> requestInfo == null
				? Task.FromResult<JToken>(null)
				: requestInfo.GetThumbnailsAsync(objectID, objectTitle, this.ValidationKey, cancellationToken, this.GetTracker(requestInfo), this.JsonFormat);

		/// <summary>
		/// Gets the collection of attachments
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetAttachmentsAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
			=> requestInfo == null
				? Task.FromResult<JToken>(null)
				: requestInfo.GetAttachmentsAsync(objectID, objectTitle, this.ValidationKey, cancellationToken, this.GetTracker(requestInfo), this.JsonFormat);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> GetFilesAsync(RequestInfo requestInfo, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
			=> requestInfo == null
				? Task.FromResult<JToken>(null)
				: requestInfo.GetFilesAsync(objectID, objectTitle, this.ValidationKey, cancellationToken, this.GetTracker(requestInfo), this.JsonFormat);

		/// <summary>
		/// Gets the collection of files (thumbnails and attachment files are included) as official
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemID"></param>
		/// <param name="entityInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="objectTitle"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> MarkFilesAsOfficialAsync(RequestInfo requestInfo, string systemID = null, string entityInfo = null, string objectID = null, string objectTitle = null, CancellationToken cancellationToken = default)
			=> requestInfo == null
				? Task.FromResult<JToken>(null)
				: requestInfo.MarkFilesAsOfficialAsync(systemID, entityInfo, objectID, objectTitle, this.ValidationKey, cancellationToken, this.GetTracker(requestInfo), this.JsonFormat);

		/// <summary>
		/// Deletes the collection of files (thumbnails and attachment files are included)
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <param name="systemID"></param>
		/// <param name="entityInfo"></param>
		/// <param name="objectID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public Task<JToken> DeleteFilesAsync(RequestInfo requestInfo, string systemID = null, string entityInfo = null, string objectID = null, CancellationToken cancellationToken = default)
			=> requestInfo == null
				? Task.FromResult<JToken>(null)
				: requestInfo.DeleteFilesAsync(systemID, entityInfo, objectID, this.ValidationKey, cancellationToken, this.GetTracker(requestInfo), this.JsonFormat);

		/// <summary>
		/// Generates the controls of this type (for working with input forms)
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <returns></returns>
		protected virtual JToken GenerateFormControls<T>() where T : class
			=> RepositoryMediator.GenerateFormControls<T>();
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

		#region Caching keys
		/// <summary>
		/// Gets the caching key for working with collection of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The page size</param>
		/// <param name="pageNumber">The page number</param>
		/// <returns>The string that presents a caching key</returns>
		protected virtual string GetCacheKey<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKey(filter, sort, pageSize, pageNumber);

		/// <summary>
		/// Gets the related caching key for working with collection of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageSize">The size of one page</param>
		/// <returns>The collection presents all related caching keys (100 pages each size is 20 objects)</returns>
		protected virtual List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0) where T : class
			=> Extensions.GetRelatedCacheKeys(filter, sort, pageSize);

		/// <summary>
		/// Gets the caching key for workingwith the number of total objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <returns>The string that presents a caching key</returns>
		protected virtual string GetCacheKeyOfTotalObjects<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
			=> Extensions.GetCacheKeyOfTotalObjects(filter, sort);

		/// <summary>
		/// Gets the caching key for working with the JSON of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		protected virtual string GetCacheKeyOfObjectsJson<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKeyOfObjectsJson(filter, sort, pageSize, pageNumber);

		/// <summary>
		/// Gets the caching key for working with the XML of objects
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="filter">The filter expression</param>
		/// <param name="sort">The sort expression</param>
		/// <param name="pageNumber">The page number</param>
		/// <param name="pageSize">The page size</param>
		/// <returns>The string that presents a caching key</returns>
		protected virtual string GetCacheKeyOfObjectsXml<T>(IFilterBy<T> filter, SortBy<T> sort, int pageSize = 0, int pageNumber = 0) where T : class
			=> Extensions.GetCacheKeyOfObjectsXml(filter, sort, pageSize, pageNumber);
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
				$"Request: {requestInfo.ToString(this.JsonFormat)}"
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

		#region Sync
		/// <summary>
		/// Builds the RequestInfo to send a synchronize request
		/// </summary>
		/// <param name="sessionID"></param>
		/// <returns></returns>
		protected RequestInfo BuildSyncRequestInfo(string sessionID = null)
		{
			this.SyncSessionID = sessionID ?? this.SyncSessionID ?? UtilityService.NewUUID;
			var ipAddresses = new List<IPAddress>();
			try
			{
				ipAddresses = Dns.GetHostAddresses(Dns.GetHostName()).ToList();
			}
			catch { }
			return new RequestInfo
			{
				Session = new Session
				{
					SessionID = this.SyncSessionID,
					User = new User
					{
						SessionID = this.SyncSessionID,
						ID = UtilityService.GetAppSetting("Users:SystemAccountID", "VIEAppsNGX-MMXVII-System-Account")
					},
					IP = ipAddresses.FirstOrDefault()?.ToString() ?? "127.0.0.1",
					DeviceID = $"{this.NodeID}@synchronizer",
					AppName = "VIEApps NGX Synchronizer",
					AppPlatform = $"{Extensions.GetRuntimeOS()} Daemon",
					AppAgent = $"{UtilityService.DesktopUserAgent} VIEApps NGX Sync Daemon/{this.GetType().Assembly.GetVersion(false)}",
					AppOrigin = null,
					Verified = true
				},
				Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "SyncKey", this.SyncKey }
				},
				CorrelationID = UtilityService.NewUUID
			};
		}

		/// <summary>
		/// Registers the session to send a synchronize request
		/// </summary>
		/// <param name="requestInfo">The RequestInfo object that contains the session information need to register</param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task RegisterSyncSessionAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var body = new JObject
			{
				{ "ID", requestInfo.Session.SessionID },
				{ "IssuedAt", DateTime.Now },
				{ "RenewedAt", DateTime.Now },
				{ "ExpiredAt", DateTime.Now.AddDays(1) },
				{ "UserID", requestInfo.Session.User.ID },
				{ "AccessToken", requestInfo.Session.User.GetAccessToken(this.ECCKey) },
				{ "IP", requestInfo.Session.IP },
				{ "DeviceID", requestInfo.Session.DeviceID },
				{ "DeveloperID", requestInfo.Session.DeveloperID },
				{ "AppID", requestInfo.Session.AppID },
				{ "AppInfo", $"{requestInfo.Session.AppName} @ {requestInfo.Session.AppPlatform}" },
				{ "OSInfo", Extensions.GetRuntimePlatform(false) },
				{ "Verified", requestInfo.Session.Verified },
				{ "Online", true }
			}.ToString(Formatting.None);
			await this.CallServiceAsync(new RequestInfo(requestInfo.Session, "Users", "Session", "POST")
			{
				Body = body,
				Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "Signature", body.GetHMACSHA256(this.ValidationKey) }
				},
				CorrelationID = requestInfo.CorrelationID
			}, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends the sync request to a remote endpoint
		/// </summary>
		/// <param name="requestInfo">The RequestInfo object that contains the session information</param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected virtual Task SendSyncRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		/// <summary>
		/// Sends the sync request to a remote API Gateway using REST APIs with PATCH verb
		/// </summary>
		/// <param name="requestInfo">The RequestInfo object that contains the session information</param>
		/// <param name="apiGatewayURI">The URI of the remote API Gateway</param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected virtual async Task SendSyncRequestAsync(RequestInfo requestInfo, string apiGatewayURI, CancellationToken cancellationToken = default)
		{
			// prepare URL
			var url = apiGatewayURI;
			if (string.IsNullOrWhiteSpace(url) || (!url.IsStartsWith("https://") && !url.IsStartsWith("http://")))
				return;

			while (url.EndsWith("/"))
				url = url.Left(url.Length - 1);

			url += $"/{requestInfo.ServiceName}/{requestInfo.ObjectName}?{requestInfo.Query.Select(kvp => $"{kvp.Key}={kvp.Value?.UrlEncode()}").Join("&")}";
			url += (url.IsContains("&") ? "&" : "") + $"x-request-extra={requestInfo.Extra?.ToJson().ToString(Formatting.None).Url64Encode()}";

			// send the request as HTTP request
			try
			{
				await UtilityService.GetWebResponseAsync("PATCH", url, requestInfo.Header, null, requestInfo.Body, "application/json", 45, requestInfo.Session.AppAgent, null, null, null, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await this.WriteLogsAsync(requestInfo, $"Error occurred while sending a synchoronizing request to a remote API Gateway [{apiGatewayURI}] => {ex.Message}", ex).ConfigureAwait(false);
				throw;
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
			Action<IService> onRegisterSuccess = null,
			Action<Exception> onRegisterError = null,
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null
		)
		{
			this.NodeID = Extensions.GetNodeID(args);
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
					await this.RegisterServiceAsync(args, onRegisterSuccess, onRegisterError).ConfigureAwait(false);

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

					// start the timer to send the sync request
					if (string.IsNullOrWhiteSpace(this.SyncSessionID))
						this.StartTimer(async () =>
						{
							try
							{
								var requestInfo = this.BuildSyncRequestInfo(UtilityService.NewUUID);
								await this.RegisterSyncSessionAsync(requestInfo, this.CancellationTokenSource.Token).ConfigureAwait(false);
								await this.SendSyncRequestAsync(requestInfo, this.CancellationTokenSource.Token).ConfigureAwait(false);
							}
							catch (Exception ex)
							{
								this.Logger?.LogError($"Error occurred while sending a sync request to API Gateway => {ex.Message}", ex);
							}
						}, UtilityService.GetAppSetting("TimerInterval:Sync", "7").CastAs<int>() * 60);

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
								.Where(a => !a.Name.IsStartsWith("System") && !a.Name.IsStartsWith("Microsoft") && !a.Name.IsStartsWith("mscorlib") && !a.Name.IsEquals("NETStandard")
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
								else if (this.IsDebugLogEnabled)
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
					this.Logger?.LogError($"Error occurred while invoking the next action when start the service => {ex.Message}", ex);
				}
			});

		public virtual void Start(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
			=> this.StartAsync(args, initializeRepository, next).Wait();
		#endregion

		#region Stop the service
		/// <summary>
		/// Gets the state that determines the service was stopped or not
		/// </summary>
		public bool Stopped { get; private set; } = false;

		/// <summary>
		/// Stops the service (unregister the service, disconnect from API Gateway and do the clean-up tasks)
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

				// clean up
				try
				{
					this.StopTimers();
					if (!this.Disposed)
						this.CancellationTokenSource.Cancel();
				}
				catch (Exception ex)
				{
					this.Logger?.LogDebug($"Error occurred while cleaning up the service => {ex.Message}", ex);
				}

				// disconnect from API Gateway Router
				try
				{
					await (disconnect ? Router.DisconnectAsync() : Task.CompletedTask).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					this.Logger?.LogDebug($"Error occurred while disconnecting the service => {ex.Message}", ex);
				}
				finally
				{
					this.Logger?.LogDebug("The service was stopped");
				}
			}

			// run the next action
			try
			{
				next?.Invoke(this);
			}
			catch (Exception ex)
			{
				this.Logger?.LogError($"Error occurred while invoking the next action when stop the service => {ex.Message}", ex);
			}
		}

		public virtual Task StopAsync(string[] args = null, Action<IService> next = null)
			=> this.StopAsync(args, true, true, next);

		/// <summary>
		/// Stops the service (unregister the service, disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		/// <param name="args">The arguments</param>
		/// <param name="available">true to mark the service still available</param>
		/// <param name="disconnect">true to disconnect from API Gateway Router and close all WAMP channels</param>
		/// <param name="next">The next action to run when the service was stopped</param>
		protected virtual void Stop(string[] args, bool available, bool disconnect, Action<IService> next = null)
			=> this.StopAsync(args, available, disconnect, next).Wait();

		public virtual void Stop(string[] args = null, Action<IService> next = null)
			=> this.StopAsync(args, next).Wait();
		#endregion

		#region Dispose the service
		/// <summary>
		/// Gets the state that determines the service was disposed or not
		/// </summary>
		public bool Disposed { get; private set; } = false;

		public virtual ValueTask DisposeAsync(string[] args, bool available = true, bool disconnect = true, Action<IService> next = null)
			=> new ValueTask(this.Disposed ? Task.CompletedTask : this.StopAsync(args, available, disconnect, _ =>
			{
				// clean up
				GC.SuppressFinalize(this);
				this.Disposed = true;
				try
				{
					this.CancellationTokenSource.Dispose();
				}
				catch (Exception ex)
				{
					this.Logger?.LogDebug($"Error occurred while disposing the service => {ex.Message}", ex);
				}
				finally
				{
					this.Logger?.LogDebug("The service was disposed");
				}

				// run the next action
				try
				{
					next?.Invoke(this);
				}
				catch (Exception ex)
				{
					this.Logger?.LogError($"Error occurred while invoking the next action when dispose the service => {ex.Message}", ex);
				}
			}));

		/// <summary>
		/// Disposes the service (unregister the service, disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		public virtual ValueTask DisposeAsync()
			=> this.DisposeAsync(null);

		public virtual void Dispose(string[] args, bool available = true, bool disconnect = true, Action<IService> next = null)
			=> this.DisposeAsync(args, available, disconnect, next).AsTask().Wait();

		/// <summary>
		/// Disposes the service (unregister the service, disconnect from API Gateway and do the clean-up tasks)
		/// </summary>
		public virtual void Dispose()
			=> this.Dispose(null);

		~ServiceBase()
			=> this.Dispose();
		#endregion

	}
}