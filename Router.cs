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
		/// Gets or sets identity of the node that runs or host the services
		/// </summary>
		public static string NodeID { get; set; }

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
		#endregion

		#region Router info
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

		internal static WebSocket RouterWebSocket { get; } = new WebSocket
		{
			OnConnectionBroken = websocket =>
			{
				var isBackup = false;
				if (Router.RouterBackupWebSocketID.Equals(websocket.ID))
				{
					Router.RouterBackupWebSocketID = Guid.Empty;
					Router.RouterBackupWebSocketState = "closed";
					isBackup = true;
				}
				else
				{
					Router.RouterPrimaryWebSocketID = Guid.Empty;
					Router.RouterPrimaryWebSocketState = "closed";
				}
				Router.ConnectRouterWebSocket(null, isBackup);
			},
			OnMessageReceived = (websocket, _, buffer) => Router.OnRouterWebSocketMessageReceived?.Invoke(websocket, buffer.GetString())
		};

		/// <summary>
		/// Gets or Sets the action to process when receive messages from Router 
		/// </summary>
		public static Action<ManagedWebSocket, string> OnRouterWebSocketMessageReceived { get; set; }

		/// <summary>
		/// Gets the identity of the web socket that connected to primary router
		/// </summary>
		public static Guid RouterPrimaryWebSocketID { get; internal set; } = Guid.Empty;

		internal static string RouterPrimaryWebSocketState { get; set; } = "initializing";

		/// <summary>
		/// Gets the identity of the web socket that connected to primary router
		/// </summary>
		public static Guid RouterBackupWebSocketID { get; internal set; } = Guid.Empty;

		internal static string RouterBackupWebSocketState { get; set; } = "initializing";

		static void ConnectRouterWebSocket(ILogger logger = null, bool isBackup = false)
		{
			var uri = new Uri(Router.GetRouterStrInfo(isBackup));
			var location = $"{uri.Scheme}://{uri.Host}:56429/";
			var doConnect = isBackup
				? Router.RouterBackupWebSocketID.Equals(Guid.Empty) && (Router.RouterBackupWebSocketState == "initializing" || Router.RouterBackupWebSocketState == "closed")
				: Router.RouterPrimaryWebSocketID.Equals(Guid.Empty) && (Router.RouterPrimaryWebSocketState == "initializing" || Router.RouterPrimaryWebSocketState == "closed");

			if (doConnect)
			{
				logger?.LogInformation($"Connect to Router websocket [{location}]");
				if (isBackup)
					Router.RouterBackupWebSocketState = "connecting";
				else
					Router.RouterPrimaryWebSocketState = "connecting";

				Router.RouterWebSocket.Connect
				(
					location,
					websocket =>
					{
						if (isBackup)
						{
							Router.RouterBackupWebSocketID = websocket.ID;
							Router.RouterBackupWebSocketState = "connected";
						}
						else
						{
							Router.RouterPrimaryWebSocketID = websocket.ID;
							Router.RouterPrimaryWebSocketState = "connected";
						}
						logger?.LogInformation($"Router websocket was connected [{location} => {websocket.ID} ({isBackup})]");
					},
					exception =>
					{
						logger?.LogInformation($"Cannot connect to Router websocket => {exception.Message}", exception);
						Router.ConnectRouterWebSocketAsync(logger, isBackup).Execute();
					}
				);
			}
		}

		static async Task ConnectRouterWebSocketAsync(ILogger logger = null, bool isBackup = false)
		{
			await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
			Router.ConnectRouterWebSocket(logger, isBackup);
		}

		/// <summary>
		/// Gets the router's statistics web-socket (means the WebSocket connection that connected to API Gateway Router to exchange information by specified commands)
		/// </summary>
		/// <param name="isBackup"></param>
		/// <returns></returns>
		public static ManagedWebSocket GetRouterWebSocket(bool isBackup = false)
			=> Router.RouterWebSocket.GetWebSocket(isBackup ? Router.RouterBackupWebSocketID : Router.RouterPrimaryWebSocketID);

		/// <summary>
		/// Sends a message to Router for exchanging information
		/// </summary>
		/// <param name="message"></param>
		/// <param name="isBackup"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public static Task SendMessageToRouterAsync(string message, bool isBackup = false, CancellationToken cancellationToken = default)
		{
			var routerWebSocket = Router.GetRouterWebSocket(isBackup);
			return routerWebSocket != null && routerWebSocket.State == System.Net.WebSockets.WebSocketState.Open
				? routerWebSocket.SendAsync(message, cancellationToken)
				: Task.CompletedTask;
		}
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

		/// <summary>
		/// Updates related information of the channel
		/// </summary>
		/// <param name="wampChannel"></param>
		/// <param name="sessionID"></param>
		/// <param name="name"></param>
		/// <param name="description"></param>
		public static async Task UpdateAsync(this IWampChannel wampChannel, long sessionID, string name, string description, ILogger logger = null, bool isBackup = false)
		{
			Router.ConnectRouterWebSocket(logger, isBackup);
			var state = isBackup ? Router.RouterBackupWebSocketState : Router.RouterPrimaryWebSocketState;
			while (state == null || state == "initializing" || state == "connecting")
			{
				await Task.Delay(UtilityService.GetRandomNumber(234, 567)).ConfigureAwait(false);
				state = isBackup ? Router.RouterBackupWebSocketState : Router.RouterPrimaryWebSocketState;
			}

			try
			{
				await Router.SendMessageToRouterAsync(new JObject
				{
					["Command"] = "Update",
					["SessionID"] = sessionID,
					["Name"] = name,
					["Description"] = description
				}.AsString(), isBackup).ConfigureAwait(false);
				logger?.LogInformation($"Update connection info with Router successful [{sessionID} @ {name} - {description}]");
			}
			catch (Exception ex)
			{
				logger?.LogInformation($"Cannot update connection info with Router => {ex.Message}", ex);
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
			Router.ConnectRouterWebSocket();
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
		public static async Task DisconnectAsync(string message = null, Action<Exception> onError = null)
		{
			Router.ChannelsAreClosedBySystem = true;
			await Task.WhenAll
			(
				Router.CloseIncomingChannelAsync(message, onError),
				Router.CloseOutgoingChannelAsync(message, onError),
				Router.CloseBackupChannelAsync(message, onError)
			).ConfigureAwait(false);
			await Router.RouterWebSocket.DisposeAsync().ConfigureAwait(false);
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

		internal static ConcurrentDictionary<string, IUniqueService> UniqueServices { get; } = new ConcurrentDictionary<string, IUniqueService>(StringComparer.OrdinalIgnoreCase);

		internal static ConcurrentDictionary<string, ISyncableService> SyncableServices { get; } = new ConcurrentDictionary<string, ISyncableService>(StringComparer.OrdinalIgnoreCase);

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

			return service ?? throw new ServiceNotFoundException($"The service \"{name.ToLower()}\" is not found");
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

	/// <summary>
	/// Presents the RPC Gate (Router's Admission Control) 
	/// </summary>
	public sealed class RouterRpcGate
	{

		#region Gate's data
		int _hardMax;
		int _softMax;
		int _inflight;

		readonly SemaphoreSlim _semaphore;
		readonly int _timeoutMilliseconds;

		readonly object _locker = new object();
		readonly int _increaseIntervalMilliseconds = 150;
		readonly int _cooldownMilliseconds = 2000;
		readonly int _increaseStep = 1;

		int _ticking;
		Timer _timer;

		long _lastIncreaseTicks;
		long _lastDecreaseTicks;

		public readonly struct Releaser : IDisposable
		{
			readonly RouterRpcGate _rpcgate;
			readonly int _weight;
			readonly bool _usedSemaphore;

			internal Releaser(RouterRpcGate rpcgate, int weight, bool usedSemaphore)
			{
				this._rpcgate = rpcgate;
				this._weight = weight;
				this._usedSemaphore = usedSemaphore;
			}

			public void Dispose() => this._rpcgate?.Release(this._weight, this._usedSemaphore);
		}

		public int Current => Volatile.Read(ref this._inflight);

		public int Max => Volatile.Read(ref this._softMax);

		public int Available => this.Max - this.Current;

		public double Usage => (double)this.Current / Math.Max(1, this.Max);
		#endregion

		public RouterRpcGate(int max, int timeoutMilliseconds = 50)
		{
			if (max <= 0)
				throw new ArgumentOutOfRangeException(nameof(max));

			if (timeoutMilliseconds < 0)
				throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

			this._hardMax = max;
			this._softMax = max;
			this._timeoutMilliseconds = timeoutMilliseconds;
			this._semaphore = new SemaphoreSlim(max, Int32.MaxValue);
		}

		public ValueTask<Releaser?> TryEnterAsync(CancellationToken cancellationToken = default)
			=> this.TryEnterAsync(1, cancellationToken);

		public async ValueTask<Releaser?> TryEnterAsync(int weight, CancellationToken cancellationToken)
		{
			if (weight <= 0)
				throw new ArgumentOutOfRangeException(nameof(weight));

			while (true)
			{
				var current = Volatile.Read(ref this._inflight);
				var capacity = Volatile.Read(ref this._softMax);
				if (weight > capacity - current)
					break;

				if (Interlocked.CompareExchange(ref this._inflight, current + weight, current) == current)
					return new Releaser(this, weight, false);
			}

			if (this._timeoutMilliseconds <= 0)
				return null;

			if (!await this._semaphore.WaitAsync(this._timeoutMilliseconds, cancellationToken).ConfigureAwait(false))
				return null;

			while (true)
			{
				var current = Volatile.Read(ref this._inflight);
				var capacity = Volatile.Read(ref this._softMax);
				if (weight > capacity - current)
				{
					this._semaphore.Release();
					return null;
				}

				if (Interlocked.CompareExchange(ref this._inflight, current + weight, current) == current)
					return new Releaser(this, weight, true);
			}
		}

		internal void Release(int weight, bool usedSemaphore)
		{
			Interlocked.Add(ref this._inflight, -weight);
			if (usedSemaphore)
				this._semaphore.Release();
		}

		#region Gate's Capacity
		public void SetMaxCapacity(int newMaxCapacity)
		{
			if (newMaxCapacity <= 0)
				throw new ArgumentOutOfRangeException(nameof(newMaxCapacity));

			Volatile.Write(ref this._hardMax, newMaxCapacity);
			var softMax = Volatile.Read(ref this._softMax);
			if (newMaxCapacity < softMax)
			{
				Volatile.Write(ref this._softMax, newMaxCapacity);
#if NETSTANDARD2_0
				Volatile.Write(ref this._lastDecreaseTicks, (long)Environment.TickCount);
#else
				Volatile.Write(ref this._lastDecreaseTicks, Environment.TickCount64);
#endif
			}
			else if (newMaxCapacity > softMax)
				this.StartTimer();
		}

		void StartTimer()
		{
			var timer = Volatile.Read(ref this._timer);
			if (timer == null)
			{
				lock (this._locker)
				{
					if (this._timer == null)
						this._timer = new Timer(_ => this.OnTimerTick(), null, this._increaseIntervalMilliseconds, this._increaseIntervalMilliseconds);
				}
			}
		}

		void StopTimer()
		{
			lock (this._locker)
			{
				var timer = this._timer;
				this._timer = null;
				timer?.Dispose();
			}
		}

		void OnTimerTick()
		{
			if (Interlocked.Exchange(ref this._ticking, 1) == 1)
				return;

			try
			{
#if NETSTANDARD2_0
				var now = (long)Environment.TickCount;
#else
				var now = Environment.TickCount64;
#endif
				var hardMax = Volatile.Read(ref this._hardMax);
				var softMax = Volatile.Read(ref this._softMax);

				if (softMax >= hardMax)
				{
					this.StopTimer();
					return;
				}

				if (!RouterRpcGate.Elapsed(now, Volatile.Read(ref this._lastDecreaseTicks), this._cooldownMilliseconds))
					return;

				if (!RouterRpcGate.Elapsed(now, Volatile.Read(ref this._lastIncreaseTicks), this._increaseIntervalMilliseconds))
					return;

				var inflight = Volatile.Read(ref this._inflight);
				if (inflight >= (int)(softMax * 0.8))
					return;

				var next = Math.Min(hardMax, softMax + this._increaseStep);
				Volatile.Write(ref this._softMax, next);
				Volatile.Write(ref this._lastIncreaseTicks, now);
			}
			finally
			{
				this._ticking = 0;
			}
		}

#if NETSTANDARD2_0
		static bool Elapsed(long now, long last, int milliseconds) => unchecked((int)(now - last)) >= milliseconds;
#else
		static bool Elapsed(long now, long last, int milliseconds) => (now - last) >= milliseconds;
#endif
		#endregion

	}
}