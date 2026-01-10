#region Related components
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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
		/// Gets the API Gateway incoming channel
		/// </summary>
		public static IWampChannel IncomingChannel { get; internal set; }

		/// <summary>
		/// Gets the API Gateway outgoing channel
		/// </summary>
		public static IWampChannel OutgoingChannel { get; internal set; }

		/// <summary>
		/// Gets the backup channel of API Gateway Router
		/// </summary>
		public static IWampChannel BackupChannel { get; internal set; }

		/// <summary>
		/// Gets the session's identity of the API Gateway incoming channel
		/// </summary>
		public static long IncomingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the session's identity of the API Gateway outgoing channel
		/// </summary>
		public static long OutgoingChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the session's identity of the API Gateway Router's backup channel
		/// </summary>
		public static long BackupChannelSessionID { get; internal set; } = 0;

		/// <summary>
		/// Gets the state that determines that the API Gateways' channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem { get; internal set; } = false;

		internal static WebSocket StatisticsWebSocket { get; } = new WebSocket
		{
			OnConnectionBroken = websocket =>
			{
				if (Router.BackupStatisticsWebSocketID.Equals(websocket.ID))
				{
					Router.BackupStatisticsWebSocketID = Guid.Empty;
					Router.BackupStatisticsWebSocketState = "closed";
				}
				else
				{
					Router.PrimaryStatisticsWebSocketID = Guid.Empty;
					Router.PrimaryStatisticsWebSocketState = "closed";
				}
			}
		};

		internal static Guid PrimaryStatisticsWebSocketID { get; set; } = Guid.Empty;

		internal static string PrimaryStatisticsWebSocketState { get; set; } = "initializing";

		internal static Guid BackupStatisticsWebSocketID { get; set; } = Guid.Empty;

		internal static string BackupStatisticsWebSocketState { get; set; } = "initializing";
		#endregion

		#region Get settings of API Gateway Router
		/// <summary>
		/// Gets settings of API Gateway Router
		/// </summary>
		/// <param name="backup">true to use backing-up router</param>
		/// <returns></returns>
		public static (string Address, string Realm, bool UseJSON) GetRouterInfo(bool backup = false)
		{
			var address = backup
				? UtilityService.GetAppSetting("Router:URI:Backup")
				: UtilityService.GetAppSetting("Router:URI", UtilityService.GetAppSetting("Router:Uri", "ws://127.0.0.1:16429/"));
			var realm = backup
				? UtilityService.GetAppSetting("Router:Realm:Backup", "VIEAppsRealm")
				: UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm");
			var useJSON = backup
				? "json".IsEquals(UtilityService.GetAppSetting("Router:Mode:Backup", "MessagePack"))
				: "json".IsEquals(UtilityService.GetAppSetting("Router:Mode", "MessagePack"));
			return (address, realm, useJSON);
		}

		/// <summary>
		/// Gets settings of API Gateway Router
		/// </summary>
		/// <param name="backup">true to use backing-up router</param>
		/// <returns></returns>
		public static string GetRouterStrInfo(bool backup = false)
		{
			var (address, realm, _) = Router.GetRouterInfo(backup);
			return $"{address}{(address.IsEndsWith("/") ? "" : "/")}{realm}";
		}

		/// <summary>
		/// Gets the state that got backup router
		/// </summary>
		/// <returns></returns>
		public static bool GotBackupRouter()
			=> Router.GetRouterInfo(true).Address != null;
		#endregion

		#region Open channels
		/// <summary>
		/// Opens a channel of the API Gateway Router
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenAsync(this IWampChannel wampChannel, CancellationToken cancellationToken = default)
		{
			await wampChannel.Open().WithCancellationToken(cancellationToken).ConfigureAwait(false);
			return wampChannel;
		}

		/// <summary>
		/// Opens a channel of the API Gateway Router
		/// </summary>
		/// <param name="wampChannel">The channel to open</param>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<IWampChannel> OpenAsync(this IWampChannel wampChannel, Action<object, WampSessionCreatedEventArgs> onConnectionEstablished, Action<object, WampSessionCloseEventArgs> onConnectionBroken, Action<object, WampConnectionErrorEventArgs> onConnectionError, CancellationToken cancellationToken = default)
		{
			wampChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>((sender, args) =>
			{
				try
				{
					onConnectionEstablished?.Invoke(sender, args);
				}
				catch { }
			});
			wampChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>((sender, args) =>
			{
				try
				{
					onConnectionBroken?.Invoke(sender, args);
				}
				catch { }
				if (!Router.ChannelsAreClosedBySystem)
				{
					using (var reconnector = new WampChannelReconnector(wampChannel, () => wampChannel.OpenAsync()))
						reconnector.Start();
				}
			});
			wampChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>((sender, args) =>
			{
				try
				{
					onConnectionError?.Invoke(sender, args);
				}
				catch { }
			});
			return wampChannel.OpenAsync(cancellationToken);
		}
		#endregion

		#region Create & Update channels
		/// <summary>
		/// Creates and opens a channel of the API Gateway Router
		/// </summary>
		/// <param name="routerInfo">The settings of API Gateway Router</param>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static Task<IWampChannel> CreateAsync(
			(string Address, string Realm, bool UseJSON) routerInfo,
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onConnectionError,
			CancellationToken cancellationToken
		)
		{
			var wampChannel = routerInfo.UseJSON
				? new DefaultWampChannelFactory().CreateJsonChannel(routerInfo.Address, routerInfo.Realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(routerInfo.Address, routerInfo.Realm);
			return wampChannel.OpenAsync(onConnectionEstablished, onConnectionBroken,	onConnectionError, cancellationToken);
		}

		static void ConnectStatisticsWebSocket(ILogger logger = null, bool isBackup = false)
		{
			var uri = new Uri(Router.GetRouterStrInfo(isBackup));
			var connect = isBackup
				? Router.BackupStatisticsWebSocketID.Equals(Guid.Empty) && (Router.BackupStatisticsWebSocketState == "initializing" || Router.BackupStatisticsWebSocketState == "closed")
				: Router.PrimaryStatisticsWebSocketID.Equals(Guid.Empty) && (Router.PrimaryStatisticsWebSocketState == "initializing" || Router.PrimaryStatisticsWebSocketState == "closed");
			if (connect)
				Router.StatisticsWebSocket.Connect
				(
					$"{uri.Scheme}://{uri.Host}:56429/",
					websocket =>
					{
						if (isBackup)
						{
							Router.BackupStatisticsWebSocketID = websocket.ID;
							Router.BackupStatisticsWebSocketState = "connected";
						}
						else
						{
							Router.PrimaryStatisticsWebSocketID = websocket.ID;
							Router.PrimaryStatisticsWebSocketState = "connected";
						}
					},
					exception =>
					{
						logger?.LogError($"Cannot connect to statistic websocket => {exception.Message}", exception);
						Router.ConnectStatisticsWebSocketAsync(logger, isBackup).Execute();
					}
				);
		}

		static async Task ConnectStatisticsWebSocketAsync(ILogger logger = null, bool isBackup = false)
		{
			await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
			Router.ConnectStatisticsWebSocket(logger, isBackup);
		}

		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static async Task UpdateAsync(this IWampChannel wampChannel, long sessionID, string name, string description, ILogger logger = null, bool isBackup = false)
		{
			Router.ConnectStatisticsWebSocket(logger, isBackup);
			var state = isBackup ? Router.BackupStatisticsWebSocketState : Router.PrimaryStatisticsWebSocketState;
			while (state == null || state == "initializing" || state == "connecting")
			{
				await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);
				state = isBackup ? Router.BackupStatisticsWebSocketState : Router.PrimaryStatisticsWebSocketState;
			}

			var websocket = Router.StatisticsWebSocket.GetWebSocket(isBackup ? Router.BackupStatisticsWebSocketID : Router.PrimaryStatisticsWebSocketID);
			if (websocket != null && websocket.State == System.Net.WebSockets.WebSocketState.Open)
				try
				{
					await websocket.SendAsync(new JObject
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
		public static void Update(this IWampChannel wampChannel, long sessionID, string name, string description, ILogger logger = null, bool isBackup = false)
			=> wampChannel.UpdateAsync(sessionID, name, description, logger, isBackup).Execute();
		#endregion

		#region Incoming channel
		/// <summary>
		/// Opens the API Gateway Router incoming channel
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenIncomingChannelAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onConnectionError,
			CancellationToken cancellationToken
		) => Router.IncomingChannel = await Router.CreateAsync(
			Router.GetRouterInfo(),
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
		).ConfigureAwait(false);

		/// <summary>
		/// Closes the API Gateway Router incoming channel
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static async Task CloseIncomingChannelAsync(string message = null, Action<Exception> onError = null)
		{
			try
			{
				await (Router.IncomingChannel != null ? Router.IncomingChannel.Close(message ?? "Disconnected", new GoodbyeDetails { Message = message ?? "Disconnected" }) : Task.CompletedTask).ConfigureAwait(false);
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
		#endregion

		#region Outgoging channel
		/// <summary>
		/// Opens the API Gateway Router outgoging channel
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenOutgoingChannelAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onConnectionError,
			CancellationToken cancellationToken
		) => Router.OutgoingChannel = await Router.CreateAsync(
			Router.GetRouterInfo(),
			(sender, args) =>
			{
				Router.OutgoingChannelSessionID = args.SessionId;
				onConnectionEstablished?.Invoke(sender, args);
			},
			(sender, args) =>
			{
				Router.Services.Clear();
				Router.UniqueServices.Clear();
				Router.SyncableServices.Clear();
				Router.OutgoingChannelSessionID = 0;
				onConnectionBroken?.Invoke(sender, args);
			},
			onConnectionError,
			cancellationToken
		).ConfigureAwait(false);

		/// <summary>
		/// Closes the API Gateway Router outgoing channel
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static async Task CloseOutgoingChannelAsync(string message = null, Action<Exception> onError = null)
		{
			try
			{
				await (Router.OutgoingChannel != null ? Router.OutgoingChannel.Close(message ?? "Disconnected", new GoodbyeDetails { Message = message ?? "Disconnected" }) : Task.CompletedTask).ConfigureAwait(false);
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

		#region Backup channel
		/// <summary>
		/// Opens the API Gateway Router backup channel
		/// </summary>
		/// <param name="onConnectionEstablished">The action to run when the connection is established</param>
		/// <param name="onConnectionBroken">The action to run when the connection is broken</param>
		/// <param name="onConnectionError">The action to run when the connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <returns></returns>
		public static async Task<IWampChannel> OpenBackupChannelAsync(
			Action<object, WampSessionCreatedEventArgs> onConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onConnectionError,
			CancellationToken cancellationToken
		) => Router.GotBackupRouter()
			? Router.BackupChannel = await Router.CreateAsync
			(
				Router.GetRouterInfo(true),
				(sender, args) =>
				{
					Router.BackupChannelSessionID = args.SessionId;
					onConnectionEstablished?.Invoke(sender, args);
				},
				(sender, args) =>
				{
					Router.BackupChannelSessionID = 0;
					onConnectionBroken?.Invoke(sender, args);
				},
				onConnectionError,
				cancellationToken
			).ConfigureAwait(false)
			: null;

		/// <summary>
		/// Closes the API Gateway Router backup channel
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static async Task CloseBackupChannelAsync(string message = null, Action<Exception> onError = null)
		{
			try
			{
				await (Router.BackupChannel != null ? Router.BackupChannel.Close(message ?? "Disconnected", new GoodbyeDetails { Message = message ?? "Disconnected" }) : Task.CompletedTask).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				onError?.Invoke(ex);
			}
			finally
			{
				Router.BackupChannel = null;
				Router.BackupChannelSessionID = 0;
			}
		}
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
		/// <param name="onBackupConnectionEstablished">The action to run when the backup connection is established</param>
		/// <param name="onBackupConnectionBroken">The action to run when the backup connection is broken</param>
		/// <param name="onBackupConnectionError">The action to run when the backup connection got any error</param>
		/// <param name="cancellationToken">The cancellation token</param>
		/// <param name="onError">The action to run when got any error</param>
		/// <returns></returns>
		public static async Task ConnectAsync(
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError,
			Action<object, WampSessionCreatedEventArgs> onBackupConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onBackupConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onBackupConnectionError,
			CancellationToken cancellationToken,
			Action<Exception> onError
		)
		{
			try
			{
				await Task.WhenAll
				(
					Router.OpenIncomingChannelAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, cancellationToken),
					Router.OpenOutgoingChannelAsync(onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, cancellationToken),
					Router.OpenBackupChannelAsync(onBackupConnectionEstablished, onBackupConnectionBroken, onBackupConnectionError, cancellationToken)
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
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError,
			Action<object, WampSessionCreatedEventArgs> onBackupConnectionEstablished,
			Action<object, WampSessionCloseEventArgs> onBackupConnectionBroken,
			Action<object, WampConnectionErrorEventArgs> onBackupConnectionError,
			CancellationToken cancellationToken,
			Action<Exception> onError = null
		) => Router.ConnectAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, onBackupConnectionEstablished, onBackupConnectionBroken, onBackupConnectionError, cancellationToken, onError).Execute();

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
		public static Task ConnectAsync(
			Action<object, WampSessionCreatedEventArgs> onIncomingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onIncomingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onIncomingConnectionError = null,
			Action<object, WampSessionCreatedEventArgs> onOutgoingConnectionEstablished = null,
			Action<object, WampSessionCloseEventArgs> onOutgoingConnectionBroken = null,
			Action<object, WampConnectionErrorEventArgs> onOutgoingConnectionError = null,
			CancellationToken cancellationToken = default,
			Action<Exception> onError = null
		) => Router.ConnectAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, null, null, null, cancellationToken, onError);

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
		) => Router.ConnectAsync(onIncomingConnectionEstablished, onIncomingConnectionBroken, onIncomingConnectionError, onOutgoingConnectionEstablished, onOutgoingConnectionBroken, onOutgoingConnectionError, cancellationToken, onError).Execute();

		/// <summary>
		/// Disconnects from API Gateway Router (means close all WAMP channels)
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static Task DisconnectAsync(string message = null, Action<Exception> onError = null)
		{
			Router.ChannelsAreClosedBySystem = true;
			return Task.WhenAll
			(
				Router.CloseIncomingChannelAsync(message, onError),
				Router.CloseOutgoingChannelAsync(message, onError),
				Router.CloseBackupChannelAsync(message, onError)
			);
		}

		/// <summary>
		/// Disconnects from API Gateway Router and close all WAMP channels
		/// </summary>
		/// <param name="message">The message to send to API Gateway Router before closing the channel</param>
		/// <param name="onError">The action to run when got any error</param>
		public static void Disconnect(string message = null, Action<Exception> onError = null)
			=> Router.DisconnectAsync(message, onError).Execute(true);
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
				service = Router.OutgoingChannel?.GetService<IService>(ProxyInterceptor.Create(name));
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
				service = Router.OutgoingChannel?.GetService<IUniqueService>(ProxyInterceptor.Create(name));
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

				var json = await Router.GetService(requestInfo.ServiceName).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
				onSuccess?.Invoke(requestInfo, json);

				tracker?.Invoke("Call service successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					Router.IncomingChannelSessionID > 0 ? Task.CompletedTask : Router.IncomingChannel.OpenAsync(cancellationToken),
					Router.OutgoingChannelSessionID > 0 ? Task.CompletedTask : Router.OutgoingChannel.OpenAsync(cancellationToken)
				).ConfigureAwait(false);
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				try
				{
					var json = await Router.GetService(requestInfo.ServiceName).ProcessRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
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
				service = Router.OutgoingChannel?.GetService<ISyncableService>(ProxyInterceptor.Create($"{name}.sync"));
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

				var json = await Router.GetSyncableService(requestInfo.ServiceName).SyncAsync(requestInfo, cancellationToken).ConfigureAwait(false);
				onSuccess?.Invoke(requestInfo, json);

				tracker?.Invoke("Call service [for synchronizing] successful" + "\r\n" + $"Request: {requestInfo.ToString(jsonFormat)}" + "\r\n" + $"Response: {json?.ToString(jsonFormat)}", null);
				return json;
			}
			catch (WampSessionNotEstablishedException)
			{
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					Router.IncomingChannelSessionID > 0 ? Task.CompletedTask : Router.IncomingChannel.OpenAsync(cancellationToken),
					Router.OutgoingChannelSessionID > 0 ? Task.CompletedTask : Router.OutgoingChannel.OpenAsync(cancellationToken)
				).ConfigureAwait(false);
				await Task.Delay(UtilityService.GetRandomNumber(567, 789), cancellationToken).ConfigureAwait(false);
				try
				{
					var json = await Router.GetSyncableService(requestInfo.ServiceName).SyncAsync(requestInfo, cancellationToken).ConfigureAwait(false);
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