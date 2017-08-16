#region Related components
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using WampSharp.V2.Rpc;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a business service
	/// </summary>
	public interface IService
	{
		/// <summary>
		/// Gets the name of this service (for working with related URIs)
		/// </summary>
		string ServiceName { get; }

		/// <summary>
		/// Gets the full URI of this service
		/// </summary>
		string ServiceURI { get; }

		/// <summary>
		/// Process the request of this service
		/// </summary>
		/// <param name="requestInfo">Requesting Information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}")]
		Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Gets the state that determines the user is able to upload the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.files.permissions.upload")]
		Task<bool> IsAbleToUploadAsync(User user, string systemID, string entityID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to download the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.files.permissions.download")]
		Task<bool> IsAbleToDownloadAsync(User user, string systemID, string entityID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to delete the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.files.permissions.delete")]
		Task<bool> IsAbleToDeleteAsync(User user, string systemID, string entityID, string objectID);

		/// <summary>
		/// Gets the state that determines the user is able to restore the attachment files or not
		/// </summary>
		/// <param name="user">The user who performs the download action</param>
		/// <param name="systemID">The identity of the business system that the attachment file is belong to</param>
		/// <param name="entityID">The identity of the entity definition that the attachment file is belong to</param>
		/// <param name="objectID">The identity of the business object that the attachment file is belong to</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.{0}.files.permissions.restore")]
		Task<bool> IsAbleToRestoreAsync(User user, string systemID, string entityID, string objectID);
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
		[WampProcedure("net.vieapps.services.rtu.client.message")]
		Task SendUpdateMessageAsync(UpdateMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for updating data of client
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="deviceID">The string that presents a client's device identity for receiving the messages</param>
		/// <param name="excludedDeviceID">The string that presents identity of a device to be excluded</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.client.messages")]
		Task SendUpdateMessagesAsync(List<BaseMessage> messages, string deviceID, string excludedDeviceID, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.message.base")]
		Task SendInterCommunicateMessageAsync(string serviceName, BaseMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.message.full")]
		Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="serviceName">The name of a service</param>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.messages.base")]
		Task SendInterCommunicateMessagesAsync(string serviceName, List<BaseMessage> messages, CancellationToken cancellationToken = default(CancellationToken));

		/// <summary>
		/// Send a message for communicating with  of other services
		/// </summary>
		/// <param name="messages">The collection of messages</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		[WampProcedure("net.vieapps.services.rtu.service.messages.full")]
		Task SendInterCommunicateMessagesAsync(List<CommunicateMessage> messages, CancellationToken cancellationToken = default(CancellationToken));
	}

	//  --------------------------------------------------------------------------------------------

	/// <summary>
	/// Presents a management service
	/// </summary>
	public interface IManagementService
	{
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
		[WampProcedure("net.vieapps.services.management.writelog")]
		Task WriteLogAsync(string correlationID, string serviceName, string objectName, string log, string simpleStack = null, string fullStack = null, CancellationToken cancellationToken = default(CancellationToken));

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
		[WampProcedure("net.vieapps.services.management.writelogs")]
		Task WriteLogsAsync(string correlationID, string serviceName, string objectName, List<string> logs, string simpleStack = null, string fullStack = null, CancellationToken cancellationToken = default(CancellationToken));
	}
}