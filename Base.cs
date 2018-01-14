#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Reactive.Linq;

using WampSharp.V2;
using WampSharp.V2.Rpc;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Client;
using WampSharp.V2.Realm;
using WampSharp.Core.Listener;

using Newtonsoft.Json.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

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
	public abstract class ServiceBase : IService, IServiceComponent, IDisposable
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
		public abstract Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Process the inter-communicate message
		/// </summary>
		/// <param name="message"></param>
		protected virtual void ProcessInterCommunicateMessage(CommunicateMessage message) { }

		#region Attributes & Properties
		IWampChannel _incommingChannel = null, _outgoingChannel = null;
		long _incommingChannelSessionID = 0, _outgoingChannelSessionID = 0;
		bool _channelsAreClosedBySystem = false;

		SystemEx.IAsyncDisposable _instance = null;
		IDisposable _communicator = null;
		IRTUService _rtuService = null;
		ILoggingService _loggingService = null;
		IMessagingService _messagingService = null;
		ConcurrentDictionary<string, IService> _businessServices = new ConcurrentDictionary<string, IService>(StringComparer.OrdinalIgnoreCase);

		internal protected CancellationTokenSource CancellationTokenSource { get; private set; } = new CancellationTokenSource();
		internal protected List<IDisposable> Timers { get; private set; } = new List<IDisposable>();

		ILogger _logger;

		/// <summary>
		/// Gets the logger
		/// </summary>
		public ILogger Logger
		{
			get
			{
				return this._logger
					?? (this._logger = new ServiceCollection()
						.AddLogging(builder =>
						{
#if DEBUG
							builder.SetMinimumLevel(LogLevel.Debug);
#else
							builder.SetMinimumLevel(LogLevel.Information);
#endif
							builder.AddConsole();
						})
						.BuildServiceProvider()
						.GetService<ILoggerFactory>()
						.CreateLogger(this.GetType()));
			}
		}

		/// <summary>
		/// Gets the full URI
		/// </summary>
		public string ServiceURI
		{
			get
			{
				return "net.vieapps.services." + (this.ServiceName ?? "unknown").Trim().ToLower();
			}
		}

		/// <summary>
		/// Gets or sets the value indicating weather current service component is running under user interactive mode or not
		/// </summary>
		public bool IsUserInteractive { get; set; } = false;
		#endregion

		#region Open/Close channels
		/// <summary>
		/// Gets the information of WAMP router from configuration file
		/// </summary>
		/// <returns></returns>
		protected virtual Tuple<string, string, bool> GetRouterInfo()
		{
			var address = UtilityService.GetAppSetting("Router:Address", "ws://127.0.0.1:16429/");
			var realm = UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm");
			var mode = UtilityService.GetAppSetting("Router:ChannelsMode", "MsgPack");
			return new Tuple<string, string, bool>(address, realm, mode.IsEquals("json"));
		}

		/// <summary>
		/// Opens the incoming channel
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		protected async Task OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (this._incommingChannel != null)
				return;

			var info = this.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			this._incommingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			this._incommingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) =>
			{
				this._incommingChannelSessionID = args.SessionId;
			};

			if (onConnectionEstablished != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);
			else
				this._incommingChannel.RealmProxy.Monitor.ConnectionBroken += (sender, args) =>
				{
					if (!this._channelsAreClosedBySystem && !args.CloseType.Equals(SessionCloseType.Disconnection))
						this.ReOpenIncomingChannel(
							123,
							(cn) => this.WriteLog(UtilityService.NewUID, this.ServiceName, null, "Re-connect the incomming connection successful"),
							(ex) => this.WriteLog(UtilityService.NewUID, this.ServiceName, null, "Cannot re-connect the incomming connection", ex)
						);
				};

			if (onConnectionError != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._incommingChannel.Open().ConfigureAwait(false);
		}

		/// <summary>
		/// Closes the incoming channels
		/// </summary>
		protected void CloseIncomingChannel()
		{
			if (this._incommingChannel != null)
			{
				this._incommingChannel.Close($"The incoming channel is closed when stop the service [{this.ServiceURI}]", new GoodbyeDetails());
				this._incommingChannel = null;
			}
		}

		/// <summary>
		/// Reopens the incoming channel
		/// </summary>
		/// <param name="delay"></param>
		/// <param name="onSuccess"></param>
		/// <param name="onError"></param>
		protected void ReOpenIncomingChannel(int delay = 0, Action<IWampChannel> onSuccess = null, Action<Exception> onError = null)
		{
			if (this._incommingChannel != null)
				new WampChannelReconnector(this._incommingChannel, async () =>
				{
					try
					{
						await Task.Delay(delay > 0 ? delay : 0).ConfigureAwait(false);
						await this._incommingChannel.Open().ConfigureAwait(false);
						onSuccess?.Invoke(this._incommingChannel);
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				}).Start();
		}

		/// <summary>
		/// Opens the outgoing channel
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		protected async Task OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (this._outgoingChannel != null)
				return;

			var info = this.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			this._outgoingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			this._outgoingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) =>
			{
				this._outgoingChannelSessionID = args.SessionId;
			};

			if (onConnectionEstablished != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);
			else
				this._outgoingChannel.RealmProxy.Monitor.ConnectionBroken += (sender, args) =>
				{
					if (!this._channelsAreClosedBySystem && !args.CloseType.Equals(SessionCloseType.Disconnection))
						this.ReOpenOutgoingChannel(
							234,
							(cn) => this.WriteLog(UtilityService.NewUID, this.ServiceName, null, "Re-connect the outgoing connection successful"),
							(ex) => this.WriteLog(UtilityService.NewUID, this.ServiceName, null, "Cannot re-connect the outgoing connection", ex)
						);
				};

			if (onConnectionError != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._outgoingChannel.Open().ConfigureAwait(false);
		}

		/// <summary>
		/// Close the outgoing channel
		/// </summary>
		protected void CloseOutgoingChannel()
		{
			if (this._outgoingChannel != null)
			{
				this._outgoingChannel.Close($"The outgoing channel is closed when stop the service [{this.ServiceURI}]", new GoodbyeDetails());
				this._outgoingChannel = null;
			}
		}

		/// <summary>
		/// Reopens the outgoing channel
		/// </summary>
		/// <param name="delay"></param>
		/// <param name="onSuccess"></param>
		/// <param name="onError"></param>
		protected void ReOpenOutgoingChannel(int delay = 0, Action<IWampChannel> onSuccess = null, Action<Exception> onError = null)
		{
			if (this._outgoingChannel != null)
				new WampChannelReconnector(this._outgoingChannel, async () =>
				{
					try
					{
						await Task.Delay(delay > 0 ? delay : 0).ConfigureAwait(false);
						await this._outgoingChannel.Open().ConfigureAwait(false);
						onSuccess?.Invoke(this._outgoingChannel);
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				}).Start();
		}
		#endregion

		#region Register the service
		/// <summary>
		/// Registers the service
		/// </summary>
		/// <param name="onSuccess"></param>
		/// <param name="onError"></param>
		/// <returns></returns>
		protected async Task RegisterServiceAsync(Action<ServiceBase> onSuccess = null, Action<Exception> onError = null)
		{
			await this.OpenIncomingChannelAsync().ConfigureAwait(false);
			try
			{
				// register the service
				var name = this.ServiceName.Trim().ToLower();
				this._instance = await this._incommingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, RegistrationInterceptor.Create(name)).ConfigureAwait(false);

				// register the handler of inter-communicate messages
				this._communicator?.Dispose();
				this._communicator = this._incommingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>($"net.vieapps.rtu.communicate.messages.{name}")
					.Subscribe(
						message => this.ProcessInterCommunicateMessage(message),
						exception => this.WriteLog(UtilityService.NewUID, "APIGateway", "RTU", $"Error occurred while fetching inter-communicate message of a service [{this.ServiceName}]", exception)
					);

				// callback when done
				onSuccess?.Invoke(this);
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
		}
		#endregion

		#region Send update messages & notifications
		async Task InitializeRTUServiceAsync()
		{
			if (this._rtuService == null)
			{
				await this.OpenOutgoingChannelAsync().ConfigureAwait(false);
				this._rtuService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>(ProxyInterceptor.Create());
			}
		}

		/// <summary>
		/// Sends a message for updating data of client
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync().ConfigureAwait(false);
			await this._rtuService.SendUpdateMessageAsync(message, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends updating messages to client
		/// </summary>
		/// <param name="messages">The messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendUpdateMessagesAsync(List<UpdateMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync().ConfigureAwait(false);
			await messages.ForEachAsync((message, token) => this._rtuService.SendUpdateMessageAsync(message, token), cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Sends updating messages to client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync().ConfigureAwait(false);
			await this._rtuService.SendUpdateMessagesAsync(messages, deviceID, excludedDeviceID, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync().ConfigureAwait(false);
			await this._rtuService.SendInterCommunicateMessageAsync(serviceName, message, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync().ConfigureAwait(false);
			await this._rtuService.SendInterCommunicateMessagesAsync(serviceName, messages, cancellationToken).ConfigureAwait(false);
		}
		#endregion

		#region Send email & web hook messages
		async Task InitializeMessagingServiceAsync()
		{
			if (this._messagingService == null)
			{
				await this.OpenOutgoingChannelAsync().ConfigureAwait(false);
				this._messagingService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IMessagingService>(ProxyInterceptor.Create());
			}
		}

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="message">The email message for sending</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await this.InitializeMessagingServiceAsync().ConfigureAwait(false);
				await this._messagingService.SendEmailAsync(message, cancellationToken).ConfigureAwait(false);
			}
			catch { }
		}

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
		{
			return this.SendEmailAsync(new EmailMessage()
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
		}

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
		{
			return this.SendEmailAsync(from, null, to, null, null, subject, body, smtpServer, smtpServerPort, smtpServerEnableSsl, smtpUsername, smtpPassword, cancellationToken);
		}

		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="to"></param>
		/// <param name="subject"></param>
		/// <param name="body"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.SendEmailAsync(null, to, subject, body, null, 0, false, null, null, cancellationToken);
		}

		/// <summary>
		/// Sends a web hook message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task SendWebHookAsync(WebHookMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await this.InitializeMessagingServiceAsync().ConfigureAwait(false);
				await this._messagingService.SendWebHookAsync(message, cancellationToken).ConfigureAwait(false);
			}
			catch { }
		}
		#endregion

		#region Working with logs (multiple)
		async Task InitializeLoggingServiceAsync()
		{
			if (this._loggingService == null)
			{
				await this.OpenOutgoingChannelAsync().ConfigureAwait(false);
				this._loggingService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<ILoggingService>(ProxyInterceptor.Create());
			}
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="stack">The stack (usually is Exception.StackTrace)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string stack, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await this.InitializeLoggingServiceAsync().ConfigureAwait(false);
				await this._loggingService.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack, cancellationToken).ConfigureAwait(false);
			}
			catch { }
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="stack">The stack (usually is Exception.StackTrace)</param>
		/// <returns></returns>
		protected void WriteLogs(string correlationID, string serviceName, string objectName, List<string> logs, string stack)
		{
			Task.Run(async () =>
			{
				await this.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stack = "";
			if (exception != null)
			{
				logs = logs ?? new List<string>();
				logs.Add($"> Message: {exception.Message}");
				logs.Add($"> Type: {exception.GetType().ToString()}");
				stack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					stack += "\r\n" + $"--- Inner ({counter}): ---------------------- " + "\r\n"
						+ "> Message: " + inner.Message + "\r\n"
						+ "> Type: " + inner.GetType().ToString() + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
				}
			}

			return this.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack, cancellationToken);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLogs(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogsAsync(correlationID, serviceName, objectName, logs, exception, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes a log message to the terminator or the standard output stream
		/// </summary>
		/// <param name="correlationID">The string that presents correlation identity</param>
		/// <param name="logs">The log messages</param>
		/// <param name="exception">The exception</param>
		/// <param name="updateCentralizedLogs">true to update the log message into centralized logs of the API Gateway</param>
		public virtual async Task WriteLogsAsync(string correlationID, List<string> logs, Exception exception = null, bool updateCentralizedLogs = true)
		{
			// update the log message into centralized logs of the API Gateway
			if (updateCentralizedLogs)
				await this.WriteLogsAsync(correlationID ?? UtilityService.NewUID, this.ServiceName, null, logs, exception, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// write to the terminator or the standard output stream
			if (this.IsUserInteractive)
			{
				if (exception == null)
					logs?.ForEach(log => this.Logger.LogInformation(log));
				else
				{
					logs?.ForEach(log => this.Logger.LogError(log));
					this.Logger.LogError(exception, exception.Message);
				}
			}
		}

		/// <summary>
		/// Writes a log message to the terminator or the standard output stream
		/// </summary>
		/// <param name="correlationID">The string that presents correlation identity</param>
		/// <param name="logs">The log messages</param>
		/// <param name="exception">The exception</param>
		/// <param name="updateCentralizedLogs">true to update the log message into centralized logs of the API Gateway</param>
		public virtual void WriteLogs(string correlationID, List<string> logs, Exception exception = null, bool updateCentralizedLogs = true)
		{
			Task.Run(async () =>
			{
				await this.WriteLogsAsync(correlationID, logs, exception, updateCentralizedLogs).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(RequestInfo requestInfo, List<string> logs, Exception exception = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, logs, exception, cancellationToken);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLogs(RequestInfo requestInfo, List<string> logs, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogsAsync(requestInfo, logs, exception, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}
		#endregion

		#region Working with logs (single)
		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="stack">The stack (usually is Exception.StackTrace)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string stack, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.WriteLogsAsync(correlationID, serviceName, objectName, !string.IsNullOrWhiteSpace(log) ? new List<string>() { log } : null, stack, cancellationToken);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="stack">The stack (usually is Exception.StackTrace)</param>
		/// <returns></returns>
		protected void WriteLog(string correlationID, string serviceName, string objectName, string log, string stack)
		{
			Task.Run(async () =>
			{
				await this.WriteLogAsync(correlationID, serviceName, objectName, log, stack, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, Exception exception = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.WriteLogsAsync(correlationID, serviceName, objectName, new List<string>() { string.IsNullOrWhiteSpace(log) ? exception?.Message ?? "" : log }, exception, cancellationToken);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		protected void WriteLog(string correlationID, string serviceName, string objectName, string log, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogAsync(correlationID, serviceName, objectName, log, exception, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes a log message to the terminator or the standard output stream
		/// </summary>
		/// <param name="correlationID">The string that presents correlation identity</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		/// <param name="updateCentralizedLogs">true to update the log message into centralized logs of the API Gateway</param>
		public virtual Task WriteLogAsync(string correlationID, string log, Exception exception = null, bool updateCentralizedLogs = true)
		{
			return this.WriteLogsAsync(correlationID, new List<string>() { string.IsNullOrWhiteSpace(log) ? exception?.Message ?? "" : log }, exception, updateCentralizedLogs);
		}

		/// <summary>
		/// Writes a log message to the terminator or the standard output stream
		/// </summary>
		/// <param name="correlationID">The string that presents correlation identity</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		/// <param name="updateCentralizedLogs">true to update the log message into centralized logs of the API Gateway</param>
		public virtual void WriteLog(string correlationID, string log, Exception exception = null, bool updateCentralizedLogs = true)
		{
			Task.Run(async () =>
			{
				await this.WriteLogAsync(correlationID, log, exception, updateCentralizedLogs).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogAsync(RequestInfo requestInfo, string log, Exception exception = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return this.WriteLogAsync(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, log, exception, cancellationToken);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLog(RequestInfo requestInfo, string log, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogAsync(requestInfo, log, exception, this.CancellationTokenSource.Token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		}
		#endregion

		#region Call services
		/// <summary>
		/// Gets a service by name
		/// </summary>
		/// <param name="name">The string that presents name of a service</param>
		/// <returns></returns>
		protected async Task<IService> GetServiceAsync(string name)
		{
			IService service = null;
			if (!string.IsNullOrWhiteSpace(name) && !this._businessServices.TryGetValue(name, out service))
			{
				await this.OpenOutgoingChannelAsync().ConfigureAwait(false);
				lock (this._businessServices)
				{
					if (!this._businessServices.TryGetValue(name, out service))
					{
						service = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
						this._businessServices.TryAdd(name, service);
					}
				}
			}
			return service;
		}

		/// <summary>
		/// Calls a service to process a request
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<JObject> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			return await (await this.GetServiceAsync(
				requestInfo != null && !string.IsNullOrWhiteSpace(requestInfo.ServiceName)
					? requestInfo.ServiceName
					: "unknown"
				).ConfigureAwait(false)).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
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
			var result = await this.CallServiceAsync(new RequestInfo()
			{
				Session = requestInfo.Session,
				ServiceName = "users",
				ObjectName = "account",
				Verb = "HEAD",
				Query = new Dictionary<string, string>()
				{
					{ "object-identity", userID ?? requestInfo.Session.User.ID }
				},
				CorrelationID = requestInfo.CorrelationID
			}, cancellationToken).ConfigureAwait(false);

			return (result["Sessions"] as JArray).ToList(info =>
				new Tuple<string, string, string, bool>(
					(info["SessionID"] as JValue).Value as string,
					(info["DeviceID"] as JValue).Value as string,
					(info["AppInfo"] as JValue).Value as string,
					(info["IsOnline"] as JValue).Value.CastAs<bool>()
				)
			);
		}
		#endregion

		#region Keys & HTTP URIs
		/// <summary>
		/// Gets a key from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultKey"></param>
		/// <returns></returns>
		protected string GetKey(string name, string defaultKey)
		{
			return UtilityService.GetAppSetting("Keys:" + name, defaultKey);
		}

		/// <summary>
		/// Gets the key for encrypting/decrypting data with AES
		/// </summary>
		protected string EncryptionKey
		{
			get
			{
				return this.GetKey("Encryption", "VIEApps-59EF0859-NGX-BC1A-Services-4088-Encryption-9743-Key-51663AB720EF");
			}
		}

		/// <summary>
		/// Gets the key for validating data
		/// </summary>
		protected string ValidationKey
		{
			get
			{
				return this.GetKey("Validation", "VIEApps-D6C8C563-NGX-26CC-Services-43AC-Validation-9040-Key-E803AF0F36E4");
			}
		}

		/// <summary>
		/// Gets a HTTP URI from app settings
		/// </summary>
		/// <param name="name"></param>
		/// <param name="defaultURI"></param>
		/// <returns></returns>
		protected string GetHttpURI(string name, string defaultURI)
		{
			return UtilityService.GetAppSetting("HttpUri:" + name, defaultURI);
		}
		#endregion

		#region Authentication & Authorization
		/// <summary>
		/// Gets the state that determines the user is authenticated or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		protected bool IsAuthenticated(RequestInfo requestInfo)
		{
			return requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null && requestInfo.Session.User.IsAuthenticated;
		}

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="user">The user information</param>
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public async Task<bool> IsSystemAdministratorAsync(User user, string correlationID = null)
		{
			if (user == null || !user.IsAuthenticated)
				return false;

			else
				try
				{
					var result = await this.CallServiceAsync(new RequestInfo()
					{
						Session = new Session() { User = user },
						ServiceName = "users",
						ObjectName = "account",
						Verb = "GET",
						Extra = new Dictionary<string, string>()
						{
							{ "IsSystemAdministrator", "" }
						},
						CorrelationID = correlationID ?? UtilityService.NewUID
					}).ConfigureAwait(false);
					return user.ID.IsEquals((result["ID"] as JValue)?.Value as string) && (result["IsSystemAdministrator"] as JValue)?.Value.CastAs<bool>() == true;
				}
				catch
				{
					return false;
				}
		}

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="session">The session information</param>
		/// /// <param name="correlationID">The correlation identity</param>
		/// <returns></returns>
		public Task<bool> IsSystemAdministratorAsync(Session session, string correlationID = null)
		{
			return this.IsSystemAdministratorAsync(session?.User, correlationID);
		}

		/// <summary>
		/// Gets the state that determines the user is system administrator or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		public Task<bool> IsSystemAdministratorAsync(RequestInfo requestInfo)
		{
			return this.IsSystemAdministratorAsync(requestInfo?.Session?.User, requestInfo?.CorrelationID);
		}

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
		protected virtual async Task<bool> IsAuthorizedAsync(User user, string serviceName, string objectName, string objectIdentity, Components.Security.Action action, Privileges privileges = null, Func<User, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			return await this.IsSystemAdministratorAsync(user).ConfigureAwait(false)
				? true
				: user != null
					? user.IsAuthorized(serviceName, objectName, objectIdentity, action, privileges, getPrivileges, getActions)
					: false;
		}

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected virtual Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<User, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			return this.IsAuthorizedAsync(requestInfo.Session?.User, requestInfo.ServiceName, requestInfo.ObjectName, requestInfo.GetObjectIdentity(true), action, privileges, getPrivileges, getActions);
		}

		/// <summary>
		/// The the global privilege role of the user in this service
		/// </summary>
		/// <param name="user"></param>
		/// <returns></returns>
		protected virtual string GetPrivilegeRole(User user)
		{
			var privilege = user != null && user.Privileges != null
				? user.Privileges.FirstOrDefault(p => p.ServiceName.IsEquals(this.ServiceName) && string.IsNullOrWhiteSpace(p.ObjectName) && string.IsNullOrWhiteSpace(p.ObjectIdentity))
				: null;
			return privilege?.Role ?? PrivilegeRole.Viewer.ToString();
		}

		/// <summary>
		/// Gets the default privileges  of the user in this service
		/// </summary>
		/// <param name="user"></param>
		/// <param name="privileges"></param>
		/// <returns></returns>
		protected virtual List<Privilege> GetPrivileges(User user, Privileges privileges)
		{
			return null;
		}

		/// <summary>
		/// Gets the default privilege actions in this service
		/// </summary>
		/// <param name="role"></param>
		/// <returns></returns>
		protected virtual List<string> GetPrivilegeActions(PrivilegeRole role)
		{
			var actions = new List<Components.Security.Action>();
			switch (role)
			{
				case PrivilegeRole.Administrator:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Full
					};
					break;

				case PrivilegeRole.Moderator:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Approve,
						Components.Security.Action.Update,
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				case PrivilegeRole.Editor:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Update,
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				case PrivilegeRole.Contributor:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.Create,
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;

				default:
					actions = new List<Components.Security.Action>()
					{
						Components.Security.Action.View,
						Components.Security.Action.Download
					};
					break;
			}
			return actions.Select(a => a.ToString()).ToList();
		}

		/// <summary>
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="entity">The business entity object</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected virtual async Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, IBusinessEntity entity, Components.Security.Action action, Func<User, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			return await this.IsSystemAdministratorAsync(requestInfo).ConfigureAwait(false)
				? true
				: requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null
					? requestInfo.Session.User.IsAuthorized(requestInfo.ServiceName, requestInfo.ObjectName, entity?.ID, action, entity?.WorkingPrivileges, getPrivileges, getActions)
					: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanManageAsync(User user, string objectName, string objectIdentity)
		{
			return await this.IsSystemAdministratorAsync(user).ConfigureAwait(false)
				|| (user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.Full, null, this.GetPrivileges, this.GetPrivilegeActions));
		}

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanManageAsync(User user, string systemID, string definitionID, string objectID)
		{
			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// system administrator can do anything
			if (await this.IsSystemAdministratorAsync(user).ConfigureAwait(false))
				return true;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Full, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanModerateAsync(User user, string objectName, string objectIdentity)
		{
			return await this.CanManageAsync(user, objectName, objectIdentity).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.Approve, null, this.GetPrivileges, this.GetPrivilegeActions);
		}

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanModerateAsync(User user, string systemID, string definitionID, string objectID)
		{
			// administrator can do
			if (await this.CanManageAsync(user, systemID, definitionID, objectID).ConfigureAwait(false))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Approve, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanEditAsync(User user, string objectName, string objectIdentity)
		{
			return await this.CanModerateAsync(user, objectName, objectIdentity).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.Update, null, this.GetPrivileges, this.GetPrivilegeActions);
		}

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanEditAsync(User user, string systemID, string definitionID, string objectID)
		{
			// moderator can do
			if (await this.CanModerateAsync(user, systemID, definitionID, objectID).ConfigureAwait(false))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Update, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanContributeAsync(User user, string objectName, string objectIdentity)
		{
			return await this.CanEditAsync(user, objectName, objectIdentity).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.Create, null, this.GetPrivileges, this.GetPrivilegeActions);
		}

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanContributeAsync(User user, string systemID, string definitionID, string objectID)
		{
			// editor can do
			if (await this.CanEditAsync(user, systemID, definitionID, objectID).ConfigureAwait(false))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Create, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanViewAsync(User user, string objectName, string objectIdentity)
		{
			return await this.CanContributeAsync(user, objectName, objectIdentity).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.View, null, this.GetPrivileges, this.GetPrivilegeActions);
		}

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanViewAsync(User user, string systemID, string definitionID, string objectID)
		{
			// contributor can do
			if (await this.CanContributeAsync(user, systemID, definitionID, objectID).ConfigureAwait(false))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.View, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanDownloadAsync(User user, string objectName, string objectIdentity)
		{
			return await this.CanModerateAsync(user, objectName, objectIdentity).ConfigureAwait(false)
				? true
				: user != null && user.IsAuthorized(this.ServiceName, objectName, objectIdentity, Components.Security.Action.Download, null, this.GetPrivileges, this.GetPrivilegeActions);
		}

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		public virtual async Task<bool> CanDownloadAsync(User user, string systemID, string definitionID, string objectID)
		{
			// moderator can do
			if (await this.CanModerateAsync(user, systemID, definitionID, objectID).ConfigureAwait(false))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID, this.CancellationTokenSource.Token).ConfigureAwait(false);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Download, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
		}
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
			var timer = Observable.Timer(TimeSpan.FromMilliseconds(delay > 0 ? delay : interval * 1000), TimeSpan.FromSeconds(interval)).Subscribe(_ => action?.Invoke());
			this.Timers.Add(timer);
			return timer;
		}

		/// <summary>
		/// Stops all timers
		/// </summary>
		protected void StopTimers()
		{
			this.Timers.ForEach(timer => timer.Dispose());
		}
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
		{
			return typeof(T).GetTypeName(true) + "#"
				+ (filter != null ? filter.GetMD5() + ":" : "")
				+ (sort != null ? sort.GetMD5() + ":" : "")
				+ (pageNumber > 0 ? pageNumber.ToString() : "");
		}

		List<string> GetRelatedCacheKeys<T>(IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			var key = this.GetCacheKey<T>(filter, sort);
			var keys = new List<string>() { key, $"{key}-json", $"{key}-total" };
			for (var index = 1; index <= 100; index++)
			{
				keys.Add($"{key}:{index}");
				keys.Add($"{key}:{index}-json");
				keys.Add($"{key}:{index}-total");
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
		{
			cache?.Remove(this.GetRelatedCacheKeys(filter, sort));
		}

		/// <summary>
		/// Clears the related data from the cache storage
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="cache">The cache storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected Task ClearRelatedCacheAsync<T>(Cache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			return cache != null
				? cache.RemoveAsync(this.GetRelatedCacheKeys(filter, sort))
				: Task.CompletedTask;
		}
		#endregion

		#region Get runtime exception
		/// <summary>
		/// Gets the runtime exception to throw to caller
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="message">The message</param>
		/// <param name="exception">The exception</param>
		/// <param name="writeLogs">true to write into centralized logs</param>
		/// <returns></returns>
		public WampException GetRuntimeException(RequestInfo requestInfo, string message, Exception exception, bool writeLogs = true)
		{
			message = string.IsNullOrWhiteSpace(message)
				? exception != null
					? exception.Message
					: $"Error occurred while processing with the service [net.vieapps.services.{requestInfo.ServiceName.ToLower().Trim()}]"
				: message;

			if (writeLogs)
				this.WriteLog(requestInfo, message, exception);

			if (exception is WampException)
				return exception as WampException;

			else
			{
				var details = exception != null
					? new Dictionary<string, object>() { { "0", exception.StackTrace } }
					: null;

				var inner = exception != null
					? exception.InnerException
					: null;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					details.Add(counter.ToString(), inner.StackTrace);
					inner = inner.InnerException;
				}

				return new WampRpcRuntimeException(details, new Dictionary<string, object>(), new Dictionary<string, object>() { { "RequestInfo", requestInfo.ToJson() } }, message, exception);
			}
		}

		/// <summary>
		/// Gets the runtime exception to throw to caller
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="exception">The exception</param>
		/// <param name="writeLogs">true to write into centralized logs</param>
		/// <returns></returns>
		public WampException GetRuntimeException(RequestInfo requestInfo, Exception exception, bool writeLogs = true)
		{
			return this.GetRuntimeException(requestInfo, null, exception, writeLogs);
		}
		#endregion

		#region Start & Stop
		/// <summary>
		/// Starts the service
		/// </summary>
		/// <param name="args">The starting arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="next">The next action to run</param>
		public virtual void Start(string[] args = null, bool initializeRepository = true, Func<IService, Task> next = null)
		{
			// prepare
			var correlationID = UtilityService.NewUID;

			// start the service
			Task.Run(async () =>
			{
				try
				{
					await this.StartAsync(
						service => this.WriteLog(correlationID, $"The service is started & registered - PID: {Process.GetCurrentProcess().Id}"),
						exception => this.WriteLog(correlationID, "Error occurred while starting the service", exception)
					).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await this.WriteLogAsync(correlationID, "Error occurred while starting the service", ex).ConfigureAwait(false);
					if (ex is ArgumentException && ex.Message.IsContains("Value does not fall within the expected range"))
					{
						await this.WriteLogAsync(correlationID, "Got a problem while connecting to WAMP router. Try to re-connect after few times...").ConfigureAwait(false);
						await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);

						this._incommingChannel?.Close();
						this._incommingChannel = null;
						this._outgoingChannel?.Close();
						this._outgoingChannel = null;

						this._loggingService = null;
						this._rtuService = null;
						this._messagingService = null;

						await this.StartAsync(
							service => this.WriteLog(correlationID, $"The service is re-registered - PID: {Process.GetCurrentProcess().Id}"),
							exception => this.WriteLog(correlationID, "Error occurred while re-starting the service", exception)
						).ConfigureAwait(false);
					}
				}
			})

			// continue
			.ContinueWith(async (task) =>
			{
				// initialize repository
				if (initializeRepository)
					try
					{
						await this.WriteLogAsync(correlationID, "Initializing the repository").ConfigureAwait(false);
						RepositoryStarter.Initialize(
							new List<Assembly>() { this.GetType().Assembly }.Concat(this.GetType().Assembly.GetReferencedAssemblies()
								.Where(n => !n.Name.IsStartsWith("mscorlib") && !n.Name.IsStartsWith("System") && !n.Name.IsStartsWith("Microsoft") && !n.Name.IsEquals("NETStandard")
									&& !n.Name.IsStartsWith("Newtonsoft") && !n.Name.IsStartsWith("WampSharp") && !n.Name.IsStartsWith("Castle.") && !n.Name.IsStartsWith("StackExchange.")
									&& !n.Name.IsStartsWith("MongoDB") && !n.Name.IsStartsWith("MySql") && !n.Name.IsStartsWith("Oracle") && !n.Name.IsStartsWith("Npgsql")
									&& !n.Name.IsStartsWith("VIEApps.Components.") && !n.Name.IsStartsWith("VIEApps.Services.Base") && !n.Name.IsStartsWith("VIEApps.Services.APIGateway"))
								.Select(n => Assembly.Load(n))
							),
							(log, ex) =>
							{
								this.WriteLog(correlationID, log, ex);
							}
						);
					}
					catch (Exception ex)
					{
						await this.WriteLogAsync(correlationID, "Error occurred while initializing the repository", ex).ConfigureAwait(false);
					}

				// run the next action
				if (next != null)
					try
					{
						await next(this).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						await this.WriteLogAsync(correlationID, "Error occurred while running the next action", ex).ConfigureAwait(false);
					}
			})
			.ConfigureAwait(false);
		}

		/// <summary>
		/// Starts the service (the short way - open channels and register service)
		/// </summary>
		/// <param name="onRegisterSuccess"></param>
		/// <param name="onRegisterError"></param>
		/// <param name="onIncomingConnectionEstablished"></param>
		/// <param name="onOutgoingConnectionEstablished"></param>
		/// <param name="onIncomingConnectionBroken"></param>
		/// <param name="onOutgoingConnectionBroken"></param>
		/// <param name="onIncomingConnectionError"></param>
		/// <param name="onOutgoingConnectionError"></param>
		/// <returns></returns>
		protected virtual async Task StartAsync(Action<ServiceBase> onRegisterSuccess = null, Action<Exception> onRegisterError = null, Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null, Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null, Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null, Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null)
		{
			await this.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError).ConfigureAwait(false);
			await this.RegisterServiceAsync(onRegisterSuccess, onRegisterError).ConfigureAwait(false);
			await this.OpenOutgoingChannelAsync(onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError).ConfigureAwait(false);
			await Task.WhenAll(
				this.InitializeLoggingServiceAsync(),
				this.InitializeRTUServiceAsync(),
				this.InitializeMessagingServiceAsync()
			).ConfigureAwait(false);
		}

		/// <summary>
		/// Stops this service (close channels and clean-up)
		/// </summary>
		public void Stop()
		{
			this.CancellationTokenSource.Cancel();
			this.CancellationTokenSource.Dispose();

			this.StopTimers();
			this._communicator?.Dispose();

			Task.WaitAll(new Task[]
			{
				Task.Run(async () =>
				{
					if (this._instance != null)
					{
						try
						{
							await this._instance.DisposeAsync().ConfigureAwait(false);
						}
						catch { }
						this._instance = null;
					}
				})
				.ContinueWith(task =>
				{
					this._channelsAreClosedBySystem = true;
					this.CloseIncomingChannel();
					this.CloseOutgoingChannel();
				})
			}, TimeSpan.FromSeconds(13));
		}

		bool _isDisposed = false;

		public virtual void Dispose()
		{
			if (!this._isDisposed)
			{
				this._isDisposed = true;
				this.Stop();
				GC.SuppressFinalize(this);
			}
		}

		protected ServiceBase() { }

		~ServiceBase()
		{
			this.Dispose();
		}
		#endregion

	}
}