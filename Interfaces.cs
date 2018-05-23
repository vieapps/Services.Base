#region Related components
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using WampSharp.V2.Rpc;
using Newtonsoft.Json.Linq;

using Microsoft.Extensions.Logging;

using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a business service
	/// </summary>
	public interface IService : IDisposable
	{
		/// <summary>
		/// Gets the name of this service (for working with WAMP protocol)
		/// </summary>
		string ServiceName { get; }

		/// <summary>
		/// Gets the URI of this service (with full namespace - for working with WAMP protocol)
		/// </summary>
		string ServiceURI { get; }

		/// <summary>
		/// Gets or sets the logger
		/// </summary>
		ILogger Logger { get; set; }

		/// <summary>
		/// Process the request of this service
		/// </summary>
		/// <param name="requestInfo">Requesting Information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}")]
		Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.manage.object")]
		Task<bool> CanManageAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to manage or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.manage.definition")]
		Task<bool> CanManageAsync(IUser user, string systemID, string definitionID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.moderate.object")]
		Task<bool> CanModerateAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to moderate or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.moderate.definition")]
		Task<bool> CanModerateAsync(IUser user, string systemID, string definitionID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.edit.object")]
		Task<bool> CanEditAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to edit or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.edit.definition")]
		Task<bool> CanEditAsync(IUser user, string systemID, string definitionID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.contribute.object")]
		Task<bool> CanContributeAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to contribute or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.contribute.definition")]
		Task<bool> CanContributeAsync(IUser user, string systemID, string definitionID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.view.object")]
		Task<bool> CanViewAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to view or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.view.definition")]
		Task<bool> CanViewAsync(IUser user, string systemID, string definitionID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to download or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="objectName">The name of the service's object</param>
		/// <param name="objectIdentity">The identity of the service's object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.download.object")]
		Task<bool> CanDownloadAsync(IUser user, string objectName, string objectIdentity);

		/// <summary>
		/// Gets the state that determines the user is able to download the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the action</param>
		/// <param name="systemID">The identity of the business system</param>
		/// <param name="definitionID">The identity of the entity definition</param>
		/// <param name="objectID">The identity of the business object</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.permissions.download.definition")]
		Task<bool> CanDownloadAsync(IUser user, string systemID, string definitionID, string objectID);
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a service component
	/// </summary>
	public interface IServiceComponent : IDisposable
	{
		/// <summary>
		/// Gets the name of this service (for working with WAMP protocol)
		/// </summary>
		string ServiceName { get; }

		/// <summary>
		/// Gets the URI of this service (with full namespace - for working with WAMP protocol)
		/// </summary>
		string ServiceURI { get; }

		/// <summary>
		/// Gets or sets the logger
		/// </summary>
		ILogger Logger { get; set; }

		/// <summary>
		/// Starts the service
		/// </summary>
		/// <param name="args">The starting arguments</param>
		/// <param name="initializeRepository">true to initialize the repository of the service</param>
		/// <param name="nextAsync">The next action to run asynchronously</param>
		void Start(string[] args = null, bool initializeRepository = true, Func<IService, Task> nextAsync = null);

		/// <summary>
		/// Stops the service
		/// </summary>
		void Stop();
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a real-time update (RTU) service
	/// </summary>
	public interface IRTUService
	{
		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.update.message")]
		Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.update.messages")]
		Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.intercommunicate.message")]
		Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.services.intercommunicate.message")]
		Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.intercommunicate.messages")]
		Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.services.intercommunicate.messages")]
		Task SendInterCommunicateMessagesAsync(List<CommunicateMessage> messages, CancellationToken cancellationToken = default(CancellationToken));
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a logging service
	/// </summary>
	public interface ILoggingService
	{
		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="log">The log message</param>
		/// <param name="stack">The simple stack (usually is Exception.StackTrace)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.logs.single")]
		Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string stack = null, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Writes the log into centralized log storage of all services
		/// </summary>
		/// <param name="correlationID">The identity of correlation</param>
		/// <param name="serviceName">The name of service</param>
		/// <param name="objectName">The name of serivice's object</param>
		/// <param name="logs">The collection of log messages</param>
		/// <param name="stack">The simple stack (usually is Exception.StackTrace)</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.logs.multiple")]
		Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string stack = null, CancellationToken cancellationToken = default(CancellationToken));
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a messaging service for sending email and web-hook messages
	/// </summary>
	public interface IMessagingService
	{
		/// <summary>
		/// Sends an email message
		/// </summary>
		/// <param name="message">The email message for sending</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.messaging.email")]
		Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Sends a web hook message
		/// </summary>
		/// <param name="message">The web hook message for sending</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.messaging.webhook")]
		Task SendWebHookAsync(WebHookMessage message, CancellationToken cancellationToken = default(CancellationToken));
	}
}