#region Related components
using System;
using System.Net.Sockets;
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
	/// <summary>
	/// Extension methods for working with services in the VIEApps NGX
	/// </summary>
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
			=> (messages?? new List<string>()).Where(message => !string.IsNullOrWhiteSpace(message)).ForEachAsync((message, token) => websocket.SendAsync(message, token), cancellationToken, true, false);

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
			var account = "Visitor";
			if (!string.IsNullOrWhiteSpace(session?.User?.ID))
				try
				{
					var json = await Router.GetService("Users").ProcessRequestAsync(new RequestInfo(session, "Users", "Profile", "GET") { CorrelationID = correlationID }, cancellationToken).ConfigureAwait(false);
					account = $"{json?.Get<string>("Name") ?? "Unknown"} ({session.User.ID})";
				}
				catch (Exception ex)
				{
					account = $"Unknown ({session.User.ID})";
					logger?.LogError($"Error occurred while fetching an account profile => {ex.Message}", ex);
				}
			websocket.Set("AccountInfo", account);
			websocket.Set("LocationInfo", session != null ? await session.GetLocationAsync(correlationID, cancellationToken).ConfigureAwait(false) : "Unknown");
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
			return $"- Account: {websocket.Get("AccountInfo", "Visitor")} - Session ID: {session?.SessionID ?? "Unknown"} - Device ID: {session?.DeviceID ?? "Unknown"} - Origin: {(websocket.Headers.TryGetValue("Origin", out var origin) ? origin : session?.AppOrigin ?? "Unknown")}" + "\r\n" +
				$"- App: {session?.AppName ?? "Unknown"} @ {session?.AppPlatform ?? "Unknown"} [{session?.AppAgent ?? "Unknown"}]" + "\r\n" +
				$"- Connection IP: {session?.IP ?? "Unknown"} - Location: {websocket.Get("LocationInfo", "Unknown")} - WebSocket: {websocket.ID} @ {websocket.RemoteEndPoint}";
		}
	}
}