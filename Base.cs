#region Related components
using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Configuration;
using System.Reflection;

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
	/// Presents a abstract micro service that provides some kind of business
	/// </summary>
	public abstract class BaseService : IDisposable
	{

		#region Attributes
		IWampChannel _incommingChannel = null, _outgoingChannel = null;
		System.Action _onCloseIncomingChannel = null, _onCloseOutgoingChannel = null;
		System.Action<BaseMessage> _onReceiveInterCommuniateMessage = null;
		IDisposable _subscriber = null;
		IRTUService _rtuService = null;
		IManagementService _managementService = null;
		Dictionary<string, IService> _services = new Dictionary<string, IService>();
		Dictionary<string, ServiceInterceptor> _interceptors = new Dictionary<string, ServiceInterceptor>();
		#endregion

		#region Open/Close channels, register service/messages, ...
		/// <summary>
		/// Gets the location information from configuration
		/// </summary>
		/// <returns></returns>
		protected virtual Tuple<string, string, bool> GetLocationInfo()
		{
			var address = ConfigurationManager.AppSettings["Address"];
			if (string.IsNullOrEmpty(address))
				address = "ws://127.0.0.1:26429/";

			var realm = ConfigurationManager.AppSettings["Realm"];
			if (string.IsNullOrEmpty(realm))
				realm = "VIEAppsRealm";

			var mode = ConfigurationManager.AppSettings["Mode"];
			if (string.IsNullOrEmpty(mode))
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

			if (onConnectionEstablished != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				this._incommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._incommingChannel.Open();

			this._onCloseIncomingChannel = onClosingConnection;
		}

		/// <summary>
		/// Closes the incoming channels
		/// </summary>
		protected void CloseIncomingChannel()
		{
			if (this._incommingChannel != null)
			{
				if (this._onCloseIncomingChannel != null)
					this._onCloseIncomingChannel();
				this._incommingChannel.Close();
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
						if (onSuccess != null)
							onSuccess();
					}
					catch (Exception ex)
					{
						if (onError != null)
							onError(ex);
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

			if (onConnectionEstablished != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				this._outgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await this._outgoingChannel.Open();

			this._onCloseOutgoingChannel = onClosingConnection;
		}

		/// <summary>
		/// Close the outgoing channel
		/// </summary>
		protected void CloseOutgoingChannel()
		{
			if (this._outgoingChannel != null)
			{
				if (this._onCloseOutgoingChannel != null)
					this._onCloseOutgoingChannel();
				this._outgoingChannel.Close();
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
						if (onSuccess != null)
							onSuccess();
					}
					catch (Exception ex)
					{
						if (onError != null)
							onError(ex);
					}
				})).Start();
		}

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
				var interceptor = new CalleeRegistrationInterceptor(new RegisterOptions() { Invoke = WampInvokePolicy.Roundrobin });
				await this._incommingChannel.RealmProxy.Services.RegisterCallee(this, interceptor);
				if (onSuccess != null)
					onSuccess();
			}
			catch (Exception ex)
			{
				if (onError != null)
					onError(ex);
			}
		}

		/// <summary>
		/// Registers the handler to process inter-communicate message
		/// </summary>
		/// <param name="onMessageReceived"></param>
		/// <returns></returns>
		protected async Task RegisterInterCommunicateMessageHandlerAsync(Action<BaseMessage> onMessageReceived)
		{
			await this.OpenIncomingChannelAsync();
			if (onMessageReceived != null)
			{
				this._onReceiveInterCommuniateMessage = onMessageReceived;
				if (this._subscriber != null)
					this._subscriber.Dispose();

				var subject = this._incommingChannel.RealmProxy.Services.GetSubject<BaseMessage>("net.vieapps.rtu.service.messages." + (this as IService).ServiceName.ToLower());
				this._subscriber = subject.Subscribe<BaseMessage>(message =>
				{
					this._onReceiveInterCommuniateMessage(message);
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
		public async Task SendUpdateMessageAsync(UpdateMessage message)
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
		public async Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID = null)
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
		public async Task SendInterCommuniateMessageAsync(string serviceName, BaseMessage message)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommuniateMessageAsync(serviceName, message);
		}

		/// <summary>
		/// Send a message for updating data of other service
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <returns></returns>
		public async Task SendInterCommuniateMessagesAsync(string serviceName, List<BaseMessage> messages)
		{
			await this.InitializeRTUServiceAsync();
			await this._rtuService.SendInterCommuniateMessagesAsync(serviceName, messages);
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
		public async Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string stack = null)
		{
			await this.InitializeManagementServiceAsync();
			await this._managementService.WriteLogAsync(correlationID, serviceName, objectName, log, stack);
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
		public Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, Exception exception = null)
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
		public Task WriteLogAsync(RequestInfo requestInfo, string log, Exception exception = null)
		{
			return this.WriteLogAsync(requestInfo.Session.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, log, exception);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="exception">The exception</param>
		public void WriteLog(string correlationID, string serviceName, string objectName, string log, Exception exception = null)
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
		public void WriteLog(RequestInfo requestInfo, string log, Exception exception = null)
		{
			this.WriteLog(requestInfo.Session.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, log, exception);
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
		public async Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string stack = null)
		{
			await this.InitializeManagementServiceAsync();
			await this._managementService.WriteLogsAsync(correlationID, serviceName, objectName, logs, stack);
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
		public Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null)
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
		public Task WriteLogsAsync(RequestInfo requestInfo, List<string> logs, Exception exception = null)
		{
			return this.WriteLogsAsync(requestInfo.Session.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, logs, exception);
		}

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="exception">The exception</param>
		public void WriteLogs(string correlationID, string serviceName, string objectName, List<string> logs, Exception exception = null)
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
		public void WriteLogs(RequestInfo requestInfo, List<string> logs, Exception exception = null)
		{
			this.WriteLogs(requestInfo.Session.CorrelationID, requestInfo.ServiceName, requestInfo.ObjectName, logs, exception);
		}
		#endregion

		#region Call other services
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
		protected async Task<JObject> Call(Session session, string serviceName, string objectName, string verb = "GET", NameValueCollection query = null, NameValueCollection header = null, string body = null, NameValueCollection extra = null)
		{
			var requestInfo = new RequestInfo()
			{
				Session = session,
				ServiceName = serviceName,
				ObjectName = objectName,
				Verb = string.IsNullOrWhiteSpace(verb) ? "GET" : verb,
				Query = query,
				Header = header,
				Body = body
			};

			var key = requestInfo.ServiceName.Trim().ToLower();
			IService service;
			if (!this._services.TryGetValue(key, out service))
			{
				ServiceInterceptor interceptor;
				if (!this._interceptors.TryGetValue(key, out interceptor))
				{
					interceptor = new ServiceInterceptor(key);
					this._interceptors.Add(key, interceptor);
				}

				await this.OpenOutgoingChannelAsync();
				service = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(interceptor);
				this._services.Add(key, service);
			}

			return await service.ProcessRequestAsync(requestInfo, extra);
		}
		#endregion

		/// <summary>
		/// Starts the service in the short way (open channels and register service)
		/// </summary>
		/// <param name="onRegisterSuccess"></param>
		/// <param name="onRegisterError"></param>
		/// <param name="onReceiveInterCommunicateMessage"></param>
		/// <param name="onIncomingConnectionEstablished"></param>
		/// <param name="onOutgoingConnectionEstablished"></param>
		/// <param name="onIncomingConnectionBroken"></param>
		/// <param name="onOutgoingConnectionBroken"></param>
		/// <param name="onIncomingConnectionError"></param>
		/// <param name="onOutgoingConnectionError"></param>
		/// <returns></returns>
		protected async Task StartAsync(System.Action onRegisterSuccess = null, Action<Exception> onRegisterError = null, Action<BaseMessage> onReceiveInterCommunicateMessage = null, Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null, Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null, Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null, Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null)
		{
			await this.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError);
			await this.RegisterServiceAsync(onRegisterSuccess, onRegisterError);
			await this.RegisterInterCommunicateMessageHandlerAsync(onReceiveInterCommunicateMessage);
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
		/// Gets the exception to throw to caller
		/// </summary>
		/// <param name="requestInfo">The request information</param>
		/// <param name="message">The message</param>
		/// <param name="exception">The exception</param>
		/// <param name="writeLogs">true to write into centralized logs</param>
		/// <returns></returns>
		public WampRpcRuntimeException GetException(RequestInfo requestInfo, string message, Exception exception, bool writeLogs = true)
		{
			message = string.IsNullOrWhiteSpace(message)
				? "Error occurred while processing"
				: message;

			if (writeLogs)
				this.WriteLog(requestInfo, message, exception);

			return new WampRpcRuntimeException(null, null, null, message, exception);
		}

		/// <summary>
		/// Disposes this service
		/// </summary>
		public virtual void Dispose()
		{
			this.Stop();
		}
	}
}