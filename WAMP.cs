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

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("VIEApps.Services.Base.Http")]
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
		public static IWampChannel IncommingChannel { get; internal set; }

		/// <summary>
		/// Gets the session's identity of the incomming channel of the WAMP router
		/// </summary>
		public static long IncommingChannelSessionID { get; internal set; }

		/// <summary>
		/// Gets the outgoing channel of the WAMP router
		/// </summary>
		public static IWampChannel OutgoingChannel { get; internal set; }

		/// <summary>
		/// Gets the session's identity of the outgoing channel of the WAMP router
		/// </summary>
		public static long OutgoingChannelSessionID { get; internal set; }

		/// <summary>
		/// Gets the state that determines that the WAMP channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem { get; internal set; }

		/// <summary>
		/// Gets information of WAMP router
		/// </summary>
		/// <returns></returns>
		public static Tuple<string, string, bool> GetRouterInfo()
			=> new Tuple<string, string, bool>
			(
				UtilityService.GetAppSetting("Router:Address", "ws://127.0.0.1:16429/"),
				UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm"),
				"json".IsEquals(UtilityService.GetAppSetting("Router:ChannelsMode", "MsgPack"))
			);
		#endregion

		#region Open
		/// <summary>
		/// Opens the incomming channel of the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (WAMPConnections.IncommingChannel != null)
				return;

			var info = WAMPConnections.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			WAMPConnections.IncommingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			WAMPConnections.IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) =>
			{
				WAMPConnections.IncommingChannelSessionID = args.SessionId;
				WAMPConnections.ChannelsAreClosedBySystem = false;
			};

			if (onConnectionEstablished != null)
				WAMPConnections.IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				WAMPConnections.IncommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				WAMPConnections.IncommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await WAMPConnections.IncommingChannel.Open().ConfigureAwait(false);
		}

		/// <summary>
		/// Opens the outgoging channel of the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (WAMPConnections.OutgoingChannel != null)
				return;

			var info = WAMPConnections.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			WAMPConnections.OutgoingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			WAMPConnections.OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) =>
			{
				WAMPConnections.OutgoingChannelSessionID = args.SessionId;
				WAMPConnections.ChannelsAreClosedBySystem = false;
			};

			if (onConnectionEstablished != null)
				WAMPConnections.OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				WAMPConnections.OutgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				WAMPConnections.OutgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await WAMPConnections.OutgoingChannel.Open().ConfigureAwait(false);
		}
		#endregion

		#region Re-open
		static void ReOpenChannel(this IWampChannel wampChannel, Action<IWampChannel> onSuccess, Action<Exception> onError, CancellationToken cancellationToken, int attempts, int minDelay = 234, int maxDelay = 567)
			=> new WampChannelReconnector(wampChannel, async () =>
			{
				try
				{
					await Task.Delay(UtilityService.GetRandomNumber(minDelay, maxDelay), cancellationToken).ConfigureAwait(false);
					await wampChannel.Open().WithCancellationToken(cancellationToken).ConfigureAwait(false);
					onSuccess?.Invoke(wampChannel);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					if (attempts < 13)
					{
						var reopen = Task.Run(() => wampChannel.ReOpenChannel(onSuccess, onError, cancellationToken, attempts + 1, minDelay + ((attempts + 1) * 13), maxDelay + ((attempts + 1) * 13))).ConfigureAwait(false);
					}
					else
						onError?.Invoke(ex);
				}
			}).Start();

		public static void ReOpenChannel(this IWampChannel wampChannel, Action<IWampChannel> onSuccess = null, Action<Exception> onError = null, CancellationToken cancellationToken = default(CancellationToken))
			=> wampChannel.ReOpenChannel(onSuccess, onError, cancellationToken, 0);
		#endregion

		#region Close
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

		#region Call services
		internal static ConcurrentDictionary<string, IService> _Services = new ConcurrentDictionary<string, IService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Gets a service by name
		/// </summary>
		/// <param name="name">The string that presents name of a service</param>
		/// <returns></returns>
		public static async Task<IService> GetServiceAsync(string name)
		{
			IService service = null;
			if (!string.IsNullOrWhiteSpace(name) && !WAMPConnections._Services.TryGetValue(name, out service))
			{
				await WAMPConnections.OpenOutgoingChannelAsync().ConfigureAwait(false);
				if (!WAMPConnections._Services.TryGetValue(name, out service))
				{
					service = WAMPConnections.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
					WAMPConnections._Services.TryAdd(name, service);
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