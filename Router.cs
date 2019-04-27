#region Related components
using System;
using System.IO;
using System.Net;
using System.Web;
using System.Linq;
using System.Dynamic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using WampSharp.Binding;
using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Core.Contracts;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.WebSockets;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Helper extension methods for working with API Gateway Router
	/// </summary>
	public static partial class RouterConnections
	{

		#region Properties
		/// <summary>
		/// Gets the incoming channel of the API Gateway Router
		/// </summary>
		public static IWampChannel IncomingChannel { get; internal set; } = null;

		/// <summary>
		/// Gets the session's identity of the incoming channel of the API Gateway Router
		/// </summary>
		public static long IncomingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the outgoing channel of the API Gateway Router
		/// </summary>
		public static IWampChannel OutgoingChannel { get; internal set; } = null;

		/// <summary>
		/// Gets the session's identity of the outgoing channel of the API Gateway Router
		/// </summary>
		public static long OutgoingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the state that determines that the API Gateway Router channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem { get; internal set; } = false;

		static WebSocket StatisticsWebSocket { get; set; }

		static ManagedWebSocket StatisticsWebSocketConnection { get; set; }

		/// <summary>
		/// Gets information of API Gateway Router
		/// </summary>
		/// <returns></returns>
		public static Tuple<string, string, bool> GetRouterInfo()
			=> new Tuple<string, string, bool>
			(
				UtilityService.GetAppSetting("Router:Uri", "ws://127.0.0.1:16429/"),
				UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm"),
				"json".IsEquals(UtilityService.GetAppSetting("Router:ChannelsMode", "MsgPack"))
			);

		/// <summary>
		/// Gets information of API Gateway Router
		/// </summary>
		/// <returns></returns>
		public static string GetRouterStrInfo()
		{
			var routerInfo = RouterConnections.GetRouterInfo();
			return $"{routerInfo.Item1}{(routerInfo.Item1.EndsWith("/") ? "" : "/")}{routerInfo.Item2}";
		}
		#endregion

		#region Open & ReOpen
		/// <summary>
		/// Opens a channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// prepare
			var routerInfo = RouterConnections.GetRouterInfo();
			var address = routerInfo.Item1;
			var realm = routerInfo.Item2;
			var useJsonChannel = routerInfo.Item3;

			var wampChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			// asisgn event handler
			if (onConnectionEstablished != null)
				wampChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				wampChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				wampChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			// open the channel
			await wampChannel.Open().WithCancellationToken(cancellationToken).ConfigureAwait(false);

			// return the connected channel
			return wampChannel;
		}

		/// <summary>
		/// Reopens a channel to the API Gateway Router
		/// </summary>
		/// <param name="wampChannel">The WAMP channel to re-open</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="tracker">The tracker to track the logs</param>
		/// <param name="prefix"></param>
		/// <param name="awatingTimes"></param>
		public static void ReOpen(this IWampChannel wampChannel, CancellationToken cancellationToken = default(CancellationToken), Action<string, Exception> tracker = null, string prefix = null, int awatingTimes = 0)
		{
			var reconnector = new WampChannelReconnector(wampChannel, async () =>
			{
				try
				{
					await Task.Delay(awatingTimes > 0 ? awatingTimes : UtilityService.GetRandomNumber(1234, 2345), cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Cancelled", ex is OperationCanceledException ? null : ex);
					return;
				}

				try
				{
					await wampChannel.Open().WithCancellationToken(cancellationToken).ConfigureAwait(false);
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Reconnected", null);
				}
				catch (Exception ex)
				{
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Reconnect error: {ex.Message}", ex is System.Net.WebSockets.WebSocketException || ex is ArgumentException || ex is OperationCanceledException ? null : ex);
				}
			});

			using (reconnector)
			{
				reconnector.Start();
			}
		}
		#endregion

		#region Open channels
		/// <summary>
		/// Opens the incoming channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null, CancellationToken cancellationToken = default(CancellationToken))
			=> RouterConnections.IncomingChannel ?? (RouterConnections.IncomingChannel = await RouterConnections.OpenAsync(
					(sender, args) =>
					{
						RouterConnections.IncomingChannelSessionID = args.SessionId;
						RouterConnections.ChannelsAreClosedBySystem = false;
						onConnectionEstablished?.Invoke(sender, args);
					},
					onConnectionBroken,
					onConnectionError,
					cancellationToken
				));

		/// <summary>
		/// Opens the outgoging channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null, CancellationToken cancellationToken = default(CancellationToken))
			=> RouterConnections.OutgoingChannel ?? (RouterConnections.OutgoingChannel = await RouterConnections.OpenAsync(
					(sender, args) =>
					{
						RouterConnections.OutgoingChannelSessionID = args.SessionId;
						RouterConnections.ChannelsAreClosedBySystem = false;
						onConnectionEstablished?.Invoke(sender, args);
					},
					onConnectionBroken,
					onConnectionError,
					cancellationToken
				));
		#endregion

		#region Close channels
		/// <summary>
		/// Closes the incoming channel of the API Gateway Router
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		public static void CloseIncomingChannel(string message = null)
		{
			if (RouterConnections.IncomingChannel != null)
				try
				{
					RouterConnections.IncomingChannel.Close(message ?? "Disconnected", new GoodbyeDetails());
					RouterConnections.IncomingChannel = null;
					RouterConnections.IncomingChannelSessionID = 0;
				}
				catch { }
		}

		/// <summary>
		/// Closes the outgoing channel of the API Gateway Router
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		public static void CloseOutgoingChannel(string message = null)
		{
			if (RouterConnections.OutgoingChannel != null)
				try
				{
					RouterConnections.OutgoingChannel.Close(message ?? "Disconnected", new GoodbyeDetails());
					RouterConnections.OutgoingChannel = null;
					RouterConnections.OutgoingChannelSessionID = 0;
				}
				catch { }
		}

		/// <summary>
		/// Closes all API Gateway Router channels
		/// </summary>
		public static void CloseChannels()
		{
			RouterConnections.ChannelsAreClosedBySystem = true;
			RouterConnections.CloseIncomingChannel();
			RouterConnections.CloseOutgoingChannel();
		}
		#endregion

		#region Update channels
		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static void Update(this IWampChannel wampChannel, long sessionID, string name, string description)
		{
			if (RouterConnections.StatisticsWebSocket == null)
				RouterConnections.StatisticsWebSocket = new WebSocket(null, null, CancellationToken.None)
				{
					OnConnectionEstablished = websocket => RouterConnections.StatisticsWebSocketConnection = websocket,
					OnConnectionBroken = websocket => RouterConnections.StatisticsWebSocketConnection = null
				};

			async Task sendAsync()
			{
				await RouterConnections.StatisticsWebSocketConnection.SendAsync(new JObject
				{
					{ "Command", "Update" },
					{ "SessionID", sessionID },
					{ "Name", name },
					{ "Description", description }
				}.ToString(Formatting.None), true).ConfigureAwait(false);
			}

			if (RouterConnections.StatisticsWebSocketConnection == null)
			{
				var uri = new Uri(RouterConnections.GetRouterStrInfo());
				RouterConnections.StatisticsWebSocket.Connect($"{uri.Scheme}://{uri.Host}:56429/", websocket => Task.Run(() => sendAsync()).ConfigureAwait(false), null);
			}
			else
				Task.Run(() => sendAsync()).ConfigureAwait(false);
		}
		#endregion

		#region Call services
		internal static ConcurrentDictionary<string, IService> Services { get; } = new ConcurrentDictionary<string, IService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets a service by name
		/// </summary>
		/// <param name="name">The string that presents name of a service</param>
		/// <returns></returns>
		public static async Task<IService> GetServiceAsync(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			if (!RouterConnections.Services.TryGetValue(name, out IService service))
			{
				await RouterConnections.OpenOutgoingChannelAsync().ConfigureAwait(false);
				if (!RouterConnections.Services.TryGetValue(name, out service))
				{
					service = RouterConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
					RouterConnections.Services.TryAdd(name, service);
				}
			}

			return service ?? throw new ServiceNotFoundException($"The service \"net.vieapps.services.{name?.ToLower()}\" is not found");
		}

		/// <summary>
		/// Calls a service to process a request
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<JToken> CallServiceAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var service = await RouterConnections.GetServiceAsync(requestInfo != null && !string.IsNullOrWhiteSpace(requestInfo.ServiceName) ? requestInfo.ServiceName : "unknown").ConfigureAwait(false);
			return await service.ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
		}
		#endregion

	}
}