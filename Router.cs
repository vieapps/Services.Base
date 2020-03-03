#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using WampSharp.Binding;
using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.WebSockets;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Helper extension methods for working with API Gateway Router
	/// </summary>
	public static class Router
	{

		#region Properties
		/// <summary>
		/// Gets the incoming channel of the API Gateway Router
		/// </summary>
		public static IWampChannel IncomingChannel { get; internal set; }

		/// <summary>
		/// Gets the session's identity of the incoming channel of the API Gateway Router
		/// </summary>
		public static long IncomingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the outgoing channel of the API Gateway Router
		/// </summary>
		public static IWampChannel OutgoingChannel { get; internal set; }

		/// <summary>
		/// Gets the session's identity of the outgoing channel of the API Gateway Router
		/// </summary>
		public static long OutgoingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the state that determines that the API Gateway Router channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem { get; internal set; } = false;

		static WebSocket StatisticsWebSocket { get; set; }

		static string StatisticsWebSocketState { get; set; }

		/// <summary>
		/// Gets information of API Gateway Router
		/// </summary>
		/// <returns></returns>
		public static Tuple<string, string, bool> GetRouterInfo()
			=> new Tuple<string, string, bool>
			(
				UtilityService.GetAppSetting("Router:Uri", "ws://127.0.0.1:16429/"),
				UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm"),
				"json".IsEquals(UtilityService.GetAppSetting("Router:ChannelsMode", "MessagePack"))
			);

		/// <summary>
		/// Gets information of API Gateway Router
		/// </summary>
		/// <returns></returns>
		public static string GetRouterStrInfo()
		{
			var routerInfo = Router.GetRouterInfo();
			return $"{routerInfo.Item1}{(routerInfo.Item1.EndsWith("/") ? "" : "/")}{routerInfo.Item2}";
		}
		#endregion

		#region Open & ReOpen channels
		/// <summary>
		/// Opens a channel to the API Gateway Router
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenAsync(
			this IWampChannel wampChannel,
			CancellationToken cancellationToken = default,
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onConnectionError = null
		)
		{
			// asisgn event handler
			if (onConnectionEstablished != null)
				wampChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				wampChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				wampChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			// open the channel
			await wampChannel.Open().WithCancellationToken(cancellationToken).ConfigureAwait(false);
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
		public static void ReOpen(this IWampChannel wampChannel, CancellationToken cancellationToken = default, Action<string, Exception> tracker = null, string prefix = null, int awatingTimes = 0)
		{
			using (var reconnector = new WampChannelReconnector(wampChannel, async () =>
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
					await wampChannel.OpenAsync(cancellationToken).ConfigureAwait(false);
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Reconnected", null);
				}
				catch (Exception ex)
				{
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Reconnect error: {ex.Message}", ex is System.Net.WebSockets.WebSocketException || ex is ArgumentException || ex is OperationCanceledException ? null : ex);
				}
			}))
			{
				reconnector.Start();
			}
		}

		/// <summary>
		/// Opens a channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<IWampChannel> OpenAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onConnectionError = null,
			CancellationToken cancellationToken = default
		)
		{
			var routerInfo = Router.GetRouterInfo();
			var address = routerInfo.Item1;
			var realm = routerInfo.Item2;
			var useJsonChannel = routerInfo.Item3;

			var wampChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			return wampChannel.OpenAsync(cancellationToken, onConnectionEstablished, onConnectionBroken, onConnectionError);
		}

		/// <summary>
		/// Opens the incoming channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenIncomingChannelAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onConnectionError = null,
			CancellationToken cancellationToken = default
		)
			=> Router.IncomingChannel ?? (Router.IncomingChannel = await Router.OpenAsync(
				(sender, args) =>
				{
					Router.IncomingChannelSessionID = args.SessionId;
					Router.ChannelsAreClosedBySystem = false;
					onConnectionEstablished?.Invoke(sender, args);
				},
				onConnectionBroken,
				onConnectionError,
				cancellationToken
			).ConfigureAwait(false));

		/// <summary>
		/// Opens the outgoging channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to fire when the connection is established</param>
		/// <param name="onConnectionBroken">The action to fire when the connection is broken</param>
		/// <param name="onConnectionError">The action to fire when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenOutgoingChannelAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onConnectionError = null,
			CancellationToken cancellationToken = default
		)
			=> Router.OutgoingChannel ?? (Router.OutgoingChannel = await Router.OpenAsync(
				(sender, args) =>
				{
					Router.OutgoingChannelSessionID = args.SessionId;
					Router.ChannelsAreClosedBySystem = false;
					onConnectionEstablished?.Invoke(sender, args);
				},
				onConnectionBroken,
				onConnectionError,
				cancellationToken
			).ConfigureAwait(false));
		#endregion

		#region Close channels
		/// <summary>
		/// Closes the incoming channel of the API Gateway Router
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static async Task CloseIncomingChannelAsync(string message = null, Action<Exception> onError = null)
		{
			try
			{
				await (Router.IncomingChannel != null
					? Router.IncomingChannel.Close(message ?? "Disconnected", new GoodbyeDetails { Message = message ?? "Disconnected" })
					: Task.CompletedTask
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
			finally
			{
				Router.IncomingChannel = null;
				Router.IncomingChannelSessionID = 0;
			}
		}

		/// <summary>
		/// Closes the outgoing channel of the API Gateway Router
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static async Task CloseOutgoingChannelAsync(string message = null, Action<Exception> onError = null)
		{
			try
			{
				await (Router.OutgoingChannel != null
					? Router.OutgoingChannel.Close(message ?? "Disconnected", new GoodbyeDetails { Message = message ?? "Disconnected" })
					: Task.CompletedTask
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
			finally
			{
				Router.OutgoingChannel = null;
				Router.OutgoingChannelSessionID = 0;
			}
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
		public static async Task UpdateAsync(this IWampChannel wampChannel, long sessionID, string name, string description)
		{
			if (Router.StatisticsWebSocket == null)
			{
				Router.StatisticsWebSocket = new WebSocket(null, null, CancellationToken.None);
				Router.StatisticsWebSocketState = "initializing";
			}

			if (Router.StatisticsWebSocketState == null || Router.StatisticsWebSocketState == "initializing")
			{
				Router.StatisticsWebSocketState = "connecting";
				var uri = new Uri(Router.GetRouterStrInfo());
				Router.StatisticsWebSocket.Connect($"{uri.Scheme}://{uri.Host}:56429/", websocket => Router.StatisticsWebSocketState = "connected", exception => Router.StatisticsWebSocketState = "closed");
			}

			while (Router.StatisticsWebSocketState == null || Router.StatisticsWebSocketState == "initializing" || Router.StatisticsWebSocketState == "connecting")
				await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);

			if (Router.StatisticsWebSocketState == "connected")
				await Router.StatisticsWebSocket.GetWebSockets().First().SendAsync(new JObject
				{
					{ "Command", "Update" },
					{ "SessionID", sessionID },
					{ "Name", name },
					{ "Description", description }
				}.ToString(Formatting.None), true).ConfigureAwait(false);
		}

		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static void Update(this IWampChannel wampChannel, long sessionID, string name, string description)
			=> Task.Run(() => wampChannel.UpdateAsync(sessionID, name, description)).ConfigureAwait(false);
		#endregion

		#region Connect & Disconnect
		/// <summary>
		/// Connects to API Gateway Router
		/// </summary>
		/// <param name="onIncomingConnectionEstablished">The action to fire when the incomming connection is established</param>
		/// <param name="onIncomingConnectionBroken">The action to fire when the incomming connection is broken</param>
		/// <param name="onIncomingConnectionError">The action to fire when the incomming connection got any error</param>
		/// <param name="onOutgoingConnectionEstablished">The action to fire when the outgoing connection is established</param>
		/// <param name="onOutgoingConnectionBroken">The action to fire when the outgoing connection is broken</param>
		/// <param name="onOutgoingConnectionError">The action to fire when the outgoing connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onError">The action to fire when got any error</param>
		/// <returns></returns>
		public static async Task ConnectAsync(
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null,
			CancellationToken cancellationToken = default,
			Action<Exception> onError = null
		)
		{
			Router.ChannelsAreClosedBySystem = false;
			try
			{
				if (Router.IncomingChannel == null)
					Router.IncomingChannel = await Router.OpenAsync(
						(sender, args) =>
						{
							Router.IncomingChannelSessionID = args.SessionId;
							onIncomingConnectionEstablished?.Invoke(sender, args);
						},
						onIncomingConnectionBroken,
						onIncomingConnectionError,
						cancellationToken
					).ConfigureAwait(false);
				if (Router.OutgoingChannel == null)
					Router.OutgoingChannel = await Router.OpenAsync(
						(sender, args) =>
						{
							Router.OutgoingChannelSessionID = args.SessionId;
							onOutgoingConnectionEstablished?.Invoke(sender, args);
						},
						onOutgoingConnectionBroken,
						onOutgoingConnectionError,
						cancellationToken
					).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
				if (onError == null)
					throw ex;
			}
		}

		/// <summary>
		/// Disconnects from API Gateway Router (means close all WAMP channels)
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static Task DisconnectAsync(string message = null, Action<Exception> onError = null)
		{
			Router.ChannelsAreClosedBySystem = true;
			return Task.WhenAll(Router.CloseIncomingChannelAsync(message, onError), Router.CloseOutgoingChannelAsync(message, onError));
		}

		/// <summary>
		/// Disconnects from API Gateway Router and close all WAMP channels
		/// </summary>
		/// <param name="waitingTimes">Times (miliseconds) for waiting to disconnect</param>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static void Disconnect(int waitingTimes = 1234, string message = null, Action<Exception> onError = null)
			=> Router.DisconnectAsync(message, onError).Wait(waitingTimes > 0 ? waitingTimes : 1234);
		#endregion

		#region Get a service
		internal static ConcurrentDictionary<string, IService> Services { get; } = new ConcurrentDictionary<string, IService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets a service by name
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <returns></returns>
		public static IService GetService(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ServiceNotFoundException($"The service name is null or empty");

			if (!Router.Services.TryGetValue(name, out var service))
			{
				service = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
				Router.Services.Add(name, service);
			}

			return service ?? throw new ServiceNotFoundException($"The service \"{name.ToLower()}\" is not found");
		}

		internal static ConcurrentDictionary<string, IUniqueService> UniqueServices { get; } = new ConcurrentDictionary<string, IUniqueService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets an unique service by name (means a service at a specified node)
		/// </summary>
		/// <param name="name">The string that presents the unique name of a service</param>
		/// <returns></returns>
		public static IUniqueService GetUniqueService(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ServiceNotFoundException($"The unique service name is null or empty");

			if (!Router.UniqueServices.TryGetValue(name, out var service))
			{
				service = Router.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IUniqueService>(ProxyInterceptor.Create(name));
				Router.UniqueServices.Add(name, service);
			}

			return service ?? throw new ServiceNotFoundException($"The service with unique URI \"{name.ToLower()}\" is not found");
		}
		#endregion

	}
}