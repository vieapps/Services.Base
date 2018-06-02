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
	/// Helper extension methods for working with WAMP
	/// </summary>
	public static partial class WAMPConnections
	{

		#region Properties
		/// <summary>
		/// Gets the incomming channel of the WAMP router
		/// </summary>
		public static IWampChannel IncommingChannel { get; internal set; } = null;

		/// <summary>
		/// Gets the session's identity of the incomming channel of the WAMP router
		/// </summary>
		public static long IncommingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the outgoing channel of the WAMP router
		/// </summary>
		public static IWampChannel OutgoingChannel { get; internal set; } = null;

		/// <summary>
		/// Gets the session's identity of the outgoing channel of the WAMP router
		/// </summary>
		public static long OutgoingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the state that determines that the WAMP channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem { get; internal set; } = false;

		static WebSocket StatisticsWebSocket { get; set; }

		static ManagedWebSocket StatisticsWebSocketConnection { get; set; }

		/// <summary>
		/// Gets information of WAMP router
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
		/// Gets information of WAMP router
		/// </summary>
		/// <returns></returns>
		public static string GetRouterStrInfo()
		{
			var routerInfo = WAMPConnections.GetRouterInfo();
			return $"{routerInfo.Item1}{(routerInfo.Item1.EndsWith("/") ? "" : "/")}{routerInfo.Item2}";
		}
		#endregion

		#region Open & ReOpen
		/// <summary>
		/// Opens a channel to the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			var routerInfo = WAMPConnections.GetRouterInfo();
			var address = routerInfo.Item1;
			var realm = routerInfo.Item2;
			var useJsonChannel = routerInfo.Item3;

			var wampChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			if (onConnectionEstablished != null)
				wampChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				wampChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				wampChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await wampChannel.Open().ConfigureAwait(false);
			return wampChannel;
		}

		/// <summary>
		/// Reopens a channel to the WAMP router
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="cancellationToken"></param>
		/// <param name="tracker"></param>
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
		/// Opens the incomming channel to the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
			=> WAMPConnections.IncommingChannel ?? (WAMPConnections.IncommingChannel = await WAMPConnections.OpenAsync(
					(sender, args) =>
					{
						WAMPConnections.IncommingChannelSessionID = args.SessionId;
						WAMPConnections.ChannelsAreClosedBySystem = false;
						onConnectionEstablished?.Invoke(sender, args);
					},
					onConnectionBroken,
					onConnectionError
				));

		/// <summary>
		/// Opens the outgoging channel to the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
			=> WAMPConnections.OutgoingChannel ?? (WAMPConnections.OutgoingChannel = await WAMPConnections.OpenAsync(
					(sender, args) =>
					{
						WAMPConnections.OutgoingChannelSessionID = args.SessionId;
						WAMPConnections.ChannelsAreClosedBySystem = false;
						onConnectionEstablished?.Invoke(sender, args);
					},
					onConnectionBroken,
					onConnectionError
				));
		#endregion

		#region Close channels
		/// <summary>
		/// Closes the incomming channel of the WAMP router
		/// </summary>
		/// <param name="message">The message to send to WAMP router before closing the channel</param>
		public static void CloseIncomingChannel(string message = null)
		{
			if (WAMPConnections.IncommingChannel != null)
				try
				{
					WAMPConnections.IncommingChannel.Close(message ?? "Disconnected", new GoodbyeDetails());
					WAMPConnections.IncommingChannel = null;
					WAMPConnections.IncommingChannelSessionID = 0;
				}
				catch { }
		}

		/// <summary>
		/// Closes the outgoing channel of the WAMP router
		/// </summary>
		/// <param name="message">The message to send to WAMP router before closing the channel</param>
		public static void CloseOutgoingChannel(string message = null)
		{
			if (WAMPConnections.OutgoingChannel != null)
				try
				{
					WAMPConnections.OutgoingChannel.Close(message ?? "Disconnected", new GoodbyeDetails());
					WAMPConnections.OutgoingChannel = null;
					WAMPConnections.OutgoingChannelSessionID = 0;
				}
				catch { }
		}

		/// <summary>
		/// Closes all WAMP channels
		/// </summary>
		public static void CloseChannels()
		{
			WAMPConnections.ChannelsAreClosedBySystem = true;
			WAMPConnections.CloseIncomingChannel();
			WAMPConnections.CloseOutgoingChannel();
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
			async Task sendAsync()
			{
				await WAMPConnections.StatisticsWebSocketConnection.SendAsync(new JObject
				{
					{ "Command", "Update" },
					{ "SessionID", sessionID },
					{ "Name", name },
					{ "Description", description }
				}.ToString(Formatting.None), true).ConfigureAwait(false);
			}

			if (WAMPConnections.StatisticsWebSocket == null)
				WAMPConnections.StatisticsWebSocket = new WebSocket(null, null, CancellationToken.None);

			if (WAMPConnections.StatisticsWebSocketConnection == null)
			{
				var uri = new Uri(WAMPConnections.GetRouterStrInfo());
				WAMPConnections.StatisticsWebSocket.Connect($"{uri.Scheme}://{uri.Host}:56429/", socket =>
				{
					WAMPConnections.StatisticsWebSocketConnection = socket;
					Task.Run(() => sendAsync()).ConfigureAwait(false);
				}, null);
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
			IService service = null;
			if (!string.IsNullOrWhiteSpace(name) && !WAMPConnections.Services.TryGetValue(name, out service))
			{
				await WAMPConnections.OpenOutgoingChannelAsync().ConfigureAwait(false);
				if (!WAMPConnections.Services.TryGetValue(name, out service))
				{
					service = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
					WAMPConnections.Services.TryAdd(name, service);
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
		public static async Task<JObject> CallServiceAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var service = await WAMPConnections.GetServiceAsync(requestInfo != null && !string.IsNullOrWhiteSpace(requestInfo.ServiceName) ? requestInfo.ServiceName : "unknown").ConfigureAwait(false);
			return await service.ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
		}
		#endregion

	}
}