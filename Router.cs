#region Related components
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Client;
using WampSharp.V2.Core.Contracts;
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
		/// <param name="wampChannel">The channel to open</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenAsync(
			this IWampChannel wampChannel,
			CancellationToken cancellationToken = default,
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onConnectionError = null
		)
		{
			// asisgn event handlers
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
		/// Opens a channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
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

			var wampChannel = useJsonChannel ? new DefaultWampChannelFactory().CreateJsonChannel(address, realm) : new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);
			return wampChannel.OpenAsync(cancellationToken, onConnectionEstablished, onConnectionBroken, onConnectionError);
		}

		/// <summary>
		/// Reopens a channel to the API Gateway Router
		/// </summary>
		/// <param name="wampChannel">The channel to re-open</param>
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
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Canceled", ex is OperationCanceledException ? null : ex);
					return;
				}

				try
				{
					tracker?.Invoke($"{(string.IsNullOrWhiteSpace(prefix) ? "" : $"[{prefix}] => ")}Reconnecting", null);
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
		/// Opens the incoming channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
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
					onConnectionEstablished?.Invoke(sender, args);
				},
				(sender, args) =>
				{
					Router.IncomingChannelSessionID = 0;
					onConnectionBroken?.Invoke(sender, args);
				},
				onConnectionError,
				cancellationToken
			).ConfigureAwait(false));

		/// <summary>
		/// Opens the outgoging channel to the API Gateway Router
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
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
					onConnectionEstablished?.Invoke(sender, args);
				},
				(sender, args) =>
				{
					Router.OutgoingChannelSessionID = 0;
					onConnectionBroken?.Invoke(sender, args);
				},
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
		static void ConnectStatisticsWebSocket(ILogger logger = null)
		{
			if (Router.StatisticsWebSocketState == null || Router.StatisticsWebSocketState == "initializing" || Router.StatisticsWebSocketState == "closed")
			{
				Router.StatisticsWebSocketState = "connecting";
				var uri = new Uri(Router.GetRouterStrInfo());
				Router.StatisticsWebSocket.Connect
				(
					$"{uri.Scheme}://{uri.Host}:56429/",
					websocket => Router.StatisticsWebSocketState = "connected",
					exception =>
					{
						logger?.LogError($"Cannot connect to statistic websocket => {exception.Message}", exception);
						Router.StatisticsWebSocketState = "closed";
						Task.Run(async () =>
						{
							await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
							Router.ConnectStatisticsWebSocket(logger);
						}).ConfigureAwait(false);
					}
				);
			}
		}

		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static async Task UpdateAsync(this IWampChannel wampChannel, long sessionID, string name, string description, ILogger logger = null)
		{
			if (Router.StatisticsWebSocket == null)
			{
				Router.StatisticsWebSocket = new WebSocket(null, null, CancellationToken.None);
				Router.StatisticsWebSocketState = "initializing";
			}

			Router.ConnectStatisticsWebSocket(logger);
			while (Router.StatisticsWebSocketState == null || Router.StatisticsWebSocketState == "initializing" || Router.StatisticsWebSocketState == "connecting")
				await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);

			if (Router.StatisticsWebSocketState == "connected" && Router.StatisticsWebSocket.GetWebSockets().Any())
				try
				{
					await Router.StatisticsWebSocket.GetWebSockets().First().SendAsync(new JObject
					{
						{ "Command", "Update" },
						{ "SessionID", sessionID },
						{ "Name", name },
						{ "Description", description }
					}.ToString(Formatting.None), true).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					logger?.LogError($"Cannot update statistic websocket info => {ex.Message}", ex);
				}
		}

		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static void Update(this IWampChannel wampChannel, long sessionID, string name, string description, ILogger logger = null)
			=> wampChannel.UpdateAsync(sessionID, name, description, logger).Run();
		#endregion

		#region Connect & Disconnect
		/// <summary>
		/// Connects to API Gateway Router
		/// </summary>
		/// <param name="onIncomingConnectionEstablished">The action to run when the incomming connection is established</param>
		/// <param name="onIncomingConnectionBroken">The action to run when the incomming connection is broken</param>
		/// <param name="onIncomingConnectionError">The action to run when the incomming connection got any error</param>
		/// <param name="onOutgoingConnectionEstablished">The action to run when the outgoing connection is established</param>
		/// <param name="onOutgoingConnectionBroken">The action to run when the outgoing connection is broken</param>
		/// <param name="onOutgoingConnectionError">The action to run when the outgoing connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onError">The action to run when got any error</param>
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
			try
			{
				await Task.WhenAll
				(
					Router.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, cancellationToken),
					Router.OpenOutgoingChannelAsync(onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, cancellationToken)
				).ConfigureAwait(false);
				Router.ChannelsAreClosedBySystem = false;
			}
			catch (Exception ex)
			{
				Router.ChannelsAreClosedBySystem = true;
				onError?.Invoke(ex);
				if (onError == null)
					throw;
			}
		}

		/// <summary>
		/// Connects to API Gateway Router
		/// </summary>
		/// <param name="onIncomingConnectionEstablished">The action to run when the incomming connection is established</param>
		/// <param name="onIncomingConnectionBroken">The action to run when the incomming connection is broken</param>
		/// <param name="onIncomingConnectionError">The action to run when the incomming connection got any error</param>
		/// <param name="onOutgoingConnectionEstablished">The action to run when the outgoing connection is established</param>
		/// <param name="onOutgoingConnectionBroken">The action to run when the outgoing connection is broken</param>
		/// <param name="onOutgoingConnectionError">The action to run when the outgoing connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		public static void Connect(
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null,
			CancellationToken cancellationToken = default,
			Action<Exception> onError = null
		)
			=> Router.ConnectAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, cancellationToken, onError).Run();

		/// <summary>
		/// Disconnects from API Gateway Router (means close all WAMP channels)
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static Task DisconnectAsync(string message = null, Action<Exception> onError = null)
		{
			Router.ChannelsAreClosedBySystem = true;
			Router.ReconnectTimer?.Dispose();
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

		static IDisposable ReconnectTimer { get; set; }

		/// <summary>
		/// Runs the reconnect timer to re-connect when the connections were broken
		/// </summary>
		public static void RunReconnectTimer()
		{
			Router.ReconnectTimer?.Dispose();
			Router.ReconnectTimer = System.Reactive.Linq.Observable.Timer(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(13)).Subscribe(_ =>
			{
				if (Router.IncomingChannel != null && (!Router.ChannelsAreClosedBySystem || Router.IncomingChannelSessionID < 1))
					Router.IncomingChannel.ReOpen();
				if (Router.OutgoingChannel != null && (!Router.ChannelsAreClosedBySystem || Router.OutgoingChannelSessionID < 1))
					Router.OutgoingChannel.ReOpen();
			});
		}
		#endregion

		#region Get & Call a service
		internal static ConcurrentDictionary<string, IService> Services { get; } = new ConcurrentDictionary<string, IService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets a service instance by name
		/// </summary>
		/// <param name="name">The string that presents the name of a service</param>
		/// <returns></returns>
		public static IService GetService(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ServiceNotFoundException("The service name is null or empty");

			if (!Router.Services.TryGetValue(name, out var service))
			{
				service = Router.OutgoingChannel?.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
				if (service != null)
					Router.Services.TryAdd(name, service);
			}

			return service ?? throw new ServiceNotFoundException($"The service \"{name.ToLower()}\" is not found");
		}

		/// <summary>
		/// Gets a service instance
		/// </summary>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static IService GetService(this RequestInfo requestInfo)
			=> Router.GetService(requestInfo?.ServiceName);

		internal static ConcurrentDictionary<string, IUniqueService> UniqueServices { get; } = new ConcurrentDictionary<string, IUniqueService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets an unique service instance by name (means a service at a specified node)
		/// </summary>
		/// <param name="name">The string that presents the unique name of a service</param>
		/// <returns></returns>
		public static IUniqueService GetUniqueService(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ServiceNotFoundException("The unique service name is null or empty");

			if (!Router.UniqueServices.TryGetValue(name, out var service))
			{
				service = Router.OutgoingChannel?.RealmProxy.Services.GetCalleeProxy<IUniqueService>(ProxyInterceptor.Create(name));
				if (service != null)
					Router.UniqueServices.TryAdd(name, service);
			}

			return service ?? throw new ServiceNotFoundException($"The service with unique URI \"{name.ToLower()}\" is not found");
		}

		/// <summary>
		/// Calls a business service
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onStart">The action to run when start</param>
		/// <param name="onSuccess">The action to run when success</param>
		/// <param name="onError">The action to run when got an error</param>
		/// <param name="tracker">The tracker to wirte debuging log of all steps</param>
		/// <param name="jsonFormat">The format of outputing json</param>
		/// <returns>A <see cref="JObject">JSON</see> object that presents the results of the business service</returns>
		public static async Task<JToken> CallServiceAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default, Action<RequestInfo> onStart = null, Action<RequestInfo, JToken> onSuccess = null, Action<RequestInfo, Exception> onError = null, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
		{
			var stopwatch = Stopwatch.StartNew();
			var objectName = requestInfo.ServiceName;
			try
			{
				onStart?.Invoke(requestInfo);
				tracker?.Invoke($"Start call service {requestInfo.Verb} {requestInfo.GetURI()} - {requestInfo.Session.AppName} ({requestInfo.Session.AppPlatform}) @ {requestInfo.Session.IP}", null);

				var service = Router.GetService(requestInfo.ServiceName);
				var json = service != null ? await service.ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false) : null;
				onSuccess?.Invoke(requestInfo, json);

				tracker?.Invoke("Call service successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				Router.IncomingChannel?.ReOpen(cancellationToken);
				Router.OutgoingChannel?.ReOpen(cancellationToken);
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);

				try
				{
					var service = Router.GetService(requestInfo.ServiceName);
					var json = service != null ? await service.ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false) : null;
					onSuccess?.Invoke(requestInfo, json);

					tracker?.Invoke("Re-call service successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
					return json;
				}
				catch (Exception)
				{
					throw;
				}
			}
			catch (Exception ex)
			{
				onError?.Invoke(requestInfo, ex);
				throw;
			}
			finally
			{
				stopwatch.Stop();
				tracker?.Invoke($"Call service finished in {stopwatch.GetElapsedTimes()}", null);
			}
		}

		internal static ConcurrentDictionary<string, ISyncableService> SyncableServices { get; } = new ConcurrentDictionary<string, ISyncableService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets a syncable service instance by name
		/// </summary>
		/// <param name="name">The string that presents the name of a syncable service</param>
		/// <returns></returns>
		public static ISyncableService GetSyncableService(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ServiceNotFoundException("The service name is null or empty");

			if (!Router.SyncableServices.TryGetValue(name, out var service))
			{
				service = Router.OutgoingChannel?.RealmProxy.Services.GetCalleeProxy<ISyncableService>(ProxyInterceptor.Create(name));
				if (service != null)
					Router.SyncableServices.TryAdd(name, service);
			}

			return service ?? throw new ServiceNotFoundException($"The service \"{name.ToLower()}\" is not found");
		}

		/// <summary>
		/// Gets and calls a syncable service for synchronizing data
		/// </summary>
		/// <param name="requestInfo">The requesting information</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onStart">The action to run when start</param>
		/// <param name="onSuccess">The action to run when success</param>
		/// <param name="onError">The action to run when got an error</param>
		/// <param name="tracker">The tracker to wirte debuging log of all steps</param>
		/// <param name="jsonFormat">The format of outputing json</param>
		/// <returns>A <see cref="JObject">JSON</see> object that presents the results of the call</returns>
		public static async Task<JToken> SyncAsync(this RequestInfo requestInfo, CancellationToken cancellationToken = default, Action<RequestInfo> onStart = null, Action<RequestInfo, JToken> onSuccess = null, Action<RequestInfo, Exception> onError = null, Action<string, Exception> tracker = null, Formatting jsonFormat = Formatting.None)
		{
			var stopwatch = Stopwatch.StartNew();
			var objectName = requestInfo.ServiceName;
			try
			{
				onStart?.Invoke(requestInfo);
				tracker?.Invoke($"Start call service [for synchronizing] {requestInfo.Verb} {requestInfo.GetURI()} - {requestInfo.Session.AppName} ({requestInfo.Session.AppPlatform}) @ {requestInfo.Session.IP}", null);

				var service = Router.GetSyncableService(requestInfo.ServiceName);
				var json = service != null ? await service.SyncAsync(requestInfo, cancellationToken).ConfigureAwait(false) : null;
				onSuccess?.Invoke(requestInfo, json);

				tracker?.Invoke("Call service [for synchronizing] successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				Router.IncomingChannel?.ReOpen(cancellationToken);
				Router.OutgoingChannel?.ReOpen(cancellationToken);
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);

				try
				{
					var service = Router.GetSyncableService(requestInfo.ServiceName);
					var json = service != null ? await service.SyncAsync(requestInfo, cancellationToken).ConfigureAwait(false) : null;
					onSuccess?.Invoke(requestInfo, json);

					tracker?.Invoke("Re-call service [for synchronizing] successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
					return json;
				}
				catch (Exception)
				{
					throw;
				}
			}
			catch (Exception ex)
			{
				onError?.Invoke(requestInfo, ex);
				throw;
			}
			finally
			{
				stopwatch.Stop();
				tracker?.Invoke($"Call service [for synchronizing] finished in {stopwatch.GetElapsedTimes()}", null);
			}
		}
		#endregion

	}
}