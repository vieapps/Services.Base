﻿#region Related components
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Configuration;
using System.Linq;

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
	/// Presents an abstract service (base of all services)
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
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public abstract Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken));

		#region Attributes & Properties
		IWampChannel _incommingChannel = null, _outgoingChannel = null;
		long _incommingChannelSessionID = 0, _outgoingChannelSessionID = 0;
		System.Action _onIncomingChannelClosing = null, _onOutgoingChannelClosing = null;
		List<Action<CommunicateMessage>> _interCommunicateMessageHandlers = new List<Action<CommunicateMessage>>();
		IDisposable _communicator = null;
		IRTUService _rtuService = null;
		IManagementService _managementService = null;
		Dictionary<string, IService> _services = new Dictionary<string, IService>();

		/// <summary>
		/// Gets the full URI of the service
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
		protected void ReOpenIncomingChannel(int delay = 0, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (this._incommingChannel != null)
				(new WampChannelReconnector(this._incommingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
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
		protected void ReOpenOutgoingChannel(int delay = 0, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (this._outgoingChannel != null)
				(new WampChannelReconnector(this._outgoingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
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
		/// Registers the handler to process inter-communicate messages
		/// </summary>
		/// <param name="onInterCommunicateMessageReceived"></param>
		/// <returns></returns>
		protected async Task RegisterInterCommunicateMessageHandlerAsync(Action<CommunicateMessage> onInterCommunicateMessageReceived)
		{
			await this.OpenIncomingChannelAsync();
			if (onInterCommunicateMessageReceived != null)
			{
				this._interCommunicateMessageHandlers.Add(onInterCommunicateMessageReceived);
				this._communicator?.Dispose();

				var subject = this._incommingChannel.RealmProxy.Services.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages");
				this._communicator = subject.Subscribe<CommunicateMessage>(message =>
				{
					if (message.ServiceName.IsEquals(this.ServiceName))
						this._interCommunicateMessageHandlers.ForEach(action =>
						{
							try
							{
								action.Invoke(message);
							}
							catch { }
						});
				},
				exception => this.WriteLog(UtilityService.BlankUID, "APIGateway", "RTU", "Error occurred while fetching inter-communicate message", exception));
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
		#endregion

		#region Call other services
		/// <summary>
		/// Calls other service
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		protected async Task<JObject> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var name = requestInfo != null && !string.IsNullOrWhiteSpace(requestInfo.ServiceName)
				? requestInfo.ServiceName.Trim().ToLower()
				: "unknown";

			if (!this._services.TryGetValue(name, out IService service))
			{
				await this.OpenOutgoingChannelAsync();
				lock (this._services)
				{
					if (!this._services.TryGetValue(name, out service))
					{
						service = this._outgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(new CachedCalleeProxyInterceptor(new ProxyInterceptor(name)));
						this._services.Add(name, service);
					}
				}
			}

			return await service.ProcessRequestAsync(requestInfo, cancellationToken);
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
		/// <param name="correlationID"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected async Task<JObject> CallServiceAsync(Session session, string serviceName, string objectName, string verb = "GET", Dictionary<string, string> query = null, Dictionary<string, string> header = null, string body = null, Dictionary<string, string> extra = null, string correlationID = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return await this.CallServiceAsync(new RequestInfo()
			{
				Session = session ?? new Session(),
				ServiceName = serviceName ?? "unknown",
				ObjectName = objectName ?? "unknown",
				Verb = string.IsNullOrWhiteSpace(verb) ? "GET" : verb,
				Query = query ?? new Dictionary<string, string>(),
				Header = header ?? new Dictionary<string, string>(),
				Body = body,
				Extra = extra ?? new Dictionary<string, string>(),
				CorrelationID = correlationID ?? UtilityService.NewUID
			}, cancellationToken);
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
		/// Gets the state that determines the user can perform the action or not
		/// </summary>
		/// <param name="requestInfo">The requesting information that contains user information</param>
		/// <param name="action">The action to perform on the object of this service</param>
		/// <param name="privileges">The working privileges of the object (entity)</param>
		/// <param name="getPrivileges">The function to prepare the collection of privileges</param>
		/// <param name="getActions">The function to prepare the actions of each privilege</param>
		/// <returns></returns>
		protected bool IsAuthorized(RequestInfo requestInfo, Components.Security.Action action, Privileges privileges = null, Func<User, Privileges, List<Privilege>> getPrivileges = null, Func<PrivilegeRole, List<string>> getActions = null)
		{
			return requestInfo != null && requestInfo.Session != null && requestInfo.Session.User != null
				? requestInfo.Session.User.IsAuthorized(requestInfo.ServiceName, requestInfo.ObjectName, action, privileges, getPrivileges, getActions)
				: false;
		}
		#endregion

		#region Authorization (for working with of service of files)
		/// <summary>
		/// Gets the state that determines the user is able to upload the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		public virtual Task<bool> IsAbleToUploadAsync(User user, string systemID, string entityID, string objectID)
		{
			return Task.FromResult(true);
		}

		/// <summary>
		/// Gets the state that determines the user is able to download the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		public virtual Task<bool> IsAbleToDownloadAsync(User user, string systemID, string entityID, string objectID)
		{
			return Task.FromResult(true);
		}

		/// <summary>
		/// Gets the state that determines the user is able to delete the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		public virtual Task<bool> IsAbleToDeleteAsync(User user, string systemID, string entityID, string objectID)
		{
			return Task.FromResult(true);
		}

		/// <summary>
		/// Gets the state that determines the user is able to restore the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		public virtual Task<bool> IsAbleToRestoreAsync(User user, string systemID, string entityID, string objectID)
		{
			return Task.FromResult(true);
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
		protected async Task StartAsync(System.Action onRegisterSuccess = null, Action<Exception> onRegisterError = null, Action<CommunicateMessage> onInterCommunicateMessageReceived = null, Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null, Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null, Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null, Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null)
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
			this._communicator?.Dispose();
			this.CloseIncomingChannel();
			this.CloseOutgoingChannel();
		}

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

		#region Dispose/Destructor
		bool disposed = false;

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				// free other state (managed objects)
				if (disposing)
				{
				}

				// free your own state (unmanaged objects).
				this.Stop();
				disposed = true;
			}
		}

		~BaseService()
		{
			this.Dispose(false);
		}
		#endregion

	}
}