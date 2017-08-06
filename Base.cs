#region Related components
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Configuration;

using WampSharp.V2;
using WampSharp.V2.Rpc;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Client;
using WampSharp.V2.Realm;
using WampSharp.Core.Listener;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents an abstract service
	/// </summary>
	public abstract class BaseService : IService, IDisposable
	{
		/// <summary>
		/// Gets the name of this service (for working with related URIs)
		/// </summary>
		public abstract string ServiceName { get; }

		/// <summary>
		/// Process the request of this service
		/// </summary>
		/// <param name="requestInfo">Requesting Information</param>
		/// <returns></returns>
		public abstract Task<JObject> ProcessRequestAsync(RequestInfo requestInfo);

		#region Properties
		IWampChannel _incommingChannel = null, _outgoingChannel = null;
		long _incommingChannelSessionID = 0, _outgoingChannelSessionID = 0;
		System.Action _onIncomingChannelClosing = null, _onOutgoingChannelClosing = null;
		Action<BaseMessage> _onInterCommunicateMessageReceived = null;
		IDisposable _subscriber = null;
		IRTUService _rtuService = null;
		IManagementService _managementService = null;
		Dictionary<string, IService> _services = new Dictionary<string, IService>();

		/// <summary>
		/// Gets the full URI of this service
		/// </summary>
		public string ServiceURI
		{
			get
			{
				return "net.vieapps.services." + this.ServiceName.ToLower().Trim();
			}
		}
		#endregion

		#region Open/Close channels
		/// <summary>
		/// Gets the location information from configuration
		/// </summary>
		/// <returns></returns>
		protected virtual Tuple<string, string, bool> GetLocationInfo()
		{
			var address = ConfigurationManager.AppSettings["RouterAddress"];
			if (string.IsNullOrWhiteSpace(address))
				address = "ws://127.0.0.1:26429/";

			var realm = ConfigurationManager.AppSettings["RouterRealm"];
			if (string.IsNullOrWhiteSpace(realm))
				realm = "VIEAppsRealm";

			var mode = ConfigurationManager.AppSettings["RouterChannelsMode"];
			if (string.IsNullOrWhiteSpace(mode))
				mode = "MsgPack";

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

			var info = this.GetLocationInfo();
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
				if (this._onIncomingChannelClosing != null)
					this._onIncomingChannelClosing();
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
		protected void ReOpenIncomingChannel(int delay = 0, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (this._incommingChannel != null)
				(new WampChannelReconnector(this._incommingChannel, async () =>
				{
					if (delay > 0)
						await Task.Delay(delay);

					try
					{
						await this._incommingChannel.Open();
						onSuccess?.Invoke();
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

			var info = this.GetLocationInfo();
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
				if (this._onOutgoingChannelClosing != null)
					this._onOutgoingChannelClosing();
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
		protected void ReOpenOutgoingChannel(int delay = 0, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (this._outgoingChannel != null)
				(new WampChannelReconnector(this._outgoingChannel, async () =>
				{
					if (delay > 0)
						await Task.Delay(delay);

					try
					{
						await this._outgoingChannel.Open();
						onSuccess?.Invoke();
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				})).Start();
		}
		#endregion

		#region Register service & handler of inter-communicate messages
		/// <summary>
		/// Registers the service
		/// </summary>
		/// <param name="onSuccess"></param>
		/// <param name="onError"></param>
		/// <returns></returns>
		protected async Task RegisterServiceAsync(System.Action onSuccess = null, Action<Exception> onError = null)
		{
			await this.OpenIncomingChannelAsync();
			try
			{
				await this._incommingChannel.RealmProxy.Services.RegisterCallee<IService>(() => this, new RegistrationInterceptor(this.ServiceName.ToLower().Trim()));
				onSuccess?.Invoke();
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
		}

		/// <summary>
		/// Registers the handler to process inter-communicate message
		/// </summary>
		/// <param name="onInterCommunicateMessageReceived"></param>
		/// <returns></returns>
		protected async Task RegisterInterCommunicateMessageHandlerAsync(Action<BaseMessage> onInterCommunicateMessageReceived)
		{
			await this.OpenIncomingChannelAsync();
			if (onInterCommunicateMessageReceived != null)
			{
				this._onInterCommunicateMessageReceived = onInterCommunicateMessageReceived;
				if (this._subscriber != null)
					this._subscriber.Dispose();

				var subject = this._incommingChannel.RealmProxy.Services.GetSubject<BaseMessage>("net.vieapps.rtu.communicate.messages." + this.ServiceName.ToLower());
				this._subscriber = subject.Subscribe<BaseMessage>(message =>
				{
					try
					{
						this._onInterCommunicateMessageReceived(message);
					}
					catch { }
				});
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
		/// <returns></returns>
		protected async Task SendUpdateMessageAsync(UpdateMessage message)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendUpdateMessageAsync(message);
		}

		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <returns></returns>
		protected async Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendUpdateMessagesAsync(messages, deviceID, excludedDeviceID);
		}

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <returns></returns>
		protected async Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommunicateMessageAsync(serviceName, message);
		}

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <returns></returns>
		protected async Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommunicateMessagesAsync(serviceName, messages);
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
		/// <param name="stack">The stack trace (usually is Exception.StackTrace)</param>
		/// <returns></returns>
		protected async Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string stack = null)
		{
			try
			{
				await this.InitializeManagementServiceAsync();
				await this._managementService.WriteLogAsync(correlationID, serviceName, objectName, log, stack);
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
		/// <returns></returns>
		protected Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, Exception exception = null)
		{
			log = string.IsNullOrWhiteSpace(log) && exception != null
				? exception.Message
				: log;

			var stack = "";
			if (exception != null)
			{
				stack = exception.StackTrace;
				var ex = exception.InnerException;
				var counter = 0;
				while (ex != null)
				{
					counter++;
					stack += "\r\n"
						+ "-- Inner (" + counter.ToString() + ") ----------------- " + "\r\n"
						+ ex.StackTrace;
					ex = ex.InnerException;
				}
			}

			return this.WriteLogAsync(correlationID, serviceName, objectName, log, stack);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		/// <returns></returns>
		protected Task WriteLogAsync(RequestInfo requestInfo, string log, Exception exception = null)
		{
			return this.WriteLogAsync(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, log, exception);
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
		/// <param name="logs">The collection of log messages</param>
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
		/// <param name="stack">The stack trace (usually is Exception.StackTrace)</param>
		/// <returns></returns>
		protected async Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string stack = null)
		{
			try
			{
				await this.InitializeManagementServiceAsync();
				await this._managementService.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack);
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
		/// <returns></returns>
		protected Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null)
		{
			var stack = "";
			if (exception != null)
			{
				stack = exception.StackTrace;
				var ex = exception.InnerException;
				var counter = 0;
				while (ex != null)
				{
					counter++;
					stack += "\r\n"
						+ "-- Inner (" + counter.ToString() + ") ----------------- " + "\r\n"
						+ ex.StackTrace;
					ex = ex.InnerException;
				}
			}

			return this.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		/// <returns></returns>
		protected Task WriteLogsAsync(RequestInfo requestInfo, List<string> logs, Exception exception = null)
		{
			return this.WriteLogsAsync(requestInfo.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, logs, exception);
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
		#endregion

		#region Call other services
		/// <summary>
		/// Calls other service
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		protected async Task<JObject> CallAsync(RequestInfo requestInfo)
		{
			var key = requestInfo.ServiceName.Trim().ToLower();
			if (!this._services.TryGetValue(key, out IService service))
			{
				await this.OpenOutgoingChannelAsync();
				lock (this._services)
				{
					if (!this._services.TryGetValue(key, out service))
					{
						service = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(new CachedCalleeProxyInterceptor(new ProxyInterceptor(key)));
						this._services.Add(key, service);
					}
				}
			}

			return await service.ProcessRequestAsync(requestInfo);
		}

		/// <summary>
		/// Calls other service
		/// </summary>
		/// <param name="session"></param>
		/// <param name="serviceName"></param>
		/// <param name="objectName"></param>
		/// <param name="verb"></param>
		/// <param name="query"></param>
		/// <param name="header"></param>
		/// <param name="body"></param>
		/// <param name="extra"></param>
		/// <returns></returns>
		protected async Task<JObject> CallAsync(Session session, string serviceName, string objectName, string verb = "GET", Dictionary<string, string> query = null, Dictionary<string, string> header = null, string body = null, Dictionary<string, string> extra = null)
		{
			return await this.CallAsync(new RequestInfo()
			{
				Session = session,
				ServiceName = serviceName,
				ObjectName = objectName,
				Verb = string.IsNullOrWhiteSpace(verb) ? "GET" : verb,
				Query = query,
				Header = header,
				Body = body,
				Extra = extra
			});
		}
		#endregion

		/// <summary>
		/// Starts the service in the short way (open channels and register service)
		/// </summary>
		/// <param name="onRegisterSuccess"></param>
		/// <param name="onRegisterError"></param>
		/// <param name="onInterCommunicateMessageReceived"></param>
		/// <param name="onIncomingConnectionEstablished"></param>
		/// <param name="onOutgoingConnectionEstablished"></param>
		/// <param name="onIncomingConnectionBroken"></param>
		/// <param name="onOutgoingConnectionBroken"></param>
		/// <param name="onIncomingConnectionError"></param>
		/// <param name="onOutgoingConnectionError"></param>
		/// <returns></returns>
		protected async Task StartAsync(System.Action onRegisterSuccess = null, Action<Exception> onRegisterError = null, Action<BaseMessage> onInterCommunicateMessageReceived = null, Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null, Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null, Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null, Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null)
		{
			await this.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError);
			await this.RegisterServiceAsync(onRegisterSuccess, onRegisterError);
			await this.RegisterInterCommunicateMessageHandlerAsync(onInterCommunicateMessageReceived);
			await this.OpenOutgoingChannelAsync(onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError);
		}

		/// <summary>
		/// Stops this service (close channels and clean-up)
		/// </summary>
		protected void Stop()
		{
			if (this._subscriber != null)
				this._subscriber.Dispose();
			this.CloseIncomingChannel();
			this.CloseOutgoingChannel();
		}

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

		/// <summary>
		/// Disposes this service
		/// </summary>
		public void Dispose()
		{
			this.Stop();
		}
	}
}