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
		static IWampChannel _IncommingChannel = null, _OutgoingChannel = null;
		static long _IncommingChannelSessionID = 0, _OutgoingChannelSessionID = 0;
		static bool _ChannelsAreClosedBySystem = false;

		/// <summary>
		/// Gets the incomming channel of the WAMP router
		/// </summary>
		public static IWampChannel IncommingChannel => WAMPConnections._IncommingChannel;

		/// <summary>
		/// Gets the session's identity of the incomming channel of the WAMP router
		/// </summary>
		public static long IncommingChannelSessionID => WAMPConnections._IncommingChannelSessionID;

		/// <summary>
		/// Gets the outgoing channel of the WAMP router
		/// </summary>
		public static IWampChannel OutgoingChannel => WAMPConnections._OutgoingChannel;

		/// <summary>
		/// Gets the session's identity of the outgoing channel of the WAMP router
		/// </summary>
		public static long OutgoingChannelSessionID => WAMPConnections._OutgoingChannelSessionID;

		/// <summary>
		/// Gets the state that determines that the WAMP channels are closed by the system
		/// </summary>
		public static bool ChannelsAreClosedBySystem => WAMPConnections._ChannelsAreClosedBySystem;

		/// <summary>
		/// Gets information of WAMP router
		/// </summary>
		/// <returns></returns>
		public static Tuple<string, string, bool> GetRouterInfo()
			=> new Tuple<string, string, bool>(
				UtilityService.GetAppSetting("Router:Address", "ws://127.0.0.1:16429/"),
				UtilityService.GetAppSetting("Router:Realm", "VIEAppsRealm"),
				"json".IsEquals(UtilityService.GetAppSetting("Router:ChannelsMode", "MsgPack"))
			);
		#endregion

		#region Incomming
		/// <summary>
		/// Opens the incomming channel of the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (WAMPConnections._IncommingChannel != null)
				return;

			var info = WAMPConnections.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			WAMPConnections._IncommingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			WAMPConnections._IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) => WAMPConnections._IncommingChannelSessionID = args.SessionId;

			if (onConnectionEstablished != null)
				WAMPConnections._IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				WAMPConnections._IncommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				WAMPConnections._IncommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await WAMPConnections._IncommingChannel.Open().ConfigureAwait(false);
		}

		/// <summary>
		/// Closes the incomming channel of the WAMP router
		/// </summary>
		/// <param name="message">The message to send to WAMP router before closing the channel</param>
		public static void CloseIncomingChannel(string message = null)
		{
			if (WAMPConnections._IncommingChannel != null)
				try
				{
					WAMPConnections._IncommingChannel.Close(message ?? "The incoming channel is closed", new GoodbyeDetails());
					WAMPConnections._IncommingChannel = null;
					WAMPConnections._IncommingChannelSessionID = 0;
				}
				catch { }
		}
		#endregion

		#region Outgoing
		/// <summary>
		/// Opens the outgoging channel of the WAMP router
		/// </summary>
		/// <param name="onConnectionEstablished"></param>
		/// <param name="onConnectionBroken"></param>
		/// <param name="onConnectionError"></param>
		/// <returns></returns>
		public static async Task OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (WAMPConnections._OutgoingChannel != null)
				return;

			var info = WAMPConnections.GetRouterInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			WAMPConnections._OutgoingChannel = useJsonChannel
				? new DefaultWampChannelFactory().CreateJsonChannel(address, realm)
				: new DefaultWampChannelFactory().CreateMsgpackChannel(address, realm);

			WAMPConnections._OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, args) => WAMPConnections._OutgoingChannelSessionID = args.SessionId;

			if (onConnectionEstablished != null)
				WAMPConnections._OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				WAMPConnections._OutgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				WAMPConnections._OutgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await WAMPConnections._OutgoingChannel.Open().ConfigureAwait(false);
		}

		/// <summary>
		/// Closes the outgoing channel of the WAMP router
		/// </summary>
		/// <param name="message">The message to send to WAMP router before closing the channel</param>
		public static void CloseOutgoingChannel(string message = null)
		{
			if (WAMPConnections._OutgoingChannel != null)
				try
				{
					WAMPConnections._OutgoingChannel.Close(message ?? "The outgoing channel is closed", new GoodbyeDetails());
					WAMPConnections._OutgoingChannel = null;
					WAMPConnections._OutgoingChannelSessionID = 0;
				}
				catch { }
		}
		#endregion

		#region Re-open & Close
		public static void ReOpen(this IWampChannel wampChannel, Action<IWampChannel> onSuccess = null, Action<Exception> onError = null)
			=> new WampChannelReconnector(wampChannel, async () =>
			{
				try
				{
					await Task.Delay(234).ConfigureAwait(false);
					await wampChannel.Open().ConfigureAwait(false);
					onSuccess?.Invoke(wampChannel);
				}
				catch (Exception ex)
				{
					onError?.Invoke(ex);
				}
			}).Start();

		/// <summary>
		/// Closes all WAMP channels
		/// </summary>
		public static void CloseChannels()
		{
			WAMPConnections._ChannelsAreClosedBySystem = true;
			WAMPConnections.CloseIncomingChannel();
			WAMPConnections.CloseOutgoingChannel();
		}
		#endregion

		#region Get exception details of WAMP
		static JObject GetJsonException(JObject exception)
		{
			var json = new JObject
			{
				{ "Message", exception["Message"] },
				{ "Type", exception["ClassName"] },
				{ "Method", exception["ExceptionMethod"] },
				{ "Source", exception["Source"] },
				{ "Stack", exception["StackTraceString"] },
			};

			var inner = exception["InnerException"];
			if (inner != null && inner is JObject)
				json.Add(new JProperty("InnerException", WAMPConnections.GetJsonException(inner as JObject)));

			return json;
		}

		/// <summary>
		/// Gets the details of a WAMP exception
		/// </summary>
		/// <param name="exception"></param>
		/// <param name="requestInfo"></param>
		/// <returns></returns>
		public static Tuple<int, string, string, string, Exception, JObject> GetDetails(this WampException exception, RequestInfo requestInfo = null)
		{
			var code = 500;
			var message = "";
			var type = "";
			var stack = "";
			Exception inner = null;
			JObject jsonException = null;

			// unavailable
			if (exception.ErrorUri.Equals("wamp.error.no_such_procedure") || exception.ErrorUri.Equals("wamp.error.callee_unregistered"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
				{
					message = (exception.Arguments[0] as JValue).Value.ToString();
					var start = message.IndexOf("'");
					var end = message.IndexOf("'", start + 1);
					message = $"The requested service ({message.Substring(start + 1, end - start - 1).Replace("'", "")}) is unavailable";
				}
				else
					message = "The requested service is unavailable";

				type = "ServiceUnavailableException";
				stack = exception.StackTrace;
				code = 503;
			}

			// cannot serialize
			else if (exception.ErrorUri.Equals("wamp.error.invalid_argument"))
			{
				message = "Cannot serialize or deserialize one of argument objects (or child object)";
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JValue)
					message += $" => {(exception.Arguments[0] as JValue).Value}";
				type = "SerializationException";
				stack = exception.StackTrace;
			}

			// runtime error
			else if (exception.ErrorUri.Equals("wamp.error.runtime_error"))
			{
				if (exception.Arguments != null && exception.Arguments.Length > 0 && exception.Arguments[0] != null && exception.Arguments[0] is JObject)
					foreach (var info in exception.Arguments[0] as JObject)
					{
						if (info.Value != null && info.Value is JValue && (info.Value as JValue).Value != null)
							stack += (stack.Equals("") ? "" : "\r\n" + $"----- Inner ({info.Key}) --------------------" + "\r\n")
								+ (info.Value as JValue).Value.ToString();
					}

				if (requestInfo == null && exception.Arguments != null && exception.Arguments.Length > 2 && exception.Arguments[2] != null && exception.Arguments[2] is JObject)
				{
					var info = (exception.Arguments[2] as JObject).First;
					if (info != null && info is JProperty && (info as JProperty).Name.Equals("RequestInfo") && (info as JProperty).Value != null && (info as JProperty).Value is JObject)
						requestInfo = ((info as JProperty).Value as JToken).FromJson<RequestInfo>();
				}

				jsonException = exception.Arguments != null && exception.Arguments.Length > 4 && exception.Arguments[4] != null && exception.Arguments[4] is JObject
					? WAMPConnections.GetJsonException(exception.Arguments[4] as JObject)
					: null;

				message = jsonException != null
					? (jsonException["Message"] as JValue).Value.ToString()
					: $"Error occurred at \"net.vieapps.services.{(requestInfo != null ? requestInfo.ServiceName.ToLower() : "unknown")}\"";

				type = jsonException != null
					? (jsonException["Type"] as JValue).Value.ToString().ToArray('.').Last()
					: "ServiceOperationException";

				inner = exception;
			}

			// unknown
			else
			{
				message = exception.Message;
				type = exception.GetType().GetTypeName(true);
				stack = exception.StackTrace;
				inner = exception.InnerException;
			}

			return new Tuple<int, string, string, string, Exception, JObject>(code, message, type, stack, inner, jsonException);
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
					service = WAMPConnections._OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
					WAMPConnections._Services.TryAdd(name, service);
				}
			}
			return service;
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