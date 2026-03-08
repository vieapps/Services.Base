#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		/// <summary>
		/// Sends the messages
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this ManagedWebSocket websocket, IEnumerable<string> messages, CancellationToken cancellationToken = default)
			=> (messages ?? Array.Empty<string>()).Where(message => !string.IsNullOrWhiteSpace(message)).ForEachAsync(message => websocket.SendAsync(message, cancellationToken), true, false);

		/// <summary>
		/// Sends the messages
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="messages"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this ManagedWebSocket websocket, IEnumerable<JToken> messages, CancellationToken cancellationToken = default)
			=> websocket.SendAsync(messages?.Where(message => message != null).Select(message => message.ToString(Formatting.None)), cancellationToken);

		/// <summary>
		/// Sends the message
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendAsync(this ManagedWebSocket websocket, JToken message, CancellationToken cancellationToken = default)
			=> websocket.SendAsync(message?.ToString(Formatting.None), cancellationToken);

		/// <summary>
		/// Sends an update message
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="toJsonPreCompleted"></param>
		/// <returns></returns>
		public static Task SendAsync(this ManagedWebSocket websocket, UpdateMessage message, CancellationToken cancellationToken = default, Action<JToken> toJsonPreCompleted = null)
			=> websocket.SendAsync(message?.ToJson(toJsonPreCompleted), cancellationToken);

		/// <summary>
		/// Prepares the information of the connection
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="correlationID"></param>
		/// <param name="session"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="logger"></param>
		/// <returns></returns>
		public static async Task PrepareConnectionInfoAsync(this ManagedWebSocket websocket, string correlationID = null, Session session = null, CancellationToken cancellationToken = default, Microsoft.Extensions.Logging.ILogger logger = null)
		{
			correlationID = correlationID ?? UtilityService.NewUUID;
			session = session ?? websocket.Get<Session>("Session");
			var (account, location) = session != null
				? await session.PrepareConnectionInfoAsync(correlationID, cancellationToken, logger).ConfigureAwait(false)
				: ("Visitor", "Unknown");
			websocket.Set("AccountInfo", account);
			websocket.Set("LocationInfo", location);
		}

		/// <summary>
		/// Gets the information of the connection
		/// </summary>
		/// <param name="websocket"></param>
		/// <param name="session"></param>
		/// <returns></returns>
		public static string GetConnectionInfo(this ManagedWebSocket websocket, Session session = null)
		{
			session = session ?? websocket.Get<Session>("Session");
			var account = websocket.Get("AccountInfo", "Visitor");
			var location = websocket.Get("LocationInfo", "Unknown");
			return $"- Account: {account} {session?.GetConnectionInfo(websocket.Headers)}\r\n- Location: {location} - WebSocket: {websocket.ID} @ {websocket.RemoteEndPoint}";
		}
	}
}