#region Related components
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Diagnostics;

using WampSharp.V2;
using WampSharp.V2.Rpc;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Client;
using WampSharp.V2.Realm;
using WampSharp.Core.Listener;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents an abstract service (base of all services)
	/// </summary>
	public abstract class ServiceBase : IService, IServiceComponent, IDisposable
	{
		/// <summary>
		/// Gets the name of this service (for working with related URIs)
		/// </summary>
		public abstract string ServiceName { get; }

		/// <summary>
		/// Process the request of this service
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
		System.Action _onIncomingChannelClosing = null, _onOutgoingChannelClosing = null;

		SystemEx.IAsyncDisposable _instance = null;
		IDisposable _communicator = null;
		IRTUService _rtuService = null;
		IManagementService _managementService = null;
		IMessagingService _messagingService = null;
		Dictionary<string, IService> _businessServices = new Dictionary<string, IService>();

		/// <summary>
		/// Gets the full URI of the service
		/// </summary>
		public string ServiceURI
		{
			get
			{
				return "net.vieapps.services." + (this.ServiceName?.ToLower() ?? "unknown").Trim();
			}
		}
		#endregion

		#region Open/Close channels
		/// <summary>
		/// Gets the information of WAMP router from configuration file
		/// </summary>
		/// <returns></returns>
		protected virtual Tuple<string, string, bool> GetRouterInfo()
		{
			var address = UtilityService.GetAppSetting("RouterAddress", "ws://127.0.0.1:26429/");
			var realm = UtilityService.GetAppSetting("RouterRealm", "VIEAppsRealm");
			var mode = UtilityService.GetAppSetting("RouterChannelsMode", "MsgPack");
			return new Tuple<string, string, bool>(address, realm, mode.IsEquals("json"));
		}

		/// <summary>
		/// Opens the incoming channel
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <param name="onClosingConnection"></param>
		/// <returns></returns>
		protected async Task OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null, System.Action onClosingConnection = null)
		{
			if (this._incommingChannel != null)
				return;

			var info = this.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			this._incommingChannel = useJsonChannel
				? (new DefaultWampChannelFactory()).CreateJsonChannel(address, realm)
				: (new DefaultWampChannelFactory()).CreateMsgpackChannel(address, realm);

			this._incommingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, arguments) =>
			{
				this._incommingChannelSessionID = arguments.SessionId;
			};

			if (onConnectionEstablished != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._incommingChannel.Open();

			this._onIncomingChannelClosing = onClosingConnection;
		}

		/// <summary>
		/// Closes the incoming channels
		/// </summary>
		protected void CloseIncomingChannel()
		{
			if (this._incommingChannel != null)
			{
				this._onIncomingChannelClosing?.Invoke();
				this._incommingChannel.Close("The incoming channel is closed when stop the service [" + this.ServiceURI + "]", new GoodbyeDetails());
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
				(new WampChannelReconnector(this._incommingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
					try
					{
						await this._incommingChannel.Open();
						onSuccess?.Invoke(this._incommingChannel);
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				})).Start();
		}

		/// <summary>
		/// Opens the outgoing channel
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <param name="onClosingConnection"></param>
		/// <returns></returns>
		protected async Task OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null, System.Action onClosingConnection = null)
		{
			if (this._outgoingChannel != null)
				return;

			var info = this.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			this._outgoingChannel = useJsonChannel
				? (new DefaultWampChannelFactory()).CreateJsonChannel(address, realm)
				: (new DefaultWampChannelFactory()).CreateMsgpackChannel(address, realm);

			this._outgoingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, arguments) =>
			{
				this._outgoingChannelSessionID = arguments.SessionId;
			};

			if (onConnectionEstablished != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._outgoingChannel.Open();

			this._onOutgoingChannelClosing = onClosingConnection;
		}

		/// <summary>
		/// Close the outgoing channel
		/// </summary>
		protected void CloseOutgoingChannel()
		{
			if (this._outgoingChannel != null)
			{
				this._onOutgoingChannelClosing?.Invoke();
				this._outgoingChannel.Close("The outgoing channel is closed when stop the service [" + this.ServiceURI + "]", new GoodbyeDetails());
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
				(new WampChannelReconnector(this._outgoingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
					try
					{
						await this._outgoingChannel.Open();
						onSuccess?.Invoke(this._outgoingChannel);
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				})).Start();
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
			await this.OpenIncomingChannelAsync();
			try
			{
				// register the service
				var name = this.ServiceName.Trim().ToLower();
				this._instance = await this._incommingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, new RegistrationInterceptor(name));

				// register the handler of inter-communicate messages
				this._communicator?.Dispose();
				this._communicator = this._incommingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages." + name)
					.Subscribe<CommunicateMessage>(
						message => this.ProcessInterCommunicateMessage(message),
						exception => this.WriteLog(UtilityService.NewUID, "APIGateway", "RTU", "Error occurred while fetching inter-communicate message of a service [" + this.ServiceName + "]: " + exception.Message, exception)
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
				await this.OpenOutgoingChannelAsync();
				this._rtuService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>();
			}
		}

		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendUpdateMessageAsync(message, cancellationToken);
		}

		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendUpdateMessagesAsync(messages, deviceID, excludedDeviceID, cancellationToken);
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
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommunicateMessageAsync(serviceName, message, cancellationToken);
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
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommunicateMessagesAsync(serviceName, messages, cancellationToken);
		}
		#endregion

		#region Send email & web hook messages
		async Task InitializeMessagingServiceAsync()
		{
			if (this._messagingService == null)
			{
				await this.OpenOutgoingChannelAsync();
				this._messagingService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IMessagingService>();
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
				await this.InitializeMessagingServiceAsync();
				await this._messagingService.SendEmailAsync(message, cancellationToken);
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
				await this.InitializeMessagingServiceAsync();
				await this._messagingService.SendWebHookAsync(message, cancellationToken);
			}
			catch { }
		}
		#endregion

		#region Working with logs
		async Task InitializeManagementServiceAsync()
		{
			if (this._managementService == null)
			{
				await this.OpenOutgoingChannelAsync();
				this._managementService = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IManagementService>();
			}
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="simpleStack">The simple stack (usually is Exception.StackTrace)</param>
		/// <param name="fullStack">The full stack (usually stack of the exception and all inners)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string simpleStack = null, string fullStack = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await this.InitializeManagementServiceAsync();
				await this._managementService.WriteLogAsync(correlationID, serviceName, objectName, log, simpleStack, fullStack, cancellationToken);
			}
			catch { }
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
			log = string.IsNullOrWhiteSpace(log) && exception != null
				? exception.Message
				: log;

			var simpleStack = exception != null
				? exception.StackTrace
				: "";

			var fullStack = "";
			if (exception != null)
			{
				fullStack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					fullStack += "\r\n"
						+ "-- Inner (" + counter.ToString() + ") ----------------- " + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
				}
			}

			return this.WriteLogAsync(correlationID, serviceName, objectName, log, simpleStack, fullStack, cancellationToken);
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
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		protected void WriteLog(string correlationID, string serviceName, string objectName, string log, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogAsync(correlationID, serviceName, objectName, log, exception);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="log">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLog(RequestInfo requestInfo, string log, Exception exception = null)
		{
			this.WriteLog(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, log, exception);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="simpleStack">The simple stack (usually is Exception.StackTrace)</param>
		/// <param name="fullStack">The full stack (usually stack of the exception and all inners)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string simpleStack = null, string fullStack = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				await this.InitializeManagementServiceAsync();
				await this._managementService.WriteLogsAsync(correlationID, serviceName, objectName, logs, simpleStack, fullStack, cancellationToken);
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
		/// <param name="exception">The exception</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var simpleStack = exception != null
				? exception.StackTrace
				: "";

			var fullStack = "";
			if (exception != null)
			{
				fullStack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					fullStack += "\r\n"
						+ "-- Inner (" + counter.ToString() + ") ----------------- " + "\r\n"
						+ inner.StackTrace;
					inner = inner.InnerException;
				}
			}

			return this.WriteLogsAsync(correlationID, serviceName, objectName, logs, simpleStack, fullStack, cancellationToken);
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
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLogs(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await this.WriteLogsAsync(correlationID, serviceName, objectName, logs, exception);
			}).ConfigureAwait(false);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		protected void WriteLogs(RequestInfo requestInfo, List<string> logs, Exception exception = null)
		{
			this.WriteLogs(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, logs, exception);
		}

		/// <summary>
		/// Writes a log message to the terminator or the standard output stream
		/// </summary>
		/// <param name="correlationID">The string that presents correlation identity</param>
		/// <param name="message">The log message</param>
		/// <param name="exception">The exception</param>
		/// <param name="updateCentralizedLogs">true to update the log message into centralized logs of the API Gateway</param>
		public virtual void WriteLog(string correlationID, string message, Exception exception = null, bool updateCentralizedLogs = true)
		{
			// prepare
			var msg = string.IsNullOrWhiteSpace(message)
				? exception?.Message ?? ""
				: message;

			// update the log message into centralized logs of the API Gateway
			if (updateCentralizedLogs)
				this.WriteLog(correlationID ?? UtilityService.NewUID, this.ServiceName, null, msg, exception);

			// write to the terminator or the standard output stream
			Console.WriteLine(msg);
			if (exception != null)
				Console.WriteLine("-----------------------\r\n" + "==> [" + exception.GetType().GetTypeName(true) + "]: " + exception.Message + "\r\n" + exception.StackTrace + "\r\n-----------------------");
			else
				Console.WriteLine("~~~~~~~~~~~~~~~~~~~~>");
		}
		#endregion

		#region Call other services
		/// <summary>
		/// Gets a service by name
		/// </summary>
		/// <param name="name">The string that presents name of a service</param>
		/// <returns></returns>
		protected async Task<IService> GetServiceAsync(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			if (!this._businessServices.TryGetValue(name, out IService service))
			{
				await this.OpenOutgoingChannelAsync();
				lock (this._businessServices)
				{
					if (!this._businessServices.TryGetValue(name, out service))
					{
						service = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(new CachedCalleeProxyInterceptor(new ProxyInterceptor(name)));
						this._businessServices.Add(name, service);
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
					? requestInfo.ServiceName.Trim().ToLower()
					: "unknown"
				)).ProcessRequestAsync(requestInfo, cancellationToken);
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
			}, cancellationToken);

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
					});
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
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <returns></returns>
		public async Task<bool> IsSystemAdministratorAsync(RequestInfo requestInfo)
		{
			return await this.IsSystemAdministratorAsync(requestInfo?.Session.User, requestInfo?.CorrelationID);
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
		protected virtual async Task<bool> IsAuthorizedAsync(RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<User, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			// administrator
			if (await this.IsSystemAdministratorAsync(requestInfo))
				return true;

			// individual user
			else
				return requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null
					? requestInfo.Session.User.IsAuthorized(requestInfo.ServiceName, requestInfo.ObjectName, requestInfo.GetObjectIdentity(true), action, privileges, getPrivileges, getActions)
					: false;
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
			return await this.IsSystemAdministratorAsync(requestInfo)
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
			return await this.IsSystemAdministratorAsync(user)
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
			if (await this.IsSystemAdministratorAsync(user))
				return true;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

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
			return await this.CanManageAsync(user, objectName, objectIdentity)
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
			if (await this.CanManageAsync(user, systemID, definitionID, objectID))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

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
			return await this.CanModerateAsync(user, objectName, objectIdentity)
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
			if (await this.CanModerateAsync(user, systemID, definitionID, objectID))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

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
			return await this.CanEditAsync(user, objectName, objectIdentity)
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
			if (await this.CanEditAsync(user, systemID, definitionID, objectID))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

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
			return await this.CanContributeAsync(user, objectName, objectIdentity)
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
			if (await this.CanContributeAsync(user, systemID, definitionID, objectID))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

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
			return await this.CanModerateAsync(user, objectName, objectIdentity)
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
			if (await this.CanModerateAsync(user, systemID, definitionID, objectID))
				return true;

			// check user
			if (user == null || string.IsNullOrWhiteSpace(user.ID))
				return false;

			// get the business object
			var @object = await RepositoryMediator.GetAsync(definitionID, objectID);

			// get the permissions state
			return @object != null && @object is IBusinessEntity
				? user.IsAuthorized(this.ServiceName, @object.GetType().GetTypeName(true), objectID, Components.Security.Action.Download, (@object as IBusinessEntity).WorkingPrivileges, this.GetPrivileges, this.GetPrivilegeActions)
				: false;
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

		/// <summary>
		/// Clears the related data from the cache storage
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="cache">The cache storage</param>
		/// <param name="filter">The filtering expression</param>
		/// <param name="sort">The sorting expression</param>
		protected void ClearRelatedCache<T>(Cache cache, IFilterBy<T> filter, SortBy<T> sort) where T : class
		{
			if (cache != null)
			{
				var key = this.GetCacheKey<T>(filter, sort);
				var keys = new List<string>() { key, key + "-total" };
				for (var index = 1; index <= 100; index++)
				{
					keys.Add(key + ":" + index.ToString());
					keys.Add(key + ":" + index.ToString() + "-json");
					keys.Add(key + ":" + index.ToString() + "-total");
				}
				cache.Remove(keys);
			}
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
					: "Error occurred while processing with the service [net.vieapps.services." + requestInfo.ServiceName.ToLower().Trim() + "]"
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

		/// <summary>
		/// Starts the service
		/// </summary>
		/// <param name="args">The starting arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="nextAction">The next action to run (synchronous)</param>
		/// <param name="nextActionAsync">The next action to run (asynchronous)</param>
		public virtual void Start(string[] args = null, bool initializeRepository = true, System.Action nextAction = null, Func<Task> nextActionAsync = null)
		{
			// prepare
			var correlationID = UtilityService.NewUID;

			// initialize repository
			if (initializeRepository)
				try
				{
					this.WriteLog(correlationID, "Initializing the repository");
					RepositoryStarter.Initialize();
				}
				catch (Exception ex)
				{
					this.WriteLog(correlationID, "Error occurred while initializing the repository", ex);
				}

			// start the service
			Task.Run(async () =>
			{
				try
				{
					await this.StartAsync(
						service => this.WriteLog(correlationID, "The service is registered - PID: " + Process.GetCurrentProcess().Id.ToString()),
						exception => this.WriteLog(correlationID, "Error occurred while registering the service", exception)
					);
				}
				catch (Exception ex)
				{
					this.WriteLog(correlationID, "Error occurred while starting the service", ex);
				}
			})
			.ContinueWith(async (task) =>
			{
				try
				{
					nextAction?.Invoke();
				}
				catch (Exception ex)
				{
					this.WriteLog(correlationID, "Error occurred while running the next action (sync)", ex);
				}

				if (nextActionAsync != null)
					try
					{
						await nextActionAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.WriteLog(correlationID, "Error occurred while running the next action (async)", ex);
					}
			})
			.ConfigureAwait(false);
		}

		/// <summary>
		/// Starts the service in the short way (open channels and register service)
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
			await this.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError);
			await this.RegisterServiceAsync(onRegisterSuccess, onRegisterError);
			await this.OpenOutgoingChannelAsync(onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError);
		}

		/// <summary>
		/// Stops this service (close channels and clean-up)
		/// </summary>
		public void Stop()
		{
			this._communicator?.Dispose();
			Task.Run(async () =>
			{
				if (this._instance != null)
				{
					try
					{
						await this._instance.DisposeAsync();
					}
					catch { }
					this._instance = null;
				}
			})
			.ContinueWith(task =>
			{
				this.CloseIncomingChannel();
				this.CloseOutgoingChannel();
			})
			.ConfigureAwait(false);
		}

		public void Dispose()
		{
			this.Stop();
			GC.SuppressFinalize(this);
		}

		~ServiceBase()
		{
			this.Dispose();
		}
	}
}